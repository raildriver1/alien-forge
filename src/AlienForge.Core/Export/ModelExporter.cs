using System.Numerics;
using System.Text.Json.Nodes;
using AlienForge.Core.Imaging;
using AlienForge.Core.Mesh;
using CATHODE;

namespace AlienForge.Core.Export;

public sealed class ModelExportOptions
{
    /// <summary>Only this LOD, or null for every LOD. LOD 0 is the highest detail.</summary>
    public int? OnlyLod { get; init; } = 0;

    /// <summary>Collision hulls ship as extra LODs named COL_*; off by default.</summary>
    public bool IncludeCollision { get; init; }

    public bool EmbedTextures { get; init; } = true;

    /// <summary>
    /// The game's geometry is Z-up; glTF is Y-up. Applied as a node rotation so the
    /// vertex data stays exactly as it was decoded.
    /// </summary>
    public bool ConvertZUpToYUp { get; init; } = true;
}

public sealed class ModelExportResult
{
    public required string Path { get; init; }
    public long Bytes { get; init; }
    public int Parts { get; init; }
    public int Vertices { get; init; }
    public int Triangles { get; init; }
    public int Materials { get; init; }
    public int Images { get; init; }
    public int SkinnedParts { get; init; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Writes a CATHODE model out as .glb with its materials and decoded textures.
/// </summary>
public static class ModelExporter
{
    /// <summary>
    /// Map role is encoded in the texture name as a bracketed letter, e.g.
    /// <c>alien_body[d].tga</c> for diffuse, <c>[n]</c> normal, <c>[s]</c> specular.
    /// </summary>
    private static bool HasRole(string name, char role)
        => name.Contains($"[{role}]", StringComparison.OrdinalIgnoreCase);

    public static ModelExportResult ExportGlb(AssetWorkspace ws, Models.CS2 model,
        string outPath, ModelExportOptions? options = null)
    {
        options ??= new ModelExportOptions();
        var builder = new GltfBuilder
        {
            Generator = "AlienForge — извлечение из Alien: Isolation (CathodeLib + свои декодеры)",
        };

        var warnings = new List<string>();
        var meshes = new List<DecodedMesh>();

        for (int ci = 0; ci < model.Components.Count; ci++)
            for (int li = 0; li < model.Components[ci].LODs.Count; li++)
            {
                if (options.OnlyLod is int wanted && li != wanted)
                    continue;
                var lod = model.Components[ci].LODs[li];
                if (!options.IncludeCollision && IsCollision(lod.Name))
                    continue;

                for (int si = 0; si < lod.Submeshes.Count; si++)
                {
                    var decoded = MeshDecoder.Decode(model, ci, li, si);
                    if (decoded is null || decoded.VertexCount == 0 || decoded.Indices.Length == 0)
                        continue;
                    meshes.Add(decoded);
                    foreach (string w in decoded.Warnings)
                        warnings.Add($"{decoded.Name}: {w}");
                }
            }

        if (meshes.Count == 0)
            throw new InvalidOperationException(
                "В модели нет геометрии для экспорта (проверьте настройки LOD и коллизий).");

        // One glTF material per CATHODE material, textures decoded once and shared.
        var materialIndex = new Dictionary<Materials.Material, int>();
        var textureIndex = new Dictionary<Textures.TEX4, int>();

        var primitives = new JsonArray();
        int skinned = 0;

        foreach (var mesh in meshes)
        {
            var attributes = new JsonObject
            {
                ["POSITION"] = builder.AddPositions(mesh.Positions),
            };
            if (mesh.Normals is { Length: > 0 })
                attributes["NORMAL"] = builder.AddVec3(mesh.Normals);
            if (mesh.Tangents is { Length: > 0 })
                attributes["TANGENT"] = builder.AddVec4(mesh.Tangents);
            if (mesh.Uv0 is { Length: > 0 })
                attributes["TEXCOORD_0"] = builder.AddVec2(mesh.Uv0);
            if (mesh.Uv1 is { Length: > 0 })
                attributes["TEXCOORD_1"] = builder.AddVec2(mesh.Uv1);

            if (mesh.IsSkinned)
                skinned++;

            var primitive = new JsonObject
            {
                ["attributes"] = attributes,
                ["indices"] = builder.AddIndices(mesh.Indices),
                ["mode"] = 4, // TRIANGLES
            };

            var submesh = model.Components[mesh.ComponentIndex]
                .LODs[mesh.LodIndex].Submeshes[mesh.SubmeshIndex];
            if (submesh.Material is not null)
                primitive["material"] = ResolveMaterial(builder, ws, submesh.Material,
                    materialIndex, textureIndex, options, warnings);

            primitive["extras"] = new JsonObject
            {
                ["part"] = mesh.Name,
                ["lod"] = mesh.LodName,
                ["component"] = mesh.ComponentIndex,
                ["lodIndex"] = mesh.LodIndex,
                ["submesh"] = mesh.SubmeshIndex,
                ["skinned"] = mesh.IsSkinned,
            };
            primitives.Add(primitive);
        }

        if (skinned > 0)
            warnings.Add($"скинованных частей {skinned}: веса и индексы костей не записаны, " +
                         "потому что скелет лежит отдельно в DATA\\GLOBAL\\ANIMATION.PAK");

        string modelName = model.Name ?? "model";
        int meshIndex = builder.AddMesh(modelName, primitives);

        var node = new JsonObject
        {
            ["name"] = LeafName(modelName),
            ["mesh"] = meshIndex,
        };
        if (options.ConvertZUpToYUp)
        {
            // -90 degrees about X, as a quaternion (x, y, z, w).
            node["rotation"] = new JsonArray { -0.70710678f, 0f, 0f, 0.70710678f };
        }
        int nodeIndex = builder.AddNode(node);

        var extras = new JsonObject
        {
            ["source"] = modelName,
            ["level"] = ws.Name,
            ["game"] = "Alien: Isolation",
            ["lod"] = options.OnlyLod?.ToString() ?? "все",
        };

        long size = builder.Save(outPath, new[] { nodeIndex }, LeafName(modelName), extras);

        var result = new ModelExportResult
        {
            Path = outPath,
            Bytes = size,
            Parts = meshes.Count,
            Vertices = meshes.Sum(m => m.VertexCount),
            Triangles = meshes.Sum(m => m.TriangleCount),
            Materials = builder.MaterialCount,
            Images = builder.ImageCount,
            SkinnedParts = skinned,
        };
        result.Warnings.AddRange(warnings);
        return result;
    }

    private static int ResolveMaterial(GltfBuilder builder, AssetWorkspace ws,
        Materials.Material material, Dictionary<Materials.Material, int> materialIndex,
        Dictionary<Textures.TEX4, int> textureIndex, ModelExportOptions options,
        List<string> warnings)
    {
        if (materialIndex.TryGetValue(material, out int existing))
            return existing;

        Textures.TEX4? diffuse = null, normal = null, specular = null;
        var allNames = new JsonArray();

        foreach (var ptr in material.TextureReferences)
        {
            var tex = ptr?.Texture;
            if (tex is null)
                continue;
            string name = tex.Name ?? string.Empty;
            allNames.Add(name);
            if (diffuse is null && HasRole(name, 'd'))
                diffuse = tex;
            else if (normal is null && HasRole(name, 'n'))
                normal = tex;
            else if (specular is null && HasRole(name, 's'))
                specular = tex;
        }

        var pbr = new JsonObject
        {
            ["metallicFactor"] = 0.0f,
            ["roughnessFactor"] = 0.6f,
        };

        int? baseColor = options.EmbedTextures
            ? EmbedTexture(builder, diffuse, textureIndex, warnings)
            : null;
        if (baseColor is not null)
            pbr["baseColorTexture"] = new JsonObject { ["index"] = baseColor.Value };
        else
            pbr["baseColorFactor"] = new JsonArray { 0.5f, 0.5f, 0.52f, 1.0f };

        var gltfMaterial = new JsonObject
        {
            ["name"] = material.Name ?? "material",
            ["doubleSided"] = true,
            ["pbrMetallicRoughness"] = pbr,
        };

        int? normalMap = options.EmbedTextures
            ? EmbedTexture(builder, normal, textureIndex, warnings)
            : null;
        if (normalMap is not null)
            gltfMaterial["normalTexture"] = new JsonObject { ["index"] = normalMap.Value };

        // glTF core has no specular map slot, so the name is recorded rather than
        // dropped silently: nothing about the source material goes missing.
        gltfMaterial["extras"] = new JsonObject
        {
            ["cathodeTextures"] = allNames,
            ["specularTexture"] = specular?.Name,
            ["environmentMapIndex"] = material.EnvironmentMapIndex,
            ["physicalMaterialIndex"] = material.PhysicalMaterialIndex,
        };

        int index = builder.AddMaterial(gltfMaterial);
        materialIndex[material] = index;
        return index;
    }

    private static int? EmbedTexture(GltfBuilder builder, Textures.TEX4? tex,
        Dictionary<Textures.TEX4, int> cache, List<string> warnings)
    {
        if (tex is null)
            return null;
        if (cache.TryGetValue(tex, out int existing))
            return existing;

        var result = TextureDecoder.Decode(tex);
        if (!result.Ok)
        {
            warnings.Add($"текстура '{tex.Name}' пропущена: {result.Error}");
            return null;
        }

        byte[] png = PngEncoder.Encode(result.Image!);
        int image = builder.AddPngImage(png, tex.Name ?? "texture");
        int index = builder.AddTexture(image);
        cache[tex] = index;
        return index;
    }

    private static bool IsCollision(string? lodName)
        => lodName is not null
           && (lodName.StartsWith("COL_", StringComparison.OrdinalIgnoreCase)
               || lodName.Contains("_COL", StringComparison.OrdinalIgnoreCase));

    private static string LeafName(string path)
    {
        string normalised = path.Replace('/', '\\');
        int slash = normalised.LastIndexOf('\\');
        string leaf = slash >= 0 ? normalised[(slash + 1)..] : normalised;
        string withoutExt = Path.GetFileNameWithoutExtension(leaf);
        return string.IsNullOrEmpty(withoutExt) ? leaf : withoutExt;
    }
}
