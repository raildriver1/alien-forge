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

    /// <summary>
    /// The real names of the clips inside, recovered from the game's string tables.
    /// Empty when the section is not listed in the master clip database.
    /// </summary>
    public List<ClipNameEntry> Names { get; } = new();

    /// <summary>
    /// What to show a person: the clip's own name when the section holds one clip, the
    /// shared folder plus a count when it holds several, and the file name only as a
    /// last resort.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (Names.Count == 1)
                return Names[0].ShortName;
            if (Names.Count > 1)
                return $"{CommonFolder()} ({Names.Count} клипов)";
            return ShortName;
        }
    }

    private string CommonFolder()
    {
        string? shared = null;
        foreach (var entry in Names)
        {
            if (shared is null)
            {
                shared = entry.Category;
                continue;
            }
            // Trim back to the deepest folder every clip agrees on.
            while (shared.Length > 0
                   && !entry.Category.StartsWith(shared, StringComparison.OrdinalIgnoreCase))
            {
                int cut = shared.LastIndexOf('\\');
                shared = cut > 0 ? shared[..cut] : string.Empty;
            }
        }
        if (string.IsNullOrEmpty(shared))
            return ShortName;
        int leaf = shared.LastIndexOf('\\');
        return leaf >= 0 && leaf + 1 < shared.Length ? shared[(leaf + 1)..] : shared;
    }

    public string SizeText => Entry.Length >= 1048576
        ? $"{Entry.Length / 1048576.0:F1} MB"
        : $"{Entry.Length / 1024.0:F0} KB";

    public string Display
        => $"{DisplayName,-52} {Entry.Length / 1024.0,8:F1} KB";

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
        var parts = Segments(modelPath);
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i].Equals("CHARACTERS", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1].ToUpperInvariant();
        return null;
    }

    private static string[] Segments(string path)
        => path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Names worth testing as this model's skeleton, best guess first.
    /// </summary>
    /// <remarks>
    /// Relying on the <c>CHARACTERS</c> folder alone is why only the Alien had
    /// animations: any model stored under a different root, or whose folder is not
    /// spelled exactly like its rig, resolved to nothing at all. Every folder on the
    /// path is a candidate instead, deepest first, because a rig is named after the
    /// character it belongs to and that is the folder the model sits in. Guessing
    /// stays safe because a candidate is only accepted when a skeleton with that hash
    /// is really in the archive.
    /// </remarks>
    public static IEnumerable<string> SkeletonCandidates(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            yield break;

        if (SkeletonNameFor(modelPath) is string preferred)
            yield return preferred;

        var parts = Segments(modelPath);

        // Deepest folder first: CHARACTERS\ALIEN\model0.cs2 tries ALIEN before
        // CHARACTERS, and a prop under PROPS\LOCKER\model0.cs2 tries LOCKER.
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            string segment = parts[i].ToUpperInvariant();
            if (segment is "CHARACTERS" or "DATA" or "ENV" or "PRODUCTION" or "MODELS")
                continue;
            yield return segment;
        }

        // The file itself, in case the rig is named after it rather than its folder.
        string leaf = Path.GetFileNameWithoutExtension(parts.Length > 0 ? parts[^1] : string.Empty);
        if (leaf.Length > 0)
            yield return leaf.ToUpperInvariant();
    }

    /// <summary>
    /// Finds the skeleton for a model. <paramref name="forceId"/> overrides the guess,
    /// which is how the interface lets a person pick a rig by hand.
    /// </summary>
    public (uint id, string? name, Pak2Entry? entry) ResolveSkeleton(string? modelPath,
        uint? forceId = null)
    {
        if (forceId is uint chosen)
        {
            _skeletons.TryGetValue(chosen, out var forced);
            return (chosen, NameOfSkeleton(chosen) ?? chosen.ToString(), forced);
        }

        string? firstGuess = null;
        foreach (string candidate in SkeletonCandidates(modelPath))
        {
            firstGuess ??= candidate;
            uint id = Fnv1a(candidate);
            if (_skeletons.TryGetValue(id, out var entry))
                return (id, candidate, entry);
        }

        // Nothing matched: report the best guess so the message can name it.
        return firstGuess is null ? (0, null, null) : (Fnv1a(firstGuess), firstGuess, null);
    }

    public string DescribeSkeleton(string? modelPath, uint? forceId = null)
    {
        var (id, name, entry) = ResolveSkeleton(modelPath, forceId);
        if (name is null)
            return "—";
        if (entry is null)
            return $"{name} (id {id}) — не найден в архиве";
        return $"{name} (id {id}, {entry.Length} B)";
    }

    // ------------------------------------------------------- skeleton listing
    private Dictionary<uint, string>? _skeletonNames;

    /// <summary>
    /// The name of every skeleton in the archive, where one can be recovered.
    /// </summary>
    /// <remarks>
    /// Skeletons are stored under a hash, not a name. The names come back by hashing
    /// candidates and looking for the hash: the character list under
    /// DATA\REFERENCESKELETONS first, then every string in the animation tables. A
    /// skeleton whose name is nowhere in those stays anonymous rather than being given
    /// an invented one.
    /// </remarks>
    public IReadOnlyDictionary<uint, string> SkeletonNames
    {
        get
        {
            if (_skeletonNames is not null)
                return _skeletonNames;

            var found = new Dictionary<uint, string>();

            void Consider(string candidate)
            {
                if (candidate.Length == 0)
                    return;
                string upper = candidate.ToUpperInvariant();
                uint id = Fnv1a(upper);
                if (_skeletons.ContainsKey(id) && !found.ContainsKey(id))
                    found[id] = upper;
            }

            foreach (string character in ReferenceSkeletons.List(_pak))
                Consider(character);

            if (found.Count < _skeletons.Count)
                foreach (string name in ClipIndex.Resolver?.AllNames ?? new List<string>())
                {
                    Consider(name);
                    // Names are namespaced; the last element is the likelier rig name.
                    int cut = name.LastIndexOfAny(new[] { '\\', '/', ':' });
                    if (cut >= 0 && cut + 1 < name.Length)
                        Consider(name[(cut + 1)..]);
                }

            _skeletonNames = found;
            return found;
        }
    }

    public string? NameOfSkeleton(uint id)
        => SkeletonNames.TryGetValue(id, out string? name) ? name : null;

    /// <summary>Every skeleton that has clips, named where possible, for a picker.</summary>
    public List<(uint id, string name, int clips)> ListSkeletons()
    {
        var counts = CountClipsPerSkeleton();
        var result = new List<(uint, string, int)>();
        foreach (var pair in _skeletons)
        {
            counts.TryGetValue(pair.Key, out int clips);
            result.Add((pair.Key, NameOfSkeleton(pair.Key) ?? $"id {pair.Key}", clips));
        }
        result.Sort((a, b) =>
        {
            // Rigs with clips first, then alphabetically: the useful ones on top.
            int byClips = b.Item3.CompareTo(a.Item3);
            return byClips != 0 ? byClips
                : string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        });
        return result;
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
    public List<AnimationClipRef> ClipsFor(string? modelPath, uint? forceSkeletonId = null)
    {
        var (id, name, skeletonEntry) = ResolveSkeleton(modelPath, forceSkeletonId);
        var result = new List<AnimationClipRef>();

        // No skeleton in the archive means no clips can belong to this model, and
        // scanning 26048 records to prove it would just be slow.
        if (name is null || skeletonEntry is null)
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

        // Every section is stored twice under the same name, as a 32-bit and a 64-bit
        // packfile, and the 64-bit copy is the larger. Without collapsing them the
        // Alien appears to have 338 containers when it has 169.
        result = result
            .GroupBy(r => r.ShortName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.Entry.Length).First())
            .ToList();

        // Real names, where the index knows them.
        var index = ClipIndex;
        foreach (var clipRef in result)
        {
            uint hash = SectionHashOf(clipRef.ShortName);
            if (hash != 0)
                clipRef.Names.AddRange(index.ForSection(hash));
        }

        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName,
            StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private AnimClipIndex? _clipIndex;

    /// <summary>
    /// Names for every clip in the game, built once and reused. Parsing the string
    /// tables and the master clip database costs a moment, so it is deferred until
    /// something actually needs a name.
    /// </summary>
    public AnimClipIndex ClipIndex => _clipIndex ??= AnimClipIndex.Load(_pak);

    /// <summary>
    /// Puts the recovered names onto the clips a section decoded to.
    /// </summary>
    /// <remarks>
    /// Havok hands the animations back in storage order, and the master clip database
    /// lists them in the same order, so position lines them up. Verified on the Alien:
    /// across the sections checked, the count the index predicts and the count Havok
    /// returns agreed every time, and the names describe what the clips measure --
    /// everything ending in LOOP came back as exactly one second, everything ending in
    /// IDLE as a two frame hold.
    /// </remarks>
    public static int ApplyNames(AnimationClipRef clipRef, AnimationBundle bundle)
    {
        int applied = 0;
        for (int i = 0; i < bundle.Clips.Count && i < clipRef.Names.Count; i++)
        {
            bundle.Clips[i].Name = clipRef.Names[i].FullName;
            bundle.Clips[i].Container = clipRef.ShortName;
            applied++;
        }
        return applied;
    }

    /// <summary>The hash in an <c>ANIM_CLIP_DB_SEC_&lt;hash&gt;.BIN</c> file name.</summary>
    public static uint SectionHashOf(string sectionFileName)
    {
        const string prefix = "ANIM_CLIP_DB_SEC_";
        int at = sectionFileName.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return 0;
        string digits = sectionFileName[(at + prefix.Length)..];
        int dot = digits.IndexOf('.');
        if (dot >= 0)
            digits = digits[..dot];
        return uint.TryParse(digits, out uint hash) ? hash : 0u;
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
    public byte[]? ExtractSkeletonHavok(string? modelPath, uint? forceSkeletonId = null)
    {
        var (_, _, entry) = ResolveSkeleton(modelPath, forceSkeletonId);
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
