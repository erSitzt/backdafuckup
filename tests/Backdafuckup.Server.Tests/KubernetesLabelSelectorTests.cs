using Backdafuckup.Contracts;

namespace Backdafuckup.Server.Tests;

/// <summary>Label selectors of jobs that back up the namespaces with certain labels (the syntax of kubectl -l).</summary>
public sealed class KubernetesLabelSelectorTests
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["backup"] = "daily",
        ["team"] = "shop",
        ["app.kubernetes.io/part-of"] = "webshop",
    };

    [Theory]
    [InlineData("backup=daily", true)]
    [InlineData("backup==daily", true)]
    [InlineData("backup!=daily", false)]
    [InlineData("backup=weekly", false)]
    [InlineData("backup, team=shop", true)]
    [InlineData("!backup", false)]
    [InlineData("!archived", true)]
    [InlineData("team in (shop, crm)", true)]
    [InlineData("team notin (shop,crm)", false)]
    [InlineData("app.kubernetes.io/part-of=webshop,team in (shop)", true)]
    [InlineData("missing=x", false)]
    [InlineData("missing!=x", true)]
    public void A_selector_matches_labels_as_kubectl_does(string selector, bool expected)
    {
        var (requirements, error) = KubernetesLabelSelector.Parse(selector);
        Assert.Null(error);
        Assert.Equal(expected, KubernetesLabelSelector.Matches(requirements!, Labels));
    }

    [Theory]
    [InlineData("")]
    [InlineData("backup in daily")]
    [InlineData("backup=da ily")]
    [InlineData("=daily")]
    [InlineData("team in ()")]
    [InlineData("backup=daily;team=shop")]
    public void Anything_else_is_refused_with_a_reason(string selector)
    {
        Assert.NotNull(KubernetesLabelSelector.Parse(selector).Error);
    }

    [Fact]
    public void Job_objects_and_inventory_labels_round_trip()
    {
        var nativeId = KubernetesLabelSelector.JobObject(" backup=daily ");
        Assert.Equal(("labels:backup=daily", true, "backup=daily"), (nativeId, KubernetesLabelSelector.IsJobObject(nativeId), KubernetesLabelSelector.Of(nativeId)));
        Assert.False(KubernetesLabelSelector.IsJobObject("bdfu-spike"));
        var text = KubernetesLabelSelector.FormatLabels(Labels);
        Assert.Equal("app.kubernetes.io/part-of=webshop,backup=daily,team=shop", text);
        Assert.Equal(Labels, KubernetesLabelSelector.ParseLabels(text));
    }
}
