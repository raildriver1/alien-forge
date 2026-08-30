using System.Buffers.Binary;

namespace AlienForge.Core.Animation;

/// <summary>One clip of the game, with its name and where its data lives.</summary>
public sealed class ClipNameEntry
{
    public required string FullName { get; init; }
    public required uint SectionHash { get; init; }

    /// <summary>Row this clip occupies in the master table, which is also its id.</summary>
    public required int Row { get; init; }

    /// <summary>Position among the clips sharing the same section.</summary>
    public int IndexInSection { get; set; }

    /// <summary>Last path element, which is what a person reads.</summary>
    public string ShortName
    {
        get
        {
            int cut = FullName.LastIndexOfAny(new[] { '\\', '/' });
            return cut >= 0 && cut + 1 < FullName.Length ? FullName[(cut + 1)..] : FullName;
        }
    }

    /// <summary>Folder the clip sits in, useful for grouping a list by purpose.</summary>
    public string Category
    {
        get
        {
            int cut = FullName.LastIndexOfAny(new[] { '\\', '/' });
            return cut > 0 ? FullName[..cut] : string.Empty;
        }
    }

    public override string ToString() => FullName;
}

/// <summary>
/// Names every animation in the game and says which section holds it.
/// </summary>
/// <remarks>
/// This is the join that makes the animation archive readable, and it needs three
/// files, all inside ANIMATION.PAK.
/// <list type="number">
/// <item>
/// <c>DATA\ANIM_SYS\ANIM_STRING_DB.BIN</c> and its larger debug twin hold every
/// string the animation system uses, 83004 of them together, addressed by hash.
/// </item>
/// <item>
/// <c>DATA\ANIM_SYS\ANIM_CLIP_DB.BIN</c> opens with a descending hash table of 14975
/// pairs: the hash of a clip's full name against the row it occupies. All 14975 resolve
/// against the string tables, which is how the naming is known to be right rather than
/// merely plausible.
/// </item>
/// <item>
/// The rest of that same file is 14975 rows of eight bytes, and a row's first four
/// bytes are the hash of the <c>ANIM_CLIP_DB_SEC_&lt;hash&gt;.BIN</c> section that
/// carries the clip. Row order therefore gives clip order inside a section.
/// </item>
/// </list>
/// Sections are stored twice, as a 32-bit and a 64-bit packfile under the same name,
/// so the archive's 338 records for the Alien are 169 real sections. Those 169 carry
/// 488 clips: one large section holds 320 and the remaining 168 hold one each.
/// </remarks>
public sealed class AnimClipIndex
{
    private readonly Dictionary<uint, List<ClipNameEntry>> _bySection = new();
    private readonly Dictionary<string, ClipNameEntry> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    private AnimClipIndex() { }

    public List<ClipNameEntry> All { get; } = new();

    public int ClipCount => All.Count;

    /// <summary>Pairs in the master table that no string table could name.</summary>
    public int Unnamed { get; private set; }

    public AnimNameResolver? Resolver { get; private set; }

    /// <summary>Clips carried by one section, in the order the master table lists them.</summary>
    public IReadOnlyList<ClipNameEntry> ForSection(uint sectionHash)
        => _bySection.TryGetValue(sectionHash, out var list)
            ? list
            : Array.Empty<ClipNameEntry>();

    public ClipNameEntry? ByName(string fullName)
        => _byName.TryGetValue(fullName, out var entry) ? entry : null;

    /// <summary>Name of the clip at a position inside a section, if it is known.</summary>
    public string? NameAt(uint sectionHash, int indexInSection)
    {
        var list = ForSection(sectionHash);
        return indexInSection >= 0 && indexInSection < list.Count
            ? list[indexInSection].FullName
            : null;
    }

    public static AnimClipIndex Load(Pak2Index archive, AnimNameResolver? resolver = null)
    {
        var index = new AnimClipIndex();
        index.Resolver = resolver ?? AnimNameResolver.Load(archive);

        var master = archive.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(@"ANIM_SYS\ANIM_CLIP_DB.BIN", StringComparison.OrdinalIgnoreCase));
        if (master is null)
            return index;

        byte[] data = archive.Read(master);
        var table = HashTableFile.TryParse(data);
        if (table is null)
            return index;

        // Row i starts at the first byte after the pair table and runs eight bytes.
        int rowsAt = table.TailAt;
        foreach (var (key, row) in table.Pairs())
        {
            string? name = index.Resolver.Name(key);
            if (name is null)
            {
                index.Unnamed++;
                continue;
            }

            int at = rowsAt + (int)row * 8;
            if (row >= (uint)table.Count || at + 4 > data.Length)
            {
                index.Unnamed++;
                continue;
            }

            uint section = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at, 4));
            var entry = new ClipNameEntry
            {
                FullName = name,
                SectionHash = section,
                Row = (int)row,
            };
            index.All.Add(entry);
            index._byName[name] = entry;

            if (!index._bySection.TryGetValue(section, out var list))
            {
                list = new List<ClipNameEntry>();
                index._bySection[section] = list;
            }
            list.Add(entry);
        }

        // Within a section, the row number orders the clips. Havok hands them back in
        // the order they are stored, so sorting by row lines the two up.
        foreach (var list in index._bySection.Values)
        {
            list.Sort((a, b) => a.Row.CompareTo(b.Row));
            for (int i = 0; i < list.Count; i++)
                list[i].IndexInSection = i;
        }
        return index;
    }
}

/// <summary>
/// Checks a naming against the clips it claims to describe.
/// </summary>
/// <remarks>
/// Pairing names with clips by position is a hypothesis, and this one can be tested,
/// because the names say what the clips should look like. Something called
/// <c>POSE_*</c> is a single held pose and must be very short. An <c>ADDITIVE_*</c>
/// clip is meant to be layered over another and should carry Havok's additive blend
/// hint, while a plain clip should not. <c>WALK_*</c> and <c>STRAFE_*</c> are
/// locomotion cycles of a second or two. When those hold, the naming is right; when
/// they do not, saying so beats showing confident nonsense.
/// </remarks>
public static class ClipNameCheck
{
    public sealed class Result
    {
        public int Checked { get; set; }
        public int PoseClips { get; set; }
        public double PoseMeanSeconds { get; set; }
        public int AdditiveNamed { get; set; }
        public int AdditiveFlagged { get; set; }
        public int PlainNamed { get; set; }
        public int PlainFlagged { get; set; }
        public int LocomotionClips { get; set; }
        public double LocomotionMeanSeconds { get; set; }

        public bool Plausible
            => Checked > 0
               && (PoseClips == 0 || PoseMeanSeconds < 0.5)
               && (AdditiveNamed == 0 || AdditiveFlagged * 100 / Math.Max(1, AdditiveNamed) > 60)
               && (PlainNamed == 0 || PlainFlagged * 100 / Math.Max(1, PlainNamed) < 40);

        public override string ToString()
            => $"проверено {Checked}; pose {PoseClips} шт., средняя {PoseMeanSeconds:F3} с; " +
               $"additive помечены аддитивными {AdditiveFlagged}/{AdditiveNamed}; " +
               $"обычные оказались аддитивными {PlainFlagged}/{PlainNamed}; " +
               $"локомоция {LocomotionClips} шт., средняя {LocomotionMeanSeconds:F2} с; " +
               $"вывод: {(Plausible ? "имена согласуются с данными" : "имена НЕ согласуются")}";
    }

    public static Result Evaluate(IEnumerable<(string name, ClipData clip)> named)
    {
        var result = new Result();
        double poseTotal = 0, locomotionTotal = 0;

        foreach (var (name, clip) in named)
        {
            result.Checked++;
            string upper = name.ToUpperInvariant();

            // A single held pose is named POSE_SOMETHING. A name like
            // ALIEN_COVERMAG_POSES is a set of them and runs as long as any clip, so
            // matching "POSE" anywhere would fail a naming that is in fact correct.
            string leaf = upper[(upper.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
            if (leaf.StartsWith("POSE_", StringComparison.Ordinal))
            {
                result.PoseClips++;
                poseTotal += clip.Duration;
            }

            if (leaf.StartsWith("ADDITIVE", StringComparison.Ordinal)
                || upper.Contains("\\ADDITIVE", StringComparison.Ordinal))
            {
                result.AdditiveNamed++;
                if (clip.IsAdditive)
                    result.AdditiveFlagged++;
            }
            else
            {
                result.PlainNamed++;
                if (clip.IsAdditive)
                    result.PlainFlagged++;
            }

            if (upper.Contains("\\LOCOMOTION\\", StringComparison.Ordinal))
            {
                result.LocomotionClips++;
                locomotionTotal += clip.Duration;
            }
        }

        if (result.PoseClips > 0)
            result.PoseMeanSeconds = poseTotal / result.PoseClips;
        if (result.LocomotionClips > 0)
            result.LocomotionMeanSeconds = locomotionTotal / result.LocomotionClips;
        return result;
    }
}
