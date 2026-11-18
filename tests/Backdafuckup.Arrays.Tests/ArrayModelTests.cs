using System.Text.Json;
using Backdafuckup.Core.Arrays;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.Arrays.Tests;

/// <summary>The array plugin's Core pieces: WWN notation and the manifest records of a backup from an array snapshot.</summary>
public sealed class ArrayModelTests
{
    [Theory]
    [InlineData("60002AC0000000000000002200019C45")]
    [InlineData("naa.60002ac0000000000000002200019c45")]
    [InlineData("360002ac0000000000000002200019c45")]
    [InlineData("60:00:2a:c0:00:00:00:00:00:00:00:22:00:01:9c:45")]
    public void Wwns_compare_in_any_notation(string wwn) => Assert.Equal("60002ac0000000000000002200019c45", Wwns.Normalize(wwn));

    [Fact]
    public void The_array_snapshot_and_the_verification_survive_the_manifest_json()
    {
        var info = new ArraySnapshotInfo
        {
            System = "alletra-test", Kind = "AlletraMp", Snapshots = ["bdfu-1a2b3c4d-01"], TakenUtc = new DateTimeOffset(2026, 11, 18, 22, 0, 0, TimeSpan.Zero),
            Method = "lvhd", VmSnapshotLifetime = TimeSpan.FromSeconds(7.5),
        };
        var stats = new DiskBackupStats { Transport = "array-lvhd", Verification = new DiskVerification("cbt-unchanged", 32, 0) };

        var infoBack = (ArraySnapshotInfo)JsonSerializer.Deserialize(JsonSerializer.Serialize(info, CoreJsonContext.Default.GetTypeInfo(typeof(ArraySnapshotInfo))!),
            CoreJsonContext.Default.GetTypeInfo(typeof(ArraySnapshotInfo))!)!;
        var statsBack = (DiskBackupStats)JsonSerializer.Deserialize(JsonSerializer.Serialize(stats, CoreJsonContext.Default.GetTypeInfo(typeof(DiskBackupStats))!),
            CoreJsonContext.Default.GetTypeInfo(typeof(DiskBackupStats))!)!;

        Assert.Equal(("alletra-test", "lvhd", TimeSpan.FromSeconds(7.5), "bdfu-1a2b3c4d-01"), (infoBack.System, infoBack.Method, infoBack.VmSnapshotLifetime, Assert.Single(infoBack.Snapshots)));
        Assert.Equal(("array-lvhd", "cbt-unchanged", 32, true), (statsBack.Transport, statsBack.Verification!.Method, statsBack.Verification.Regions, statsBack.Verification.Passed));
    }
}
