using System.Text.Json;
using System.Text.Json.Nodes;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;

namespace Backdafuckup.Server.Sql;

/// <summary>
/// What a server restart leaves of a SQL Server restore: the runner keeps the files it plans to write with the session
/// (paths only, no secrets), and the startup recovery names them and says what to check.
/// </summary>
public static class SqlRestoreRecovery
{
    private const string PlannedFiles = "PlannedFiles";

    /// <summary>The session parameters with the files the agent writes (target paths, or the copies beside the database's files).</summary>
    public static string WithPlannedFiles(string? parameters, IReadOnlyList<string> files)
    {
        var node = (parameters is null ? null : JsonNode.Parse(parameters) as JsonObject) ?? new JsonObject();
        node[PlannedFiles] = new JsonArray([.. files.Select(f => (JsonNode?)JsonValue.Create(f))]);
        return node.ToJsonString();
    }

    /// <summary>
    /// The prefix of a SQL session's log lines: the backed-up VM, and the target VM if it is another one
    /// (<c>bdfu-lab-sql1 → BDFU-LAB-SQL2</c>), so the lines do not seem to happen in the backed-up VM.
    /// </summary>
    public static async Task<string> LogPrefixAsync(Agents.AgentService agents, string sourceVmName, string sourceVmNativeId, string targetVmNativeId, CancellationToken cancellationToken)
    {
        if (string.Equals(sourceVmNativeId, targetVmNativeId, StringComparison.OrdinalIgnoreCase))
        {
            return sourceVmName;
        }

        var target = await agents.FindForVmAsync(targetVmNativeId, cancellationToken).ConfigureAwait(false);
        return $"{sourceVmName} → {target?.HostName ?? "another VM"}";
    }

    /// <summary>The summary of a restore session that a server restart interrupted.</summary>
    public static string Summary(string? parameters)
    {
        SqlRestoreRequestDto? request = null;
        List<string> files = [];
        try
        {
            request = parameters is null ? null : JsonSerializer.Deserialize<SqlRestoreRequestDto>(parameters);
            if (parameters is not null && JsonNode.Parse(parameters)?[PlannedFiles] is JsonArray planned)
            {
                files = [.. planned.Select(f => f?.GetValue<string>()).OfType<string>()];
            }
        }
        catch (JsonException)
        {
        }

        if (request is null)
        {
            return "The backup service was restarted while this SQL Server restore was running.";
        }

        var name = SqlCatalog.QuoteName(request.DatabaseName);
        var where = $"instance {request.TargetInstance} of the target VM";
        if (files.Count == 0)
        {
            return $"The backup service was restarted before this restore of {name} to {where} wrote any file; nothing was changed.";
        }

        return request.Overwrite
            ? $"The backup service was restarted during the overwrite of {name} on {where}. If the agent had begun to swap the files, it finished or rolled back " +
              $"the overwrite on its own: check that {name} is online. Restored copies may lie beside its files; delete them once it is online: {string.Join(", ", files)} " +
              $"(and *{SqlReplaceNames.ReplacedSuffix} files, if the overwrite finished)."
            : $"The backup service was restarted during the restore of {name} to {where}. Check whether {name} is attached there. If it is not, delete the files " +
              $"this restore wrote (it never replaces existing files): {string.Join(", ", files)}.";
    }
}
