using System.Diagnostics;
using CATHODE;

namespace AlienForge.Core;

/// <summary>
/// One level's asset archives, loaded in dependency order, plus the shared GLOBAL
/// texture pack that its materials reach into.
/// </summary>
/// <remarks>
/// The load order matters and is not interchangeable: materials need both texture
/// packs and the shader table before they can resolve their own references, and
/// models need the material table before their submeshes can point at one. This
/// mirrors how CathodeLib's own Level class wires things up.
/// </remarks>
public sealed class AssetWorkspace
{
    private AssetWorkspace(GameInstall install, LevelRef level, Textures globalTextures)
    {
        Install = install;
        Level = level;
        GlobalTextures = globalTextures;
    }

    public GameInstall Install { get; }
    public LevelRef Level { get; }
    public string Name => Level.Name;

    /// <summary>Shared pack from DATA/ENV/GLOBAL. Reused across levels.</summary>
    public Textures GlobalTextures { get; }

    public Textures LevelTextures { get; private set; } = null!;
    public Shaders Shaders { get; private set; } = null!;
    public Materials Materials { get; private set; } = null!;
    public Models Models { get; private set; } = null!;
    public Collisions Collisions { get; private set; } = null!;
    public MorphTargets MorphTargets { get; private set; } = null!;

    /// <summary>Human-readable notes about what loaded and what did not.</summary>
    public List<string> Log { get; } = new();

    public TimeSpan LoadTime { get; private set; }

    public int ModelCount => Models.Entries.Count;
    public int MaterialCount => Materials.Entries.Count;
    public int LevelTextureCount => LevelTextures.Entries.Count;
    public int GlobalTextureCount => GlobalTextures.Entries.Count;

    // ------------------------------------------------------------------ load
    /// <summary>
    /// Loads the shared texture pack once so it can be handed to several levels.
    /// It is 88 MB, so reloading it per level would be wasteful.
    /// </summary>
    public static Textures LoadGlobalTextures(GameInstall install)
        => new(install.GlobalTexturesPak);

    public static AssetWorkspace OpenLevel(GameInstall install, LevelRef level,
        Textures? globalTextures = null, Action<LoadStage>? onProgress = null)
    {
        var sw = Stopwatch.StartNew();
        onProgress?.Invoke(LoadStage.GlobalTextures);
        globalTextures ??= LoadGlobalTextures(install);
        var ws = new AssetWorkspace(install, level, globalTextures);

        ws.Note(globalTextures.Loaded,
            $"GLOBAL_TEXTURES.ALL.PAK: {globalTextures.Entries.Count} записей");
        onProgress?.Invoke(LoadStage.LevelTextures);

        ws.LevelTextures = new Textures(level.TexturesPak);
        ws.Note(ws.LevelTextures.Loaded,
            $"{Path.GetFileName(level.TexturesPak)}: {ws.LevelTextures.Entries.Count} записей");

        onProgress?.Invoke(LoadStage.Shaders);
        ws.Shaders = new Shaders(level.ShadersPak);
        ws.Note(ws.Shaders.Loaded, $"{Path.GetFileName(level.ShadersPak)}");

        onProgress?.Invoke(LoadStage.Collisions);
        ws.Collisions = new Collisions(level.CollisionBin);
        ws.Note(ws.Collisions.Loaded, $"{Path.GetFileName(level.CollisionBin)}");
        ws.MorphTargets = new MorphTargets(level.MorphTargetBin);
        ws.Note(ws.MorphTargets.Loaded, $"{Path.GetFileName(level.MorphTargetBin)}");

        // Materials must come after both texture packs and the shaders, models
        // after the materials: each resolves indices into the previous one.
        onProgress?.Invoke(LoadStage.Materials);
        ws.Materials = new Materials(level.ModelsMtl, ws.GlobalTextures, ws.LevelTextures, ws.Shaders);
        ws.Note(ws.Materials.Loaded,
            $"{Path.GetFileName(level.ModelsMtl)}: {ws.Materials.Entries.Count} материалов");

        onProgress?.Invoke(LoadStage.Models);
        ws.Models = new Models(level.ModelsPak, ws.Materials, ws.Collisions, ws.MorphTargets);
        ws.Note(ws.Models.Loaded,
            $"{Path.GetFileName(level.ModelsPak)}: {ws.Models.Entries.Count} моделей");

        sw.Stop();
        ws.LoadTime = sw.Elapsed;
        onProgress?.Invoke(LoadStage.Done);
        return ws;
    }

    private void Note(bool ok, string what)
        => Log.Add($"[{(ok ? "ok " : "нет")}] {what}");

    /// <summary>True when there is geometry to work with.</summary>
    public bool HasModels => Models.Loaded && Models.Entries.Count > 0;

    // --------------------------------------------------------------- queries
    /// <summary>
    /// Model entries whose name contains all of the given fragments, case-insensitively.
    /// </summary>
    public IEnumerable<Models.CS2> FindModels(params string[] fragments)
    {
        foreach (var cs2 in Models.Entries)
        {
            string name = cs2.Name ?? string.Empty;
            bool all = true;
            foreach (string f in fragments)
                if (name.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    all = false;
                    break;
                }
            if (all)
                yield return cs2;
        }
    }

    /// <summary>
    /// Exact match on the tail of the entry path, e.g. "CHARACTERS\ALIEN\model0.cs2".
    /// Slashes are normalised so either separator works.
    /// </summary>
    public Models.CS2? FindModelByPath(string tail)
    {
        string want = tail.Replace('/', '\\');
        foreach (var cs2 in Models.Entries)
        {
            string name = (cs2.Name ?? string.Empty).Replace('/', '\\');
            if (name.EndsWith(want, StringComparison.OrdinalIgnoreCase))
                return cs2;
        }
        return null;
    }

    /// <summary>Distinct materials referenced by a model's submeshes, in first-use order.</summary>
    public List<Materials.Material> MaterialsOf(Models.CS2 model)
    {
        var result = new List<Materials.Material>();
        foreach (var comp in model.Components)
            foreach (var lod in comp.LODs)
                foreach (var sm in lod.Submeshes)
                    if (sm.Material is not null && !result.Contains(sm.Material))
                        result.Add(sm.Material);
        return result;
    }

    /// <summary>
    /// Distinct textures a model reaches through its materials, with the slot index
    /// and which pack each came from.
    /// </summary>
    public List<TextureUse> TexturesOf(Models.CS2 model)
    {
        var result = new List<TextureUse>();
        foreach (var mat in MaterialsOf(model))
            for (int slot = 0; slot < mat.TextureReferences.Count; slot++)
            {
                var ptr = mat.TextureReferences[slot];
                if (ptr?.Texture is null)
                    continue;
                if (result.Any(u => ReferenceEquals(u.Texture, ptr.Texture)))
                    continue;
                result.Add(new TextureUse(ptr.Texture, mat, slot, ptr.Location));
            }
        return result;
    }

    public int SubmeshCount(Models.CS2 model)
    {
        int n = 0;
        foreach (var comp in model.Components)
            foreach (var lod in comp.LODs)
                n += lod.Submeshes.Count;
        return n;
    }
}

/// <summary>
/// Stages of a level load, reported so a caller can show progress. Named rather than
/// described in words so the wording stays with the user interface and can be
/// translated there.
/// </summary>
public enum LoadStage
{
    GlobalTextures,
    LevelTextures,
    Shaders,
    Collisions,
    Materials,
    Models,
    Done,
}

/// <summary>A texture as reached from a specific material slot.</summary>
public readonly record struct TextureUse(
    Textures.TEX4 Texture,
    Materials.Material Material,
    int Slot,
    TexturePtr.Source Source)
{
    public string Name => Texture.Name ?? string.Empty;
}
