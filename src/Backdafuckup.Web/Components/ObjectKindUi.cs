using Backdafuckup.Contracts;

namespace Backdafuckup.Web.Components;

/// <summary>
/// How the console tells the kinds of backed-up objects apart (a VM, a Kubernetes namespace, later others): an icon and
/// a color per kind, for groups, rows and sessions (see <c>.kind-*</c> in app.css).
/// </summary>
public static class ObjectKindUi
{
    public const string VmCss = "kind-vm";
    public const string KubernetesCss = "kind-k8s";

    public static bool IsNamespace(string? objectKind) => objectKind == RestorePointObjectKinds.KubernetesNamespace;

    public static string Icon(string? objectKind) => IsNamespace(objectKind) ? AppIcons.Kubernetes : AppIcons.Vm;

    public static string Css(string? objectKind) => IsNamespace(objectKind) ? KubernetesCss : VmCss;

    public static string Noun(string? objectKind) => IsNamespace(objectKind) ? "Kubernetes namespace" : "VM";

    /// <summary>A session's icon: what it works on (VM or namespace), else what it is (SQL Server, Active Directory, maintenance, agents).</summary>
    public static (string Icon, string Css, string Title) Session(SessionSummaryDto session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.ObjectKind switch
        {
            RestorePointObjectKinds.KubernetesNamespace => (AppIcons.Kubernetes, KubernetesCss, "Kubernetes namespace"),
            RestorePointObjectKinds.Vm => (AppIcons.Vm, VmCss, "VM"),
            _ => session.Kind switch
            {
                "SqlBackup" or "SqlPublish" or "SqlRestore" or "SqlExport" => (AppIcons.Database, "kind-other", "SQL Server"),
                "AdExplore" or "AdRestore" => (AppIcons.ActiveDirectory, "kind-other", "Active Directory"),
                "RepositoryMaintenance" => (AppIcons.Repository, "kind-other", "Repository"),
                "AgentDeployment" => (AppIcons.Agent, "kind-other", "Guest agents"),
                _ => (AppIcons.Vm, VmCss, "VM"),
            },
        };
    }
}
