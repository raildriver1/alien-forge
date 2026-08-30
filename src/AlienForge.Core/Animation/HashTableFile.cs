using System.Buffers.Binary;

namespace AlienForge.Core.Animation;

/// <summary>
/// The lookup-table shape the animation system uses across its .BIN files.
/// </summary>
/// <remarks>
/// Every one of these files is a hash table meant for binary search: a short header,
/// the record count written twice, then that many (key, value) pairs sorted by
/// descending key, then whatever payload the particular file needs. The header length
/// differs between files -- ANIM_STRING_DB starts its counts at +0x03 while
/// &lt;skeleton&gt;_ANIM_CLIP_DB starts at +0x10 -- so the header is found rather than
/// assumed: the count appearing twice in a row, with a plausible value that makes the
/// pair table fit the file, is a strong enough signature to locate it.
/// </remarks>
public sealed class HashTableFile
{
    private HashTableFile(byte[] data, int headerAt, int count, int pairsAt)
    {
        Data = data;
        HeaderAt = headerAt;
        Count = count;
        PairsAt = pairsAt;
        TailAt = pairsAt + count * 8;
    }

    public byte[] Data { get; }

    /// <summary>Where the doubled count was found.</summary>
    public int HeaderAt { get; }

    public int Count { get; }
    public int PairsAt { get; }

    /// <summary>First byte after the pair table.</summary>
    public int TailAt { get; }

    public int TailLength => Data.Length - TailAt;

    public uint Key(int index)
        => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(PairsAt + index * 8, 4));

    public uint Value(int index)
        => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(PairsAt + index * 8 + 4, 4));

    public IEnumerable<(uint key, uint value)> Pairs()
    {
        for (int i = 0; i < Count; i++)
            yield return (Key(i), Value(i));
    }

    /// <summary>Whether the keys really are sorted descending, as a sanity check.</summary>
    public bool KeysDescending()
    {
        for (int i = 1; i < Count; i++)
            if (Key(i - 1) < Key(i))
                return false;
        return true;
    }

    public static HashTableFile? TryParse(byte[] data, int searchLimit = 64)
    {
        if (data.Length < 24)
            return null;

        for (int at = 0; at + 8 <= Math.Min(searchLimit, data.Length - 8); at++)
        {
            uint first = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at, 4));
            uint second = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4, 4));
            if (first == 0 || first != second || first > 4_000_000)
                continue;

            int count = (int)first;
            int pairsAt = at + 8;
            long needed = (long)pairsAt + (long)count * 8;
            if (needed > data.Length)
                continue;

            var candidate = new HashTableFile(data, at, count, pairsAt);

            // Descending keys are what make this a lookup table rather than a
            // coincidence of two equal integers.
            if (!candidate.KeysDescending())
                continue;
            return candidate;
        }
        return null;
    }
}
