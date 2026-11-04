using System;

namespace Backdafuckup.Agent.Core;

/// <summary>Account names as users enter them, made usable for Windows' name lookup.</summary>
public static class AccountNames
{
    /// <summary>
    /// <c>.\user</c> (a local account, as LogonUser accepts it) becomes <c>MACHINE\user</c>: the name lookup behind
    /// NTAccount.Translate does not know ".". Other forms (<c>DOMAIN\user</c>, <c>user@domain</c>, <c>NT SERVICE\…</c>) stay.
    /// </summary>
    public static string Qualify(string account, string machineName)
    {
        if (account is null)
        {
            throw new ArgumentNullException(nameof(account));
        }

        return account.StartsWith(@".\", StringComparison.Ordinal) ? machineName + account.Substring(1) : account;
    }
}
