using System.Net;
using SMBLibrary;
using SMBLibrary.Client;

namespace Backdafuckup.Smb;

/// <summary>Connects (SMB 2/3, port 445) and logs on with NTLMv2; errors name the server, its address and the account.</summary>
internal static class SmbLogon
{
    /// <param name="server">Host name or address as the user wrote it.</param>
    /// <param name="display">How the server appears in messages, e.g. <c>fs01 (10.0.0.5)</c>.</param>
    /// <param name="ip">Resolved address, or null to let SMBLibrary resolve <paramref name="server"/>.</param>
    public static SMB2Client Open(string server, string display, IPAddress? ip, string userName, string password, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(userName);
        var (domain, user) = SmbRemoteWindowsConnector.SplitUser(userName, server);
        var client = new SMB2Client((int)timeout.TotalMilliseconds, enableSMB311Support: true);
        var connected = ip is not null
            ? client.Connect(ip, SMBTransportType.DirectTCPTransport)
            : IPAddress.TryParse(server, out var parsed)
                ? client.Connect(parsed, SMBTransportType.DirectTCPTransport)
                : client.Connect(server, SMBTransportType.DirectTCPTransport);
        if (!connected)
        {
            throw new IOException($"No SMB connection to {display}:445 (firewall, or no file sharing on that computer).");
        }

        var status = client.Login(domain, user, password ?? string.Empty, AuthenticationMethod.NTLMv2);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            client.Disconnect();
            var reason = status switch
            {
                NTStatus.STATUS_LOGON_FAILURE => "wrong user name or password",
                NTStatus.STATUS_ACCOUNT_RESTRICTION or NTStatus.STATUS_ACCOUNT_DISABLED => "the account is restricted or disabled",
                NTStatus.STATUS_ACCESS_DENIED => "access denied (NTLM may be blocked by policy)",
                _ => status.ToString(),
            };
            throw new UnauthorizedAccessException($"Logon as {userName} at {display}:445 failed: {reason}.");
        }

        return client;
    }
}
