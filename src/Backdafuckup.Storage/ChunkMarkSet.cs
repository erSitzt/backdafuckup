using Backdafuckup.Core.Repository;

namespace Backdafuckup.Storage;

/// <summary>
/// The set of referenced chunks during GC, kept as the first 64 bits of each id (8 bytes per chunk instead of a full id
/// in a hash set). Two ids sharing a prefix can only make GC keep a chunk that is no longer referenced, never delete
/// one that is.
/// </summary>
internal sealed class ChunkMarkSet
{
    private ulong[] _slots = new ulong[1024];
    private int _count;

    /// <summary>Distinct prefixes marked (equal to the number of distinct chunks but for prefix collisions).</summary>
    public int Count => _count;

    public bool Add(ChunkId id)
    {
        if ((_count + 1) * 10L > _slots.LongLength * 7)
        {
            Grow();
        }

        return Insert(_slots, Key(id), ref _count);
    }

    public bool Contains(ChunkId id)
    {
        var key = Key(id);
        var mask = _slots.Length - 1;
        for (var i = (int)(Mix(key) & (ulong)mask); ; i = (i + 1) & mask)
        {
            var slot = _slots[i];
            if (slot == key)
            {
                return true;
            }

            if (slot == 0)
            {
                return false;
            }
        }
    }

    private static ulong Key(ChunkId id)
    {
        var key = id.Words.A;
        return key == 0 ? 1 : key;
    }

    // The prefix of a SHA-256 is already uniform; this only spreads keys that share low bits after masking.
    private static ulong Mix(ulong key) => key ^ (key >> 29);

    private static bool Insert(ulong[] slots, ulong key, ref int count)
    {
        var mask = slots.Length - 1;
        for (var i = (int)(Mix(key) & (ulong)mask); ; i = (i + 1) & mask)
        {
            var slot = slots[i];
            if (slot == key)
            {
                return false;
            }

            if (slot == 0)
            {
                slots[i] = key;
                count++;
                return true;
            }
        }
    }

    private void Grow()
    {
        var next = new ulong[_slots.Length * 2];
        var count = 0;
        foreach (var key in _slots)
        {
            if (key != 0)
            {
                Insert(next, key, ref count);
            }
        }

        _slots = next;
    }
}
