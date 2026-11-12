using Backdafuckup.Server.Engine;

namespace Backdafuckup.Server.Tests;

/// <summary>The size of the new volume a file tree goes back into.</summary>
public sealed class TreeVolumeCapacityTests
{
    private const long GiB = 1L << 30;
    private const long MiB = 1L << 20;

    [Theory]
    [InlineData(GiB, 95 * MiB, GiB)] // the files fit into the PVC's size: it stays
    [InlineData(5 * GiB, 4 * GiB, 5 * GiB)]
    [InlineData(GiB, 900 * MiB, 2 * GiB)] // they do not with room for the file system: whole GiB above
    [InlineData(0, 0, GiB)] // no size known, nothing in it
    [InlineData(10 * GiB, 40 * GiB, 46 * GiB)] // an NFS PV whose size said little: 40 GiB + 5 GiB + 256 MiB, rounded up
    public void The_pvc_keeps_its_size_unless_its_files_need_more(long requested, long files, long expected) =>
        Assert.Equal(expected, RestoreSessionRunner.TreeVolumeCapacity(requested, files));
}
