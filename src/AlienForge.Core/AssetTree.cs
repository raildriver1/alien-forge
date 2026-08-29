using CATHODE;
using AlienForge.Core.Imaging;

namespace AlienForge.Core;

public enum AssetKind
{
    Level,
    Folder,
    Model,
    Component,
    Lod,
    Submesh,
    Material,
    Texture,
    Shader,
    Note,
}

/// <summary>
/// One node of the browsable asset tree.
/// </summary>
/// <remarks>
/// Children are produced on demand. A single level holds hundreds of models, each
/// with components, LODs and submeshes, so building the whole thing up front would
/// mean decoding far more than the user ever looks at.
/// </remarks>
public sealed class AssetNode
{
    private readonly Func<List<AssetNode>>? _factory;
    private List<AssetNode>? _children;

    public AssetNode(AssetKind kind, string name, string? detail = null,
        object? payload = null, Func<List<AssetNode>>? factory = null)
    {
        Kind = kind;
        Name = name;
        Detail = detail;
        Payload = payload;
        _factory = factory;
    }

    public AssetKind Kind { get; }
    public string Name { get; }

    /// <summary>Short right-hand summary, e.g. counts or a texture format.</summary>
    public string? Detail { get; }

    /// <summary>The underlying CathodeLib object, when there is one.</summary>
    public object? Payload { get; }

    /// <summary>True when this node can have children without building them.</summary>
    public bool HasChildren => _factory is not null || (_children?.Count ?? 0) > 0;

    public IReadOnlyList<AssetNode> Children
    {
        get
        {
            if (_children is null)
                _children = _factory?.Invoke() ?? new List<AssetNode>();
            return _children;
        }
    }

    public override string ToString()
        => Detail is null ? Name : $"{Name}  —  {Detail}";
}

/// <summary>
/// Builds the two trees the browser shows: the archive contents, and what a single
/// model depends on.
/// </summary>
public static class AssetTree
{
    /// <summary>
    /// Archive contents, grouped by the folder structure encoded in entry names
    /// (they look like <c>CHARACTERS\ALIEN\model0.cs2</c>).
    /// </summary>
    public static AssetNode BuildArchiveTree(AssetWorkspace ws, string? filter = null)
    {
        var models = (filter is null
            ? ws.Models.Entries.AsEnumerable()
            : ws.FindModels(filter)).ToList();

        var root = new AssetNode(AssetKind.Level, ws.Name,
            $"{models.Count} моделей, {ws.MaterialCount} материалов, " +
            $"{ws.LevelTextureCount}+{ws.GlobalTextureCount} текстур",
            ws, () => GroupByFolder(ws, models, prefixLength: 0));
        return root;
    }

    /// <summary>
    /// Groups entries by the path segment at <paramref name="prefixLength"/>, then
    /// recurses. Entries that end at this depth become model nodes.
    /// </summary>
    private static List<AssetNode> GroupByFolder(AssetWorkspace ws,
        List<Models.CS2> models, int prefixLength)
    {
        var folders = new Dictionary<string, List<Models.CS2>>(StringComparer.OrdinalIgnoreCase);
        var leaves = new List<Models.CS2>();

        foreach (var cs2 in models)
        {
            string[] parts = SplitName(cs2);
            if (parts.Length <= prefixLength + 1)
            {
                leaves.Add(cs2);
                continue;
            }
            string segment = parts[prefixLength];
            if (!folders.TryGetValue(segment, out var bucket))
                folders[segment] = bucket = new List<Models.CS2>();
            bucket.Add(cs2);
        }

        var result = new List<AssetNode>();
        foreach (var kv in folders.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var bucket = kv.Value;
            result.Add(new AssetNode(AssetKind.Folder, kv.Key, $"{bucket.Count}",
                null, () => GroupByFolder(ws, bucket, prefixLength + 1)));
        }
        foreach (var cs2 in leaves.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            result.Add(ModelNode(ws, cs2));
        return result;
    }

    private static string[] SplitName(Models.CS2 cs2)
        => (cs2.Name ?? string.Empty).Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);

    private static AssetNode ModelNode(AssetWorkspace ws, Models.CS2 cs2)
    {
        string[] parts = SplitName(cs2);
        string leaf = parts.Length > 0 ? parts[^1] : (cs2.Name ?? "model");
        int subs = ws.SubmeshCount(cs2);
        return new AssetNode(AssetKind.Model, leaf,
            $"компонентов {cs2.Components.Count}, сабмешей {subs}",
            cs2, () => ModelChildren(ws, cs2));
    }

    /// <summary>
    /// What one model depends on: its geometry hierarchy, and the materials and
    /// textures reached through it.
    /// </summary>
    public static AssetNode BuildDependencyTree(AssetWorkspace ws, Models.CS2 cs2)
    {
        string[] parts = SplitName(cs2);
        string leaf = parts.Length > 0 ? parts[^1] : (cs2.Name ?? "model");
        return new AssetNode(AssetKind.Model, leaf, cs2.Name, cs2,
            () => ModelChildren(ws, cs2));
    }

    private static List<AssetNode> ModelChildren(AssetWorkspace ws, Models.CS2 cs2)
    {
        var children = new List<AssetNode>();

        for (int ci = 0; ci < cs2.Components.Count; ci++)
        {
            var comp = cs2.Components[ci];
            int index = ci;
            children.Add(new AssetNode(AssetKind.Component, $"компонент {ci}",
                $"LOD-ов {comp.LODs.Count}", comp,
                () => ComponentChildren(ws, cs2, index)));
        }

        var mats = ws.MaterialsOf(cs2);
        children.Add(new AssetNode(AssetKind.Folder, "Материалы", $"{mats.Count}", null,
            () => mats.Select(m => MaterialNode(ws, m)).ToList()));

        var textures = ws.TexturesOf(cs2);
        children.Add(new AssetNode(AssetKind.Folder, "Текстуры", $"{textures.Count}", null,
            () => textures.Select(TextureNode).ToList()));

        return children;
    }

    private static List<AssetNode> ComponentChildren(AssetWorkspace ws, Models.CS2 cs2, int ci)
    {
        var comp = cs2.Components[ci];
        var result = new List<AssetNode>();
        for (int li = 0; li < comp.LODs.Count; li++)
        {
            var lod = comp.LODs[li];
            int lodIndex = li;
            string name = string.IsNullOrEmpty(lod.Name) ? $"LOD {li}" : lod.Name;
            result.Add(new AssetNode(AssetKind.Lod, name,
                $"сабмешей {lod.Submeshes.Count}", lod,
                () => LodChildren(ws, cs2, ci, lodIndex)));
        }
        return result;
    }

    private static List<AssetNode> LodChildren(AssetWorkspace ws, Models.CS2 cs2, int ci, int li)
    {
        var lod = cs2.Components[ci].LODs[li];
        var result = new List<AssetNode>();
        for (int si = 0; si < lod.Submeshes.Count; si++)
        {
            var sm = lod.Submeshes[si];
            var material = sm.Material;
            string detail = $"вершин {sm.VertexCount}, трис {sm.IndexCount / 3}" +
                            (sm.Bones.Count > 0 ? $", костей {sm.Bones.Count}" : "");
            result.Add(new AssetNode(AssetKind.Submesh, $"сабмеш {si}", detail, sm,
                material is null
                    ? null
                    : () => new List<AssetNode> { MaterialNode(ws, material) }));
        }
        return result;
    }

    private static AssetNode MaterialNode(AssetWorkspace ws, Materials.Material material)
    {
        var uses = new List<TextureUse>();
        for (int slot = 0; slot < material.TextureReferences.Count; slot++)
        {
            var ptr = material.TextureReferences[slot];
            if (ptr?.Texture is not null)
                uses.Add(new TextureUse(ptr.Texture, material, slot, ptr.Location));
        }

        return new AssetNode(AssetKind.Material, material.Name ?? "материал",
            $"текстур {uses.Count}" + (material.Shader is null ? ", без шейдера" : ""),
            material,
            () =>
            {
                var kids = uses.Select(TextureNode).ToList();
                if (material.Shader is not null)
                    kids.Add(new AssetNode(AssetKind.Shader, "шейдер", null, material.Shader));
                return kids;
            });
    }

    private static AssetNode TextureNode(TextureUse use)
    {
        var tex = use.Texture;
        var layout = TextureLayout.For(tex.Format);
        var picked = TextureDecoder.SelectPart(tex);
        string size = picked is null
            ? "нет данных"
            : $"{picked.Value.part.Width}x{picked.Value.part.Height}, мипов {picked.Value.part.MipLevels}";
        string detail = $"{tex.Format}, {size}, [{use.Source}] слот {use.Slot}";
        if (!layout.CanDecode)
            detail += "  (нет декодера)";
        return new AssetNode(AssetKind.Texture, tex.Name ?? "текстура", detail, tex);
    }

    /// <summary>Flattens a tree into indented lines, for console output.</summary>
    public static void Print(AssetNode node, Action<string> write, int maxDepth = 4,
        int depth = 0, int maxChildren = 200)
    {
        string pad = new string(' ', depth * 2);
        write($"{pad}{node.Name}" + (node.Detail is null ? "" : $"   [{node.Detail}]"));
        if (depth >= maxDepth || !node.HasChildren)
            return;
        int shown = 0;
        foreach (var child in node.Children)
        {
            if (shown++ >= maxChildren)
            {
                write($"{pad}  ... и ещё {node.Children.Count - maxChildren}");
                break;
            }
            Print(child, write, maxDepth, depth + 1, maxChildren);
        }
    }
}
