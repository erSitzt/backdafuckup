using Backdafuckup.Web.Services;
using MudBlazor;

namespace Backdafuckup.Web.Components;

/// <summary>
/// The small chips that sum up what a restore point holds and how consistent it is, the same in the list of a VM's restore
/// points and in its row of Backups &amp; Restore: application- or crash-consistent (or RAM for a snapshot with memory),
/// SQL Server (green when every user database is consistent), Active Directory.
/// </summary>
public static class RestorePointPills
{
    /// <param name="Css">"mini-chip" plus its color.</param>
    /// <param name="Icon">An <see cref="AppIcons"/> icon, or null.</param>
    public sealed record Pill(string Css, string? Icon, string Text, string Title);

    /// <param name="scope">"this restore point" or "the newest restore point", for the tooltips.</param>
    public static List<Pill> For(
        string consistency, string? reason, bool warning, long? memoryImageBytes, int sqlDatabases, int? sqlUserDatabases, int? sqlConsistent,
        bool domainController, string? adDomain, string scope)
    {
        var pills = new List<Pill>(3);
        if (consistency == "application")
        {
            pills.Add(new("mini-chip success", AppIcons.ApplicationConsistent, "application-consistent",
                "The guest held its writes for the snapshot (VSS): applications start as after a clean shutdown."));
        }
        else if (memoryImageBytes is { } ram)
        {
            pills.Add(new("mini-chip info", AppIcons.Memory, "RAM",
                $"Snapshot with memory ({Fmt.Bytes(ram)} RAM): the disks match each other; a restore can resume the VM where it was, " +
                "or boot it like after a power failure of the whole VM."));
        }
        else
        {
            // Crash-consistent is what a backup without guest processing promises: a warning only when it is less than that.
            pills.Add(new(warning ? "mini-chip warning" : "mini-chip", warning ? Icons.Material.Filled.Warning : null, "crash-consistent",
                reason ?? "Like after a power failure: the newest changes before the snapshot may be missing; applications recover as after a power outage."));
        }

        if (sqlDatabases > 0)
        {
            // The user databases count: a SQL Server snapshot cannot suspend master, model and msdb, so they are crash-consistent
            // there by design (SQL Server recovers them at start).
            var system = sqlUserDatabases is { } u ? sqlDatabases - u : 0;
            var systemNote = system > 0 ? $" The {system} system database(s) (master, model, msdb) do not count; the SQL Server browser shows their state." : string.Empty;
            pills.Add((sqlUserDatabases, sqlConsistent) switch
            {
                (null, _) or (_, null) or (0, _) => new("mini-chip info", AppIcons.Database, $"SQL {sqlDatabases}", $"{sqlDatabases} SQL Server database(s) in {scope}."),
                var (n, k) when k >= n => new("mini-chip success", AppIcons.Database, "SQL-consistent",
                    $"{n} SQL Server user database(s) in {scope}, all application-consistent: each restores as after a clean shutdown of SQL Server.{systemNote}"),
                var (n, k) => new("mini-chip warning", AppIcons.Database, $"SQL {k}/{n} consistent",
                    $"{n - k} of {n} SQL Server user database(s) in {scope} are only crash-consistent; the SQL Server browser of the restore point says why.{systemNote}"),
            });
        }

        if (domainController)
        {
            pills.Add(new("mini-chip info", AppIcons.ActiveDirectory, "AD", string.IsNullOrEmpty(adDomain)
                ? $"Domain controller: its Active Directory database is in {scope}."
                : $"Domain controller of {adDomain}: its Active Directory database is in {scope}."));
        }

        return pills;
    }
}
