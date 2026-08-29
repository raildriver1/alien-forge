using System.Text;

namespace AlienForge.Core.Animation;

/// <summary>A clip container inside ANIMATION.PAK.</summary>
public sealed class AnimationClipRef
{
    public required Pak2Entry Entry { get; init; }
    public required uint SkeletonId { get; init; }
    public string? SkeletonName { get; init; }

    /// <summary>Size of the embedded Havok packfile, from the container header.</summary>
    public int HavokSize { get; init; }

    public string Name => Entry.Name;

    /// <summary>Leaf file name, which is what the clip is known by.</summary>
    public string ShortName => LeafName(Entry.Name);

    public string SizeText => Entry.Length >= 1048576
        ? $"{Entry.Length / 1048576.0:F1} MB"
        : $"{Entry.Length / 1024.0:F0} KB";

    public string Display
        => $"{LeafName(Entry.Name),-52} {Entry.Length / 1024.0,8:F1} KB";

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Entry.Name);
        sb.AppendLine();
        sb.AppendLine($"skeleton : {SkeletonName ?? "?"} (id {SkeletonId})");
        sb.AppendLine($"offset   : {Entry.Offset}");
        sb.AppendLine($"bytes    : {Entry.Length}");
        sb.AppendLine($"havok    : {HavokSize}");
        return sb.ToString();
    }

    private static string LeafName(string name)
    {
        string normalised = name.Replace('/', '\\');
        int slash = normalised.LastIndexOf('\\');
        return slash >= 0 ? normalised[(slash + 1)..] : normalised;
    }
}

/// <summary>
/// Indexes the animation archive and maps a model onto its skeleton and clips.
/// </summary>
/// <remarks>
/// Skeletons are addressed by a hash of their name, not by the name itself: the
/// entry for the Alien is <c>SKELE\SK64\4020659628</c>, and 4020659628 is the
/// FNV-1a of the ASCII string "ALIEN". That was established by hashing the 27803
/// names in ANIM_STRING_DB and matching them against the ids present in the
/// archive, so the mapping is measured rather than assumed.
/// </remarks>
public sealed class AnimationCatalog
{
    private readonly Pak2Index _pak;
    private readonly Dictionary<uint, Pak2Entry> _skeletons = new();

    private AnimationCatalog(Pak2Index pak)
    {
        _pak = pak;

        // SK64 is the 64-bit skeleton variant the PC build uses.
        foreach (var entry in pak.Entries)
        {
            if (entry.Name.IndexOf("SKELE", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            string leaf = Leaf(entry.Name);
            if (uint.TryParse(leaf, out uint id))
                _skeletons[id] = entry;
        }
    }

    public Pak2Index Archive => _pak;
    public int SkeletonCount => _skeletons.Count;

    public static AnimationCatalog Open(GameInstall install)
    {
        if (!File.Exists(install.AnimationPak))
            throw new FileNotFoundException($"Не найден {install.AnimationPak}");
        return new AnimationCatalog(Pak2Index.Open(install.AnimationPak));
    }

    /// <summary>
    /// FNV-1a, 32-bit, case sensitive over the raw bytes. Used for every id in the
    /// animation system; verified as fnv1a("ALIEN") == 4020659628.
    /// </summary>
    public static uint Fnv1a(string text)
    {
        uint hash = 0x811C9DC5;
        foreach (char c in text)
        {
            hash ^= (byte)c;
            hash *= 0x01000193;
        }
        return hash;
    }

    /// <summary>
    /// Guesses the skeleton name from a model path. Characters live under
    /// <c>CHARACTERS\&lt;NAME&gt;\model0.cs2</c>, and that folder is the skeleton name.
    /// </summary>
    public static string? SkeletonNameFor(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            return null;
        var parts = modelPath.Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i].Equals("CHARACTERS", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1].ToUpperInvariant();
        return null;
    }

    public (uint id, string? name, Pak2Entry? entry) ResolveSkeleton(string? modelPath)
    {
        string? name = SkeletonNameFor(modelPath);
        if (name is null)
            return (0, null, null);
        uint id = Fnv1a(name);
        _skeletons.TryGetValue(id, out var entry);
        return (id, name, entry);
    }

    public string DescribeSkeleton(string? modelPath)
    {
        var (id, name, entry) = ResolveSkeleton(modelPath);
        if (name is null)
            return "—";
        if (entry is null)
            return $"{name} (id {id}) — не найден в архиве";
        return $"{name} (id {id}, {entry.Length} B)";
    }

    /// <summary>Marks the start of the embedded Havok packfile inside a clip.</summary>
    private const uint HavokPackfileMagic = 0x57e0e057;

    /// <summary>
    /// Clip containers belonging to a model's skeleton.
    /// </summary>
    /// <remarks>
    /// The skeleton a clip belongs to is not in its name, it is the second field of
    /// the container, so the names alone cannot group them. Each candidate's header
    /// is read instead: 4 bytes of version (1), 4 bytes of skeleton id, 4 bytes of
    /// Havok payload size, then the packfile magic 0x57E0E057. One file handle is
    /// reused for the whole sweep, which keeps it well under a second even though
    /// the archive holds 26048 records.
    /// </remarks>
    public List<AnimationClipRef> ClipsFor(string? modelPath)
    {
        var (id, name, _) = ResolveSkeleton(modelPath);
        var result = new List<AnimationClipRef>();
        if (name is null)
            return result;

        var header = new byte[16];
        using var stream = File.OpenRead(_pak.Path);

        foreach (var entry in _pak.Entries)
        {
            if (entry.Length < 16)
                continue;
            // Skeletons and mapping tables are not clips.
            if (entry.Name.IndexOf("SKELE", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            stream.Position = entry.Offset;
            if (stream.Read(header, 0, 16) != 16)
                continue;

            uint version = BitConverter.ToUInt32(header, 0);
            uint skeletonId = BitConverter.ToUInt32(header, 4);
            uint magic = BitConverter.ToUInt32(header, 12);
            if (version != 1 || skeletonId != id || magic != HavokPackfileMagic)
                continue;

            result.Add(new AnimationClipRef
            {
                Entry = entry,
                SkeletonId = id,
                SkeletonName = name,
                HavokSize = (int)BitConverter.ToUInt32(header, 8),
            });
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// Cuts the embedded Havok packfile out of a clip container.
    /// </summary>
    /// <remarks>
    /// The container is a 12 byte prelude (version, skeleton id, payload size)
    /// followed by the packfile itself, so the .hkx starts at offset 0x0C. Written
    /// out unchanged, which is what a Havok reader expects to be handed.
    /// </remarks>
    public byte[] ExtractHavok(AnimationClipRef clip)
    {
        const int prelude = 12;
        int length = clip.HavokSize > 0
            ? Math.Min(clip.HavokSize, clip.Entry.Length - prelude)
            : clip.Entry.Length - prelude;
        if (length <= 0)
            return Array.Empty<byte>();

        using var stream = File.OpenRead(_pak.Path);
        stream.Position = clip.Entry.Offset + prelude;
        var buffer = new byte[length];
        int read = 0;
        while (read < buffer.Length)
        {
            int got = stream.Read(buffer, read, buffer.Length - read);
            if (got <= 0)
                break;
            read += got;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>The skeleton's own Havok packfile, needed to interpret any clip.</summary>
    public byte[]? ExtractSkeletonHavok(string? modelPath)
    {
        var (_, _, entry) = ResolveSkeleton(modelPath);
        if (entry is null)
            return null;

        byte[] raw = _pak.Read(entry);
        // The skeleton record carries a short prelude too; find the packfile magic
        // rather than assuming a fixed offset, since it differs from the clip layout.
        for (int at = 0; at + 4 <= Math.Min(raw.Length, 64); at++)
        {
            if (BitConverter.ToUInt32(raw, at) == HavokPackfileMagic)
                return raw[at..];
        }
        return raw;
    }

    /// <summary>Every skeleton id that actually has clips, with how many.</summary>
    public Dictionary<uint, int> CountClipsPerSkeleton()
    {
        var counts = new Dictionary<uint, int>();
        var header = new byte[16];
        using var stream = File.OpenRead(_pak.Path);

        foreach (var entry in _pak.Entries)
        {
            if (entry.Length < 16)
                continue;
            stream.Position = entry.Offset;
            if (stream.Read(header, 0, 16) != 16)
                continue;
            if (BitConverter.ToUInt32(header, 0) != 1)
                continue;
            if (BitConverter.ToUInt32(header, 12) != HavokPackfileMagic)
                continue;
            uint id = BitConverter.ToUInt32(header, 4);
            counts[id] = counts.TryGetValue(id, out int n) ? n + 1 : 1;
        }
        return counts;
    }

    private static string Leaf(string name)
    {
        string normalised = name.Replace('/', '\\');
        int slash = normalised.LastIndexOf('\\');
        return slash >= 0 ? normalised[(slash + 1)..] : normalised;
    }
}
