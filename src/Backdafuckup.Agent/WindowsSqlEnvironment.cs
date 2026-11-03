using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.Common;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
#if NET48
using System.Data.SqlClient;
#else
using Microsoft.Data.SqlClient;
#endif

namespace Backdafuckup.Agent;

/// <summary>
/// The local SQL Server installation: instances from the registry (64-bit view), service state and account from the
/// service manager, connections over shared memory. A Windows account logs on as a batch job (or interactively, with
/// the elevated linked token, so UAC does not filter the Administrators group); SYSTEM holds the privileges for that.
/// </summary>
internal sealed class WindowsSqlEnvironment : ISqlEnvironment
{
    private const string SqlRoot = @"SOFTWARE\Microsoft\Microsoft SQL Server";

    public List<SqlInstanceInfo> Instances()
    {
        var result = new List<SqlInstanceInfo>();
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var names = hklm.OpenSubKey(SqlRoot + @"\Instance Names\SQL"))
        {
            if (names is null)
            {
                return result;
            }

            foreach (var name in names.GetValueNames())
            {
                var id = names.GetValue(name) as string;
                var service = string.Equals(name, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "MSSQLSERVER" : "MSSQL$" + name;
                var instance = new SqlInstanceInfo { Name = name, InstanceId = id, ServiceName = service };
                if (id is not null)
                {
                    using (var setup = hklm.OpenSubKey($@"{SqlRoot}\{id}\Setup"))
                    using (var server = hklm.OpenSubKey($@"{SqlRoot}\{id}\MSSQLServer"))
                    {
                        instance.SetupVersion = setup?.GetValue("Version") as string;
                        instance.SetupEdition = setup?.GetValue("Edition") as string;
                        var dataRoot = setup?.GetValue("SQLDataRoot") as string;
                        instance.DefaultDataFolder = server?.GetValue("DefaultData") as string ?? (dataRoot is null ? null : dataRoot.TrimEnd('\\') + @"\DATA");
                        instance.DefaultLogFolder = server?.GetValue("DefaultLog") as string ?? instance.DefaultDataFolder;
                        instance.BackupFolder = server?.GetValue("BackupDirectory") as string;
                    }
                }

                using (var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service))
                {
                    instance.ServiceAccount = key?.GetValue("ObjectName") as string;
                }

                try
                {
                    using (var controller = new ServiceController(service))
                    {
                        instance.ServiceState = controller.Status.ToString();
                    }
                }
                catch (InvalidOperationException)
                {
                    instance.ServiceState = null;
                }

                result.Add(instance);
            }
        }

        return result;
    }

    public List<SqlVolumeInfo> Volumes()
    {
        var result = new List<SqlVolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    result.Add(new SqlVolumeInfo { Root = drive.Name, Label = drive.VolumeLabel, FreeBytes = drive.AvailableFreeSpace, TotalBytes = drive.TotalSize });
                }
            }
            catch (IOException)
            {
            }
        }

        return result;
    }

    public DbConnection Open(string instance, SqlAccount account)
    {
        var builder = new SqlConnectionStringBuilder
        {
            // Shared memory: local only, needs neither TCP nor the SQL Browser service.
            DataSource = string.Equals(instance, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "lpc:." : @"lpc:.\" + instance,
            Pooling = false,
            ConnectTimeout = 15,
            ApplicationName = "backdafuckup agent",
            TrustServerCertificate = true,
        };
        builder["Encrypt"] = "false";
        var where = $"SQL Server instance {instance} on {Environment.MachineName}";
        if (account.Kind == SqlAccountKinds.Sql)
        {
            builder.UserID = account.UserName;
            builder.Password = account.Password;
            return OpenAs(builder.ConnectionString, where, $"SQL login {account.UserName}");
        }

        builder.IntegratedSecurity = true;
        using (var token = WindowsLogon.LogOn(account.UserName, account.Password))
        {
            DbConnection? connection = null;
            WindowsIdentity.RunImpersonated(token, () => connection = OpenAs(builder.ConnectionString, where, $"Windows account {account.UserName}"));
            return connection!;
        }
    }

    private static DbConnection OpenAs(string connectionString, string where, string who)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch (SqlException ex)
        {
            connection.Dispose();
            throw new InvalidOperationException($"{who} could not connect to {where}: {ex.Message} (error {ex.Number}).", ex);
        }
    }
}

/// <summary>
/// Logs a Windows account on for impersonation: as a batch job (full token, no UAC filtering), or if that right is
/// missing interactively, then with the elevated linked token. The agent runs as Local System, which holds the
/// SeTcbPrivilege needed for a usable linked token.
/// </summary>
internal static class WindowsLogon
{
    private const int LogonInteractive = 2;
    private const int LogonBatch = 4;
    private const int ProviderDefault = 0;
    private const int TokenElevationType = 18;
    private const int TokenLinkedToken = 19;
    private const int ElevationTypeLimited = 3;
    private const int ErrorLogonTypeNotGranted = 1385;

    public static SafeAccessTokenHandle LogOn(string account, string password)
    {
        var (user, domain) = Split(account);
        if (LogonUser(user, domain, password, LogonBatch, ProviderDefault, out var token))
        {
            return Elevated(token);
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorLogonTypeNotGranted && LogonUser(user, domain, password, LogonInteractive, ProviderDefault, out token))
        {
            return Elevated(token);
        }

        if (error == ErrorLogonTypeNotGranted)
        {
            error = Marshal.GetLastWin32Error();
        }

        var reason = error switch
        {
            1326 => "the user name or password is wrong",
            1327 => "account restrictions (e.g. a blank password or logon hours)",
            1330 => "the password has expired",
            1331 => "the account is disabled",
            ErrorLogonTypeNotGranted => "the account may neither log on as a batch job nor locally on this computer",
            _ => new Win32Exception(error).Message,
        };
        throw new UnauthorizedAccessException($"The Windows account {account} cannot log on to {Environment.MachineName}: {reason} (error {error}).");
    }

    /// <summary>A UAC-filtered token is replaced by its elevated linked token.</summary>
    private static SafeAccessTokenHandle Elevated(SafeAccessTokenHandle token)
    {
        var size = Marshal.SizeOf<int>();
        var buffer = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            if (GetTokenInformation(token, TokenElevationType, buffer, size, out _) && Marshal.ReadInt32(buffer) == ElevationTypeLimited
                && GetTokenInformation(token, TokenLinkedToken, buffer, IntPtr.Size, out _))
            {
                var linked = new SafeAccessTokenHandle(Marshal.ReadIntPtr(buffer));
                token.Dispose();
                return linked;
            }

            return token;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>DOMAIN\user, .\user (local) or user@domain (UPN: domain null).</summary>
    private static (string User, string? Domain) Split(string account)
    {
        var slash = account.IndexOf('\\');
        if (slash > 0)
        {
            return (account.Substring(slash + 1), account.Substring(0, slash));
        }

        return account.IndexOf('@') > 0 ? (account, null) : (account, ".");
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string? domain, string password, int logonType, int provider, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returnLength);
}
