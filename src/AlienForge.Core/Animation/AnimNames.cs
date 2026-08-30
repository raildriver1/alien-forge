using System.Buffers.Binary;
using System.Text;

namespace AlienForge.Core.Animation;

/// <summary>
/// The game's string table: 27803 names, addressed by hash.
/// </summary>
/// <remarks>
/// Layout, measured on DATA\ANIM_SYS\ANIM_STRING_DB.BIN (1047596 bytes):
/// <code>
///   [3 byte prefix][uint32 count][uint32 count]
///   [count * (uint32 key, uint32 ordinal)]   keys sorted descending
///   [count * uint32 offset]                  indexed by ordinal, relative to the blob
///   [string blob]                            null terminated ASCII
/// </code>
/// For that file count is 27803, the pairs start at 11, the offsets at 222435 and the
/// blob at 333647. The offsets rise monotonically from 0 to 713898.
/// </remarks>
public sealed class AnimStringTable
{
    private readonly List<string> _byOrdinal;
    private readonly Dictionary<uint, string> _byStoredKey;

    private AnimStringTable(List<string> byOrdinal, Dictionary<uint, string> byStoredKey)
    {
        _byOrdinal = byOrdinal;
        _byStoredKey = byStoredKey;
    }

    public int Count => _byOrdinal.Count;
    public IReadOnlyList<string> Names => _byOrdinal;

    /// <summary>Names keyed by the value the file itself stores next to them.</summary>
    public IReadOnlyDictionary<uint, string> ByStoredKey => _byStoredKey;

    public static AnimStringTable? TryParse(byte[] data)
    {
        var table = HashTableFile.TryParse(data);
        if (table is null)
            return null;

        int offsetsAt = table.TailAt;
        long blobAt = (long)offsetsAt + (long)table.Count * 4;
        if (blobAt >= data.Length)
            return null;

        var offsets = new uint[table.Count];
        for (int i = 0; i < table.Count; i++)
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(
                data.AsSpan(offsetsAt + i * 4, 4));

        var names = new List<string>(table.Count);
        for (int i = 0; i < table.Count; i++)
            names.Add(ReadCString(data, blobAt + offsets[i]));

        // A table that decoded correctly is nearly all readable. Anything less means
        // the layout guess was wrong, and a wrong guess must not be reported as names.
        int readable = names.Count(n => n.Length > 0 && n.Length < 200);
        if (table.Count == 0 || readable * 100 / table.Count < 90)
            return null;

        var byKey = new Dictionary<uint, string>(table.Count);
        for (int i = 0; i < table.Count; i++)
        {
            uint key = table.Key(i);
            uint ordinal = table.Value(i);
            if (ordinal < names.Count)
                byKey[key] = names[(int)ordinal];
        }

        return new AnimStringTable(names, byKey);
    }

    private static string ReadCString(byte[] data, long at)
    {
        if (at < 0 || at >= data.Length)
            return string.Empty;
        int start = (int)at;
        int end = start;
        while (end < data.Length && data[end] != 0)
            end++;
        return Encoding.ASCII.GetString(data, start, end - start);
    }
}

/// <summary>
/// Turns the hashes scattered through the animation archive back into names.
/// </summary>
/// <remarks>
/// Nothing in the archive stores a clip's name next to the clip. Names live once, in
/// the string tables, and everything else refers to them by a 32-bit hash. Recovering
/// a name is therefore a matter of knowing which hash of which spelling was used, and
/// that varies: skeleton folders use the FNV-1a of an upper-case name, while other
/// tables carry their own key column. Rather than commit to one rule, this builds an
/// index for every spelling and keying it has seen and reports which one answered, so
/// a file that follows a new convention shows up as such instead of silently
/// resolving to nothing.
/// </remarks>
public sealed class AnimNameResolver
{
    /// <summary>How a hash was matched back to a name.</summary>
    public enum Route
    {
        None,
        StoredKey,
        Fnv1aExact,
        Fnv1aUpper,
        Fnv1aLower,
        Fnv1aLeaf,
        Fnv1aLeafUpper,
    }

    private readonly Dictionary<uint, string> _stored = new();
    private readonly Dictionary<uint, string> _exact = new();
    private readonly Dictionary<uint, string> _upper = new();
    private readonly Dictionary<uint, string> _lower = new();
    private readonly Dictionary<uint, string> _leaf = new();
    private readonly Dictionary<uint, string> _leafUpper = new();

    public int NameCount { get; private set; }
    public List<string> Sources { get; } = new();

    /// <summary>Every name from every table, for searching and for reverse lookups.</summary>
    public List<string> AllNames { get; } = new();

    public void Add(AnimStringTable table, string source)
    {
        Sources.Add($"{source}: {table.Count}");

        foreach (var pair in table.ByStoredKey)
            _stored.TryAdd(pair.Key, pair.Value);

        foreach (string name in table.Names)
        {
            if (name.Length == 0)
                continue;
            AllNames.Add(name);
            NameCount++;

            _exact.TryAdd(AnimationCatalog.Fnv1a(name), name);
            _upper.TryAdd(AnimationCatalog.Fnv1a(name.ToUpperInvariant()), name);
            _lower.TryAdd(AnimationCatalog.Fnv1a(name.ToLowerInvariant()), name);

            // Names are often namespaced, e.g. ALIEN:HIPS or a backslash path. Ids
            // elsewhere are sometimes built from just the last part.
            string leaf = Leaf(name);
            if (!string.Equals(leaf, name, StringComparison.Ordinal))
            {
                _leaf.TryAdd(AnimationCatalog.Fnv1a(leaf), name);
                _leafUpper.TryAdd(AnimationCatalog.Fnv1a(leaf.ToUpperInvariant()), name);
            }
        }
    }

    private static string Leaf(string name)
    {
        int cut = name.LastIndexOfAny(new[] { '\\', '/', ':' });
        return cut >= 0 && cut + 1 < name.Length ? name[(cut + 1)..] : name;
    }

    public (string? name, Route route) Resolve(uint hash)
    {
        if (_stored.TryGetValue(hash, out string? name)) return (name, Route.StoredKey);
        if (_exact.TryGetValue(hash, out name)) return (name, Route.Fnv1aExact);
        if (_upper.TryGetValue(hash, out name)) return (name, Route.Fnv1aUpper);
        if (_lower.TryGetValue(hash, out name)) return (name, Route.Fnv1aLower);
        if (_leaf.TryGetValue(hash, out name)) return (name, Route.Fnv1aLeaf);
        if (_leafUpper.TryGetValue(hash, out name)) return (name, Route.Fnv1aLeafUpper);
        return (null, Route.None);
    }

    public string? Name(uint hash) => Resolve(hash).name;

    /// <summary>
    /// Loads every string table in the archive. Both the shipping table and the
    /// larger debug one are used: the debug build carries names the runtime does not
    /// need, and any name it adds is a name that can be recovered.
    /// </summary>
    public static AnimNameResolver Load(Pak2Index archive)
    {
        var resolver = new AnimNameResolver();
        foreach (var entry in archive.Entries)
        {
            string name = entry.Name.Replace('/', '\\');
            if (!name.Contains("ANIM_STRING_DB", StringComparison.OrdinalIgnoreCase))
                continue;

            var table = AnimStringTable.TryParse(archive.Read(entry));
            if (table is not null)
                resolver.Add(table, ShortName(name));
            else
                resolver.Sources.Add($"{ShortName(name)}: не разобрана");
        }
        return resolver;
    }

    private static string ShortName(string path)
    {
        int slash = path.LastIndexOf('\\');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
