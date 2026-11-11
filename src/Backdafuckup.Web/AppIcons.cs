using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Backdafuckup.Web;

/// <summary>
/// One icon per thing the console shows, used everywhere it appears: the menu, page heads, tables, chips, buttons and the
/// wizards. A database looks the same in the SQL Server menu entry, the job editor and the restore point's chips. Use these
/// instead of picking a MudBlazor icon on a page.
/// </summary>
/// <remarks>
/// Material Icons (MudBlazor) lack a database, so <see cref="Database"/>, <see cref="SqlInstance"/>,
/// <see cref="LogBackup"/> and <see cref="Disk"/> are paths from Material Design Icons (Pictogrammers, @mdi/svg 7.4.47,
/// Apache-2.0; see THIRD-PARTY-NOTICES.md), drawn on the same 24×24 grid.
/// </remarks>
public static class AppIcons
{
    // ---- Navigation ----
    public const string Home = Icons.Material.Filled.Dashboard;

    /// <summary>Backup jobs.</summary>
    public const string Job = Icons.Material.Filled.Work;

    /// <summary>Restore points ("Backups &amp; Restore"), and backups in general.</summary>
    public const string Backups = Icons.Material.Filled.Backup;

    /// <summary>Sessions (History).</summary>
    public const string History = Icons.Material.Filled.History;

    /// <summary>The group of pools, repositories, agents and credentials.</summary>
    public const string Infrastructure = Icons.Material.Filled.Lan;

    public const string Inventory = Icons.Material.Filled.AccountTree;

    public const string ApiReference = Icons.Material.Filled.Api;

    public const string ApiToken = Icons.Material.Filled.VpnKey;

    public const string About = Icons.Material.Filled.Info;

    // ---- Things ----

    /// <summary>A virtualization pool and its hosts.</summary>
    public const string Pool = Icons.Material.Filled.Dns;

    /// <summary>A VM, or a guest computer (also an Active Directory computer object).</summary>
    public const string Vm = Icons.Material.Filled.Computer;

    /// <summary>A disk or volume.</summary>
    public const string Disk = "<path d=\"M6,2H18A2,2 0 0,1 20,4V20A2,2 0 0,1 18,22H6A2,2 0 0,1 4,20V4A2,2 0 0,1 6,2M12,4A6,6 0 0,0 6,10C6,13.31 8.69,16 12.1,16L11.22,13.77C10.95,13.29 11.11,12.68 11.59,12.4L12.45,11.9C12.93,11.63 13.54,11.79 13.82,12.27L15.74,14.69C17.12,13.59 18,11.9 18,10A6,6 0 0,0 12,4M12,9A1,1 0 0,1 13,10A1,1 0 0,1 12,11A1,1 0 0,1 11,10A1,1 0 0,1 12,9M7,18A1,1 0 0,0 6,19A1,1 0 0,0 7,20A1,1 0 0,0 8,19A1,1 0 0,0 7,18M12.09,13.27L14.58,19.58L17.17,18.08L12.95,12.77L12.09,13.27Z\"/>";

    /// <summary>A backup repository.</summary>
    public const string Repository = Icons.Material.Filled.Inventory2;

    /// <summary>A guest agent.</summary>
    public const string Agent = Icons.Material.Filled.SettingsInputComponent;

    /// <summary>Stored credentials (accounts, passwords).</summary>
    public const string Credential = Icons.Material.Filled.Key;

    /// <summary>SQL Server and its databases: the database cylinder.</summary>
    public const string Database = "<path d=\"M12,3C7.58,3 4,4.79 4,7C4,9.21 7.58,11 12,11C16.42,11 20,9.21 20,7C20,4.79 16.42,3 12,3M4,9V12C4,14.21 7.58,16 12,16C16.42,16 20,14.21 20,12V9C20,11.21 16.42,13 12,13C7.58,13 4,11.21 4,9M4,14V17C4,19.21 7.58,21 12,21C16.42,21 20,19.21 20,17V14C20,16.21 16.42,18 12,18C7.58,18 4,16.21 4,14Z\"/>";

    /// <summary>A SQL Server instance.</summary>
    public const string SqlInstance = "<path d=\"M4 7C4 4.79 7.58 3 12 3S20 4.79 20 7 16.42 11 12 11 4 9.21 4 7M12.08 18L12 18C7.58 18 4 16.21 4 14V17C4 19.21 7.58 21 12 21C12.1 21 12.2 21 12.29 21C12.11 20.36 12 19.69 12 19C12 18.66 12.03 18.33 12.08 18M20 12.08C20 12.05 20 12.03 20 12V9C20 11.21 16.42 13 12 13S4 11.21 4 9V12C4 14.21 7.58 16 12 16C12.23 16 12.46 16 12.69 16C13.82 13.63 16.22 12 19 12C19.34 12 19.67 12.03 20 12.08M23.8 20.4C23.9 20.4 23.9 20.5 23.8 20.6L22.8 22.3C22.7 22.4 22.6 22.4 22.5 22.4L21.3 22C21 22.2 20.8 22.3 20.5 22.5L20.3 23.8C20.3 23.9 20.2 24 20.1 24H18.1C18 24 17.9 23.9 17.8 23.8L17.6 22.5C17.3 22.4 17 22.2 16.8 22L15.6 22.5C15.5 22.5 15.4 22.5 15.3 22.4L14.3 20.7C14.2 20.6 14.3 20.5 14.4 20.4L15.5 19.6V18.6L14.4 17.8C14.3 17.7 14.3 17.6 14.3 17.5L15.3 15.8C15.4 15.7 15.5 15.7 15.6 15.7L16.8 16.2C17.1 16 17.3 15.9 17.6 15.7L17.8 14.4C17.8 14.3 17.9 14.2 18.1 14.2H20.1C20.2 14.2 20.3 14.3 20.3 14.4L20.5 15.7C20.8 15.8 21.1 16 21.4 16.2L22.6 15.7C22.7 15.7 22.9 15.7 22.9 15.8L23.9 17.5C24 17.6 23.9 17.7 23.8 17.8L22.7 18.6V19.6L23.8 20.4M20.5 19C20.5 18.2 19.8 17.5 19 17.5S17.5 18.2 17.5 19 18.2 20.5 19 20.5 20.5 19.8 20.5 19Z\"/>";

    /// <summary>SQL Server log backups (a database with a clock: restore to a point in time).</summary>
    public const string LogBackup = "<path d=\"M16.5 16.25L19.36 17.94L18.61 19.16L15 17V12H16.5V16.25M23 16C23 19.87 19.87 23 16 23C13.61 23 11.5 21.8 10.25 20C6.18 19.79 3 18.08 3 16V13C3 14.88 5.58 16.44 9.06 16.88C9.03 16.59 9 16.3 9 16C9 15.62 9.04 15.25 9.1 14.88C5.6 14.45 3 12.88 3 11V8C3 10.09 6.2 11.8 10.27 12C10.87 11.14 11.64 10.44 12.53 9.93C12.04 9.97 11.5 10 11 10C6.58 10 3 8.21 3 6S6.58 2 11 2 19 3.79 19 6C19 7.2 17.93 8.28 16.25 9C17 9.04 17.75 9.19 18.44 9.45C18.79 9 19 8.5 19 8V9.68C21.36 10.81 23 13.21 23 16M21 16C21 13.24 18.76 11 16 11S11 13.24 11 16 13.24 21 16 21 21 18.76 21 16Z\"/>";

    /// <summary>Active Directory: a domain, and restores of its objects.</summary>
    public const string ActiveDirectory = Icons.Material.Filled.Domain;

    /// <summary>Files and folders in a restore point.</summary>
    public const string Files = Icons.Material.Filled.FolderOpen;

    /// <summary>RAM: a snapshot with memory, and its memory image.</summary>
    public const string Memory = Icons.Material.Filled.Memory;

    // ---- States and actions ----

    /// <summary>Restoring anything.</summary>
    public const string Restore = Icons.Material.Filled.SettingsBackupRestore;

    /// <summary>Application-consistent (guest processing).</summary>
    public const string ApplicationConsistent = Icons.Material.Filled.VerifiedUser;

    /// <summary>Transfer rates and the settings that tune them.</summary>
    public const string Performance = Icons.Material.Filled.Speed;

    /// <summary>Checking data or a connection.</summary>
    public const string Verify = Icons.Material.Filled.FactCheck;

    /// <summary>
    /// An icon as plain inline SVG in the text color, for rows rendered in bulk (Backups &amp; Restore) where a MudIcon
    /// component per chip would cost a component each.
    /// </summary>
    public static MarkupString Svg(string icon, string cssClass) =>
        new($"<svg class=\"{cssClass}\" viewBox=\"0 0 24 24\" fill=\"currentColor\" aria-hidden=\"true\" focusable=\"false\">{icon}</svg>");
}
