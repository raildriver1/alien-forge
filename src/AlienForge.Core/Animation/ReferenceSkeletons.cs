using System.Text;

namespace AlienForge.Core.Animation;

/// <summary>A named set of bones, as the game groups them for animation blending.</summary>
public sealed class BoneGroup
{
    public required string Name { get; init; }

    /// <summary>Bone names, as written in the file: <c>Alien:Tail1</c> and so on.</summary>
    public List<string> Bones { get; } = new();

    /// <summary>Named blend weights the group drives, e.g. <c>pose_Tail_Curl_Up</c>.</summary>
    public List<string> Floats { get; } = new();

    public override string ToString() => $"{Name}: костей {Bones.Count}, весов {Floats.Count}";
}

/// <summary>
/// The bone names and left/right pairing for one character.
/// </summary>
/// <remarks>
/// The shipped Havok skeletons have their bone names stripped, so the rig arrives as
/// 127 nameless bones. The names survive in plain text elsewhere in ANIMATION.PAK,
/// under DATA\REFERENCESKELETONS: one <c>*_BONE_GROUPS.TXT</c> per character listing
/// bones by body part, and one <c>*_MIRROR.TXT</c> pairing the left side with the
/// right. Together they say what a character's skeleton is made of, which is what
/// makes an animation list readable.
/// </remarks>
public sealed class ReferenceSkeleton
{
    public required string Character { get; init; }
    public List<BoneGroup> Groups { get; } = new();
    public List<string> LeftBones { get; } = new();
    public List<string> RightBones { get; } = new();

    /// <summary>Every bone named anywhere in the two files, in first-seen order.</summary>
    public List<string> AllBones { get; } = new();

    public int TotalBones => AllBones.Count;

    /// <summary>The group a bone belongs to, or null when it is only in the mirror list.</summary>
    public string? GroupOf(string bone)
    {
        foreach (var group in Groups)
            if (group.Bones.Contains(bone, StringComparer.OrdinalIgnoreCase))
                return group.Name;
        return null;
    }

    /// <summary>Mirror partner of a bone, or null when it sits on the centre line.</summary>
    public string? MirrorOf(string bone)
    {
        int left = LeftBones.FindIndex(b => string.Equals(b, bone, StringComparison.OrdinalIgnoreCase));
        if (left >= 0 && left < RightBones.Count)
            return RightBones[left];
        int right = RightBones.FindIndex(b => string.Equals(b, bone, StringComparison.OrdinalIgnoreCase));
        if (right >= 0 && right < LeftBones.Count)
            return LeftBones[right];
        return null;
    }
}

/// <summary>Reads the reference skeleton text files out of ANIMATION.PAK.</summary>
public static class ReferenceSkeletons
{
    private const string Folder = @"DATA\REFERENCESKELETONS\";

    /// <summary>Character names that have a reference skeleton, e.g. ALIEN, RIPLEY.</summary>
    public static List<string> List(Pak2Index archive)
    {
        var names = new List<string>();
        foreach (var entry in archive.Entries)
        {
            string name = entry.Name.Replace('/', '\\');
            if (!name.StartsWith(Folder, StringComparison.OrdinalIgnoreCase))
                continue;
            string leaf = name[Folder.Length..];
            const string suffix = "_BONE_GROUPS.TXT";
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                names.Add(leaf[..^suffix.Length]);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Loads one character. <paramref name="character"/> is the bare name, so ALIEN
    /// rather than a path.
    /// </summary>
    public static ReferenceSkeleton? Load(Pak2Index archive, string character)
    {
        var result = new ReferenceSkeleton { Character = character.ToUpperInvariant() };

        string? groupsText = ReadText(archive, $"{Folder}{result.Character}_BONE_GROUPS.TXT");
        if (groupsText is null)
            return null;
        ParseGroups(groupsText, result);

        string? mirrorText = ReadText(archive, $"{Folder}{result.Character}_MIRROR.TXT");
        if (mirrorText is not null)
            ParseMirror(mirrorText, result);

        return result;
    }

    private static string? ReadText(Pak2Index archive, string path)
    {
        foreach (var entry in archive.Entries)
        {
            if (!string.Equals(entry.Name.Replace('/', '\\'), path, StringComparison.OrdinalIgnoreCase))
                continue;
            return Encoding.ASCII.GetString(archive.Read(entry));
        }
        return null;
    }

    /// <summary>
    /// Format is line based: <c>BEGIN_GROUP:HIPS</c>, then <c>BONE:Alien:Hips</c> or
    /// <c>FLOAT:pose_Tail_Idle</c> lines, closed by <c>END_GROUP</c>.
    /// </summary>
    private static void ParseGroups(string text, ReferenceSkeleton result)
    {
        BoneGroup? current = null;
        foreach (string raw in SplitLines(text))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("BEGIN_GROUP:", StringComparison.OrdinalIgnoreCase))
            {
                current = new BoneGroup { Name = line["BEGIN_GROUP:".Length..].Trim() };
                result.Groups.Add(current);
            }
            else if (line.StartsWith("END_GROUP", StringComparison.OrdinalIgnoreCase))
            {
                current = null;
            }
            else if (line.StartsWith("BONE:", StringComparison.OrdinalIgnoreCase))
            {
                string bone = line["BONE:".Length..].Trim();
                current?.Bones.Add(bone);
                Remember(result, bone);
            }
            else if (line.StartsWith("FLOAT:", StringComparison.OrdinalIgnoreCase))
            {
                current?.Floats.Add(line["FLOAT:".Length..].Trim());
            }
        }
    }

    /// <summary>
    /// Two sections, <c>BEGIN_LEFT_BONES:</c> and <c>BEGIN_RIGHT_BONES:</c>, each a
    /// bare list. The two lists line up index for index.
    /// </summary>
    private static void ParseMirror(string text, ReferenceSkeleton result)
    {
        List<string>? target = null;
        foreach (string raw in SplitLines(text))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("BEGIN_LEFT_BONES", StringComparison.OrdinalIgnoreCase))
                target = result.LeftBones;
            else if (line.StartsWith("BEGIN_RIGHT_BONES", StringComparison.OrdinalIgnoreCase))
                target = result.RightBones;
            else if (line.StartsWith("END_", StringComparison.OrdinalIgnoreCase))
                target = null;
            else if (target is not null)
            {
                target.Add(line);
                Remember(result, line);
            }
        }
    }

    private static void Remember(ReferenceSkeleton result, string bone)
    {
        if (!result.AllBones.Contains(bone, StringComparer.OrdinalIgnoreCase))
            result.AllBones.Add(bone);
    }

    private static IEnumerable<string> SplitLines(string text)
        => text.Split('\n', '\r');
}
