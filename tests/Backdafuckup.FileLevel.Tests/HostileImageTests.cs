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
        var path = Fixtures.Image(fixture);
        var random = new Random(42);
        for (var round = 0; round < 12; round++)
        {
            // Metadata of these small file systems sits in the first MiBs after the partition start (1 MiB).
            // The image stays on disk; the flipped bits are applied while reading (an in-memory copy per round and
            // file system took over 1 GB and got the test host OOM-killed on a busy machine).
            var flips = new Dictionary<long, byte>();
            for (var i = 0; i < 64; i++)
            {
                var offset = (1L << 20) + random.Next(8 << 20);
                flips[offset] = (byte)(flips.GetValueOrDefault(offset) ^ (byte)(1 + random.Next(255)));
            }

            var walk = FileLevelWorkers.RunDedicated(
                () => WalkEverything(() => new FlippedBitsStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess), flips)),
                $"hostile-{fixture}-{round}");
            var finished = await Task.WhenAny(walk, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
            Assert.True(finished == walk, $"{fixture} round {round}: the walk did not finish within 60 s");
        }
    }

    private static void WalkEverything(Func<Stream> open)
    {
        try
        {
            using var guest = GuestImage.Open([new GuestDiskSource("0", "corrupt", open)]);
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

    /// <summary>A read-only view of an image with some bytes XORed (the corruption of one round).</summary>
    private sealed class FlippedBitsStream(Stream inner, IReadOnlyDictionary<long, byte> flips) : Stream
    {
        private readonly KeyValuePair<long, byte>[] _flips = [.. flips.OrderBy(f => f.Key)];

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var start = inner.Position;
            var n = inner.Read(buffer);
            foreach (var (at, mask) in _flips)
            {
                if (at >= start && at < start + n)
                {
                    buffer[(int)(at - start)] ^= mask;
                }
            }

            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
