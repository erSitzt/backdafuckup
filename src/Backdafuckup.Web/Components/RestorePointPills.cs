using Backdafuckup.Web.Services;
using MudBlazor;

namespace Backdafuckup.Web.Components;

/// <summary>
/// The small chips that sum up what a restore point holds and how consistent it is, the same in the list of a VM's restore
/// points and in its row of Backups &amp; Restore: application- or crash-consistent (or RAM for a snapshot with memory),
/// SQL Server (green when every database is consistent), Active Directory.
/// </summary>
public static class RestorePointPills
{
    /// <param name="Css">"mini-chip" plus its color.</param>
    /// <param name="Icon">An <see cref="AppIcons"/> icon, or null.</param>
    public sealed record Pill(string Css, string? Icon, string Text, string Title);

    /// <param name="scope">"this restore point" or "the newest restore point", for the tooltips.</param>
    public static List<Pill> For(
        string consistency, string? reason, bool warning, long? memoryImageBytes, int sqlDatabases, int? sqlConsistent,
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
            pills.Add(sqlConsistent switch
            {
                null => new("mini-chip info", AppIcons.Database, $"SQL {sqlDatabases}", $"{sqlDatabases} SQL Server database(s) in {scope}."),
                var n when n >= sqlDatabases => new("mini-chip success", AppIcons.Database, "SQL-consistent",
                    $"{sqlDatabases} SQL Server database(s) in {scope}, all application-consistent: each restores as after a clean shutdown of SQL Server."),
                var n => new("mini-chip warning", AppIcons.Database, $"SQL {n}/{sqlDatabases} consistent",
                    $"{sqlDatabases - n} of {sqlDatabases} SQL Server database(s) in {scope} are only crash-consistent; the SQL Server browser of the restore point says why."),
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
