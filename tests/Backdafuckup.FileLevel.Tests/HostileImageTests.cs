using Backdafuckup.FileLevel;

namespace Backdafuckup.FileLevel.Tests;

/// <summary>
/// Guest images are untrusted. Corrupted metadata must end in an exception within a bounded time, never in a hang
/// or a crash of the process.
/// </summary>
[Trait("Category", "Unit")]
public sealed class HostileImageTests
{
    [Theory]
    [InlineData("ntfs-gpt")]
    [InlineData("ext4-gpt")]
    [InlineData("xfs-nrext64")]
    public async Task Corrupted_metadata_fails_cleanly(string fixture)
    {
        var original = await File.ReadAllBytesAsync(Fixtures.Image(fixture), TestContext.Current.CancellationToken);
        var random = new Random(42);
        for (var round = 0; round < 12; round++)
        {
            var image = (byte[])original.Clone();

            // Metadata of these small file systems sits in the first MiBs after the partition start (1 MiB).
            for (var i = 0; i < 64; i++)
            {
                image[(1 << 20) + random.Next(8 << 20)] ^= (byte)(1 + random.Next(255));
            }

            var walk = FileLevelWorkers.RunDedicated(() => WalkEverything(image), $"hostile-{fixture}-{round}");
            var finished = await Task.WhenAny(walk, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
            Assert.True(finished == walk, $"{fixture} round {round}: the walk did not finish within 60 s");
        }
    }

    private static void WalkEverything(byte[] image)
    {
        try
        {
            using var guest = GuestImage.Open([new GuestDiskSource("0", "corrupt", () => new MemoryStream(image, writable: false))]);
            foreach (var volume in guest.Volumes.Where(v => v.Readable))
            {
                var fs = guest.OpenFileSystem(volume.Id);
                foreach (var (path, entry) in GuestWalker.Walk(fs, [GuestPath.Root], new GuestWalkLimits(MaxEntries: 50_000), CancellationToken.None))
                {
                    if (entry.Kind == GuestEntryKind.File && entry.Unsupported is null)
                    {
                        try
                        {
                            // At most 64 MiB per file: a corrupt size within the plausibility bound may still be large.
                            using var stream = fs.OpenRead(path);
                            var buffer = new byte[1 << 20];
                            for (var read = 0; read < 64 && stream.Read(buffer) > 0; read++)
                            {
                            }
                        }
                        catch (Exception ex) when (IsExpected(ex))
                        {
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
        }
    }

    /// <summary>Any ordinary exception is fine; only hangs and process crashes are failures.</summary>
    private static bool IsExpected(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
