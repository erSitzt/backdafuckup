namespace Backdafuckup.Kubernetes.Tests;

/// <summary>A volume restored as files: what tells that a class cannot share a volume, and how its size is written.</summary>
public sealed class VolumeFilesImportTests
{
    [Theory]
    [InlineData("failed to provision volume with StorageClass \"standard\": NodePath only supports ReadWriteOnce and ReadWriteOncePod (1.22+) access modes", true)]
    [InlineData("rpc error: code = InvalidArgument desc = volume capabilities MULTI_NODE_MULTI_WRITER not supported", true)]
    [InlineData("failed to provision volume with StorageClass \"thin\": ReadWriteMany volumes are not supported for block volumes", true)]
    [InlineData("failed to provision volume with StorageClass \"nfs-client\": mkdir /persistentvolumes/x: permission denied", false)]
    [InlineData("waiting for first consumer to be created before binding", false)]
    [InlineData(null, false)]
    public void A_provisioner_that_refuses_the_access_modes_is_told_from_other_failures(string? message, bool refuses) =>
        Assert.Equal(refuses, VolumeFilesImport.RefusesAccessModes(message));

    [Theory]
    [InlineData(1L << 30, "1Gi")]
    [InlineData(3L << 30, "3Gi")]
    [InlineData(1536L << 20, "1536Mi")]
    [InlineData(1193745944L, "1193745944")]
    public void Sizes_are_written_in_gi_or_mi_when_they_are_whole(long bytes, string expected) =>
        Assert.Equal(expected, VolumeSnapshotFlow.Quantity(bytes).ToString());
}
