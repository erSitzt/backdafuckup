using System.Security.Cryptography;
using Backdafuckup.Core.Repository;
using Backdafuckup.Tape.Format;

namespace Backdafuckup.Tape.Tests;

public sealed class OpenSetIndexTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bdfu-bdci-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() => OpenSetIndex.Delete(_directory);

    [Fact]
    public void Chunks_of_committed_groups_are_found_and_an_uncommitted_segment_is_ignored()
    {
        var index = new OpenSetIndex(_directory, []);
        var groups = new List<List<ChunkLocation>>();
        for (var g = 0; g < 10; g++)
        {
            var group = Enumerable.Range(0, 30_000).Select(i => new ChunkLocation(ChunkId.FromBytes(RandomNumberGenerator.GetBytes(32)), 1 + (g / 4), g, (uint)i, i % 1000, 4000 + g)).ToList();
            foreach (var location in group)
            {
                index.Add(location);
            }

            Assert.True(index.TryFind(group[17].Id, out _)); // pending ones count too
            groups.Add(group);
            var committed = index.CommitPending();
            Assert.InRange(committed.Count, 1, OpenSetIndex.MaxSegments);
        }

        // Ten groups, merged on the way: every chunk found with its location, unknown ids not.
        Assert.All(groups.SelectMany(g => g.Where((_, i) => i % 97 == 0)), l => Assert.True(index.TryFind(l.Id, out var found) && found == l));
        Assert.False(index.TryFind(ChunkId.FromBytes(RandomNumberGenerator.GetBytes(32)), out _));

        // A group written but never committed (crash, torn file): its segment is not used after a restart.
        var segments = index.Segments;
        var lost = new ChunkLocation(ChunkId.FromBytes(RandomNumberGenerator.GetBytes(32)), 9, 9, 9, 9, 9);
        index.Add(lost);
        index.CommitPending();
        var reopened = new OpenSetIndex(_directory, segments);
        Assert.False(reopened.TryFind(lost.Id, out _));
        Assert.True(reopened.TryFind(groups[3][5].Id, out var again) && again == groups[3][5]);
        Assert.Equal(segments, reopened.Segments);
        Assert.Equal(segments.Count, Directory.EnumerateFiles(_directory).Count());
    }
}
