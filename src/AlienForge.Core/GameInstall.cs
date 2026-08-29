using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AlienForge.Core;

/// <summary>
/// A located Alien: Isolation installation, plus the layout knowledge needed to
/// find its asset archives.
/// </summary>
public sealed class GameInstall
{
    public const string SteamAppName = "Alien Isolation";

    private GameInstall(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string DataDir => Path.Combine(Root, "DATA");

    /// <summary>
    /// Shared data. Note what is NOT here: on the Steam build GLOBAL_MODELS.PAK is
    /// a 32 byte stub and GLOBAL_MODELS.MTL is 52 bytes, so no geometry lives in
    /// GLOBAL. Characters ship inside the LEVEL_MODELS.PAK of every level that
    /// uses them. GLOBAL only supplies the shared texture pack (~88 MB).
    /// </summary>
    public string GlobalDir => Path.Combine(DataDir, "ENV", "GLOBAL");

    public string GlobalTexturesPak => Path.Combine(GlobalDir, "WORLD", "GLOBAL_TEXTURES.ALL.PAK");

    public string ProductionDir => Path.Combine(DataDir, "ENV", "PRODUCTION");

    /// <summary>DATA/GLOBAL/ANIMATION.PAK, ~451 MB, every animation in the game.</summary>
    public string AnimationPak => Path.Combine(DataDir, "GLOBAL", "ANIMATION.PAK");

    public string AnimSysDir => Path.Combine(DataDir, "ANIM_SYS");

    public override string ToString() => Root;

    // ------------------------------------------------------------------ open
    /// <summary>
    /// Wrap a path the user picked. Does not validate; call <see cref="Validate"/>.
    /// </summary>
    public static GameInstall Open(string root) => new(root);

    /// <summary>
    /// Reports why this path is not a usable install, or null when it looks fine.
    /// Checked against files this tool actually reads, not a guessed marker file.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Root))
            return "Путь не задан.";
        if (!Directory.Exists(Root))
            return $"Папки не существует: {Root}";
        if (!Directory.Exists(DataDir))
            return $"Нет подпапки DATA: похоже, это не корень игры ({Root}).";
        if (!Directory.Exists(GlobalDir))
            return $"Нет DATA\\ENV\\GLOBAL — без него не прочитать общие ассеты.";

        // Checked against the shared texture pack, not GLOBAL_MODELS.PAK: the
        // latter exists but is an empty stub, so its presence proves nothing.
        if (!File.Exists(GlobalTexturesPak))
            return $"Не найден {GlobalTexturesPak}.";

        if (!Directory.Exists(ProductionDir))
            return $"Нет DATA\\ENV\\PRODUCTION — уровней не найдётся.";

        return null;
    }

    public bool IsValid => Validate() is null;

    // ---------------------------------------------------------------- detect
    /// <summary>
    /// Every plausible install directory found on this machine, best guess first.
    /// Looks through the Steam registry keys and every Steam library folder.
    /// </summary>
    public static List<string> FindCandidates()
    {
        var found = new List<string>();

        foreach (string steam in FindSteamRoots())
        {
            foreach (string library in FindSteamLibraries(steam))
            {
                string candidate = Path.Combine(library, "steamapps", "common", SteamAppName);
                if (Directory.Exists(candidate))
                    AddUnique(found, candidate);
            }
        }

        // Fall back to the usual hard-coded spots in case the registry is unhelpful.
        foreach (string guess in DefaultGuesses())
            if (Directory.Exists(guess))
                AddUnique(found, guess);

        return found;
    }

    /// <summary>First candidate that passes validation, or null.</summary>
    public static GameInstall? AutoDetect()
    {
        foreach (string candidate in FindCandidates())
        {
            var install = new GameInstall(candidate);
            if (install.IsValid)
                return install;
        }
        return null;
    }

    private static IEnumerable<string> DefaultGuesses()
    {
        foreach (char drive in "CDEFGH")
        {
            yield return $@"{drive}:\Program Files (x86)\Steam\steamapps\common\{SteamAppName}";
            yield return $@"{drive}:\Program Files\Steam\steamapps\common\{SteamAppName}";
            yield return $@"{drive}:\SteamLibrary\steamapps\common\{SteamAppName}";
            yield return $@"{drive}:\Games\Steam\steamapps\common\{SteamAppName}";
        }
    }

    private static List<string> FindSteamRoots()
    {
        var roots = new List<string>();

        // Values are written by the Steam client itself; missing keys are normal.
        TryRegistry(RegistryHive.CurrentUser, RegistryView.Default,
            @"Software\Valve\Steam", "SteamPath", roots);
        TryRegistry(RegistryHive.LocalMachine, RegistryView.Registry32,
            @"SOFTWARE\Valve\Steam", "InstallPath", roots);
        TryRegistry(RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\Valve\Steam", "InstallPath", roots);

        return roots;
    }

    private static void TryRegistry(RegistryHive hive, RegistryView view,
        string subKey, string valueName, List<string> into)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? key = baseKey.OpenSubKey(subKey);
            if (key?.GetValue(valueName) is string path && !string.IsNullOrWhiteSpace(path))
                AddUnique(into, path.Replace('/', '\\'));
        }
        catch (Exception)
        {
            // No access or no such key: just means we fall back to the guesses.
        }
    }

    /// <summary>
    /// Steam keeps extra install drives in steamapps\libraryfolders.vdf. Both the
    /// old ("1" "D:\\SteamLibrary") and new ("path" "D:\\SteamLibrary") shapes put
    /// the directory in a quoted string, so one pattern covers them.
    /// </summary>
    private static List<string> FindSteamLibraries(string steamRoot)
    {
        var libs = new List<string> { steamRoot };

        foreach (string vdf in new[]
                 {
                     Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
                 })
        {
            if (!File.Exists(vdf))
                continue;
            string text;
            try
            {
                text = File.ReadAllText(vdf);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (Match m in Regex.Matches(text, "\"(?:path|[0-9]+)\"\\s*\"([^\"]+)\""))
            {
                string raw = m.Groups[1].Value.Replace(@"\\", @"\");
                // Numeric keys also hold non-path values such as sizes; keep real dirs only.
                if (raw.Length > 2 && raw[1] == ':' && Directory.Exists(raw))
                    AddUnique(libs, raw);
            }
        }

        return libs;
    }

    private static void AddUnique(List<string> list, string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path.TrimEnd('\\', '/'));
        }
        catch (Exception)
        {
            return;
        }
        if (!list.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase)))
            list.Add(full);
    }

    // ---------------------------------------------------------------- levels
    /// <summary>
    /// Every level that actually has the archives we need. Sorted by name.
    /// </summary>
    public List<LevelRef> EnumerateLevels()
    {
        var levels = new List<LevelRef>();
        if (!Directory.Exists(ProductionDir))
            return levels;

        foreach (string dir in Directory.EnumerateDirectories(ProductionDir))
        {
            var candidate = LevelRef.Probe(dir);
            if (candidate is not null)
                levels.Add(candidate);
        }

        levels.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return levels;
    }
}

/// <summary>
/// One level directory under DATA/ENV/PRODUCTION, with the archive paths resolved
/// (levels ship either plain or compressed, and the suffixes differ per file).
/// </summary>
public sealed class LevelRef
{
    private LevelRef(string dir, bool compressed)
    {
        Directory = dir;
        Name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        Compressed = compressed;
    }

    public string Directory { get; }
    public string Name { get; }

    /// <summary>A compressed level uses .FZIP for PAKs and .GZ for BINs.</summary>
    public bool Compressed { get; }

    public string RenderableDir => Path.Combine(Directory, "RENDERABLE");
    public string WorldDir => Path.Combine(Directory, "WORLD");

    public string TexturesPak => Path.Combine(RenderableDir, "LEVEL_TEXTURES.ALL.PAK" + (Compressed ? ".FZIP" : ""));
    public string ShadersPak => Path.Combine(RenderableDir, "LEVEL_SHADERS_DX11.PAK" + (Compressed ? ".GZ" : ""));
    public string ModelsMtl => Path.Combine(RenderableDir, "LEVEL_MODELS.MTL" + (Compressed ? ".GZ" : ""));
    public string ModelsPak => Path.Combine(RenderableDir, "LEVEL_MODELS.PAK" + (Compressed ? ".FZIP" : ""));
    public string CollisionBin => Path.Combine(WorldDir, "COLLISION.BIN" + (Compressed ? ".GZ" : ""));
    public string MorphTargetBin => Path.Combine(WorldDir, "MORPH_TARGET_DB.BIN");

    public override string ToString() => Name;

    /// <summary>
    /// Returns a level only if the model archive is present, which is the file
    /// everything else hangs off. Mirrors how CathodeLib decides a level is real.
    /// </summary>
    public static LevelRef? Probe(string dir)
    {
        string renderable = Path.Combine(dir, "RENDERABLE");
        bool compressed = File.Exists(Path.Combine(renderable, "LEVEL_TEXTURES.ALL.PAK.FZIP"));

        var candidate = new LevelRef(dir, compressed);
        if (File.Exists(candidate.ModelsPak))
            return candidate;

        // Try the other compression flavour before giving up.
        var flipped = new LevelRef(dir, !compressed);
        return File.Exists(flipped.ModelsPak) ? flipped : null;
    }
}
