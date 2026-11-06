using Backdafuckup.Contracts;
using MudBlazor;

namespace Backdafuckup.Web.Services;

/// <summary>What a SQL Server dialog starts: a restore (new database), an overwrite, a publish or an export.</summary>
public enum SqlTargetMode
{
    Restore,
    Overwrite,
    Publish,
    Export,
}

/// <summary>
/// The checks before a SQL Server restore, overwrite, publish or export starts, after Connect: shared by the SQL browser
/// (restore points) and the database page (points in time), so both dialogs refuse the same things the agent would.
/// </summary>
public static class SqlTargetChecks
{
    /// <summary>512 MB stay free on a volume the restored files go to.</summary>
    public const long VolumeReserve = 512L << 20;

    public static int? Major(string? version) =>
        version?.Split('.') is [var first, ..] && int.TryParse(first, out var major) ? major : null;

    /// <summary>The account cannot connect to the instance (then nothing else is checked).</summary>
    public static string? Connection(SqlTargetDto target, SqlTargetInstanceDto instance) =>
        instance.Connected ? null : $"{target.Account} cannot connect to instance {instance.Name}: {instance.ConnectError ?? "unknown error"}";

    /// <summary>The rights the account needs: sysadmin to overwrite or export, dbcreator or sysadmin otherwise.</summary>
    public static IEnumerable<(Severity Severity, string Text)> Rights(SqlTargetDto target, SqlTargetInstanceDto instance, SqlTargetMode mode)
    {
        var login = instance.LoginName ?? target.Account;
        switch (mode)
        {
            case SqlTargetMode.Overwrite when instance.IsSysadmin != true:
                yield return (Severity.Error, $"{login} is not sysadmin on instance {instance.Name}; overwriting a database needs sysadmin.");
                break;
            case SqlTargetMode.Export when instance.IsSysadmin != true:
                // dbcreator can attach, but the attached database keeps its original owner inside, so it cannot be opened (or backed up) by the account.
                yield return (Severity.Error, $"{login} is not sysadmin on instance {instance.Name}: it could attach the database, but not back it up " +
                    "(SQL Server keeps the database's original owner inside it, so only a sysadmin can open it). Choose a sysadmin account.");
                break;
            case SqlTargetMode.Restore or SqlTargetMode.Publish when instance.IsSysadmin != true && instance.IsDbCreator != true:
                yield return (Severity.Error, $"{login} may not create databases on instance {instance.Name}; it needs the server role dbcreator or sysadmin.");
                break;
            case SqlTargetMode.Restore or SqlTargetMode.Publish when instance.IsSysadmin != true:
                yield return (Severity.Info, $"{login} is dbcreator, not sysadmin: it attaches the database, but cannot open it afterwards, because SQL Server " +
                    "keeps the database's original owner inside it. Sysadmins (and the original owner) can; the session log names the ALTER AUTHORIZATION that gives it to this account.");
                break;
        }
    }

    /// <summary>A new database's name: given, at most 128 characters, and not taken on the instance.</summary>
    public static string? NewName(SqlTargetInstanceDto instance, string name) =>
        name.Length is 0 or > 128 ? "Enter a database name (at most 128 characters)."
        : instance.Databases.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) ? $"Instance {instance.Name} has a database {name} already; choose another name."
        : null;

    /// <summary>
    /// SQL Server takes a database only on the same or a newer version: an older instance is an error, a newer one a note
    /// (it upgrades the database; for a publish the upgrade lands in the overlay).
    /// </summary>
    public static (Severity Severity, string Text)? Version(string? sourceProduct, string? sourceVersion, SqlTargetInstanceDto instance, SqlTargetMode mode)
    {
        if (Major(sourceVersion) is not { } from || Major(instance.ProductVersion ?? instance.SetupVersion) is not { } to || from == to)
        {
            return null;
        }

        var source = sourceProduct ?? $"SQL Server {from}.0";
        var target = instance.Product ?? $"SQL Server {to}.0";
        return to < from
            ? (Severity.Error, $"The database comes from {source}; instance {instance.Name} is {target}. SQL Server takes databases only on the same or a newer version.")
            : (Severity.Info, mode switch
            {
                SqlTargetMode.Export => $"{target} upgrades the database from {source} when it attaches it: the .bak can then be restored only on {target} or newer. Use an instance of {source} for a .bak of the original version.",
                SqlTargetMode.Publish => $"{target} upgrades the database from {source} when it attaches it; the upgrade lands in the overlay, the backup stays as it is.",
                _ => $"{target} upgrades the database from {source} when it restores it; afterwards it runs only on {target} or newer.",
            });
    }

    /// <summary>SQL Server Express takes databases up to 10 GB of data files (50 GB from SQL Server 2025).</summary>
    public static string? ExpressLimit(SqlTargetInstanceDto instance, long dataBytes)
    {
        if (instance.EngineEdition != 4)
        {
            return null;
        }

        var limit = (Major(instance.ProductVersion ?? instance.SetupVersion) ?? 0) >= 17 ? 50L << 30 : 10L << 30;
        return dataBytes > limit ? $"Instance {instance.Name} is SQL Server Express: databases up to {Fmt.Bytes(limit)}; this one has {Fmt.Bytes(dataBytes)} of data files." : null;
    }

    /// <summary>
    /// Drives of the target paths that the target VM does not have as local fixed volumes, or without room for the files
    /// (512 MB stay free), as the server checks them before it writes anything.
    /// </summary>
    public static IEnumerable<string> Volumes(IEnumerable<(string TargetPath, long? SizeBytes)> files, SqlTargetDto target)
    {
        foreach (var drive in files.Where(f => f.TargetPath.Length >= 3).GroupBy(f => f.TargetPath[..3], StringComparer.OrdinalIgnoreCase))
        {
            var needed = drive.Sum(f => f.SizeBytes ?? 0);
            var volume = target.Volumes.FirstOrDefault(v => string.Equals(v.Root, drive.Key, StringComparison.OrdinalIgnoreCase));
            if (volume is null)
            {
                yield return $"{drive.Key} is not a local fixed volume of {target.Host}: choose the instance's default folders or other folders.";
            }
            else if (volume.FreeBytes < needed + VolumeReserve)
            {
                yield return $"{drive.Key} on {target.Host} has {Fmt.Bytes(volume.FreeBytes)} free; the files need {Fmt.Bytes(needed)} (512 MB stay free).";
            }
        }
    }
}
