namespace AlienForge.Core;

/// <summary>
/// Turns in-game asset paths into safe file names.
/// </summary>
public static class AssetNaming
{
    private static readonly char[] Illegal =
        { '\\', '/', ':', '*', '?', '"', '<', '>', '|', '[', ']' };

    /// <summary>
    /// Flattens an asset path into one file name component. Texture names look like
    /// <c>ayz\characters\primary\alien\alien_body[d].tga</c>, where the bracketed
    /// letter is the map role, so brackets are folded away too.
    /// </summary>
    public static string Flatten(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "unnamed";
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(Illegal, chars[i]) >= 0 || char.IsControl(chars[i]))
                chars[i] = '_';
        }
        return new string(chars).Trim();
    }

    /// <summary>Flattened name with the decoded size appended, as PNG.</summary>
    public static string TextureFileName(string name, int width, int height)
        => $"{Flatten(name)}_{width}x{height}.png";

    /// <summary>Keeps the directory shape of an asset path but makes each part safe.</summary>
    public static string RelativePath(string name, string extension)
    {
        string normalised = (name ?? string.Empty).Replace('/', '\\');
        var parts = normalised.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "unnamed" + extension;

        for (int i = 0; i < parts.Length; i++)
            parts[i] = Flatten(parts[i]);

        string last = Path.GetFileNameWithoutExtension(parts[^1]);
        parts[^1] = (string.IsNullOrEmpty(last) ? parts[^1] : last) + extension;
        return Path.Combine(parts);
    }
}
