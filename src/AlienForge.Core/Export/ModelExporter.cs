using System.Numerics;
using System.Text.Json.Nodes;
using AlienForge.Core.Animation;
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

    /// <summary>
    /// Write bone weights and a skin when a skeleton is supplied. Without it the
    /// export is a static pose, which is what you want for props and level geometry.
    /// </summary>
    public bool ExportSkin { get; init; } = true;
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
    public int Bones { get; init; }
    public int Clips { get; init; }
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
    /// Reading it lives in <see cref="ShaderLayout"/>, which the exporter and the
    /// material inspector share so they can never disagree.
    /// </summary>

    /// <param name="animation">
    /// Skeleton and clips to bake in. Null exports the bind pose only.
    /// </param>
    public static ModelExportResult ExportGlb(AssetWorkspace ws, Models.CS2 model,
        string outPath, ModelExportOptions? options = null, AnimationBundle? animation = null)
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

        SkeletonData? skeleton = options.ExportSkin ? animation?.Skeleton : null;
        if (skeleton is { Count: 0 })
            skeleton = null;

        // Skinned and static parts go into separate meshes. glTF ties a skin to the
        // node, so a node carrying a skin must not also hold primitives without bone
        // weights; splitting keeps the file valid when a model mixes the two.
        var skinnedPrimitives = new JsonArray();
        var staticPrimitives = new JsonArray();
        int skinned = 0;
        int highestJoint = -1;

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

            bool useSkin = skeleton is not null && mesh.IsSkinned;
            if (mesh.IsSkinned)
                skinned++;
            if (useSkin)
            {
                attributes["JOINTS_0"] = builder.AddJoints(mesh.Joints!);
                attributes["WEIGHTS_0"] = builder.AddVec4(mesh.Weights!);
                foreach (ushort j in mesh.Joints!)
                    if (j > highestJoint)
                        highestJoint = j;
            }

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
            (useSkin ? skinnedPrimitives : staticPrimitives).Add(primitive);
        }

        if (skinned > 0 && skeleton is null)
            warnings.Add($"скинованных частей {skinned}: веса костей не записаны, " +
                         "потому что скелет не передан (он лежит в DATA\\GLOBAL\\ANIMATION.PAK)");
        if (skeleton is not null && highestJoint >= skeleton.Count)
            warnings.Add($"меш ссылается на кость {highestJoint}, а в скелете их " +
                         $"{skeleton.Count}: скорее всего скелет не от этой модели");

        string modelName = model.Name ?? "model";
        string leaf = LeafName(modelName);

        // -90 degrees about X, as a quaternion (x, y, z, w). Takes the game's Z-up
        // geometry to the Y-up glTF expects.
        JsonArray ZUpToYUp() => new() { -0.70710678f, 0f, 0f, 0.70710678f };

        var sceneRoots = new List<int>();
        int[]? boneNodes = null;
        bool writeSkin = skeleton is not null && skinnedPrimitives.Count > 0;

        if (writeSkin)
        {
            // Two wrapper nodes above the rig, for reasons that both matter:
            //
            //  * the Havok-to-CATHODE correction cannot sit on the root bone, because
            //    root bones get animated and the clip's raw values would overwrite it;
            //  * the Z-up to Y-up rotation cannot sit on the mesh node, because glTF
            //    ignores the transform of a node carrying a skin.
            //
            // Putting both above the joints means they reach the skinned vertices
            // through the joint matrices instead.
            var yUpNode = new JsonObject { ["name"] = leaf + "_root" };
            if (options.ConvertZUpToYUp)
                yUpNode["rotation"] = ZUpToYUp();
            int yUpIndex = builder.AddNode(yUpNode);

            var rigNode = new JsonObject { ["name"] = leaf + "_rig" };
            if (skeleton!.Correction != Matrix4x4.Identity
                && Matrix4x4.Decompose(skeleton.Correction, out Vector3 cs,
                    out Quaternion cr, out Vector3 ct))
            {
                if (ct != Vector3.Zero) rigNode["translation"] = Vec3Array(ct);
                if (cr != Quaternion.Identity) rigNode["rotation"] = QuatArray(cr);
                if (cs != Vector3.One) rigNode["scale"] = Vec3Array(cs);
            }
            int rigIndex = builder.AddNode(rigNode);
            yUpNode["children"] = new JsonArray { rigIndex };

            var boneRoots = new JsonArray();
            boneNodes = BuildSkeletonNodes(builder, skeleton, boneRoots);
            if (boneRoots.Count > 0)
                rigNode["children"] = boneRoots;

            var bind = skeleton.BindWorld();
            var inverseBind = new Matrix4x4[skeleton.Count];
            for (int i = 0; i < skeleton.Count; i++)
                inverseBind[i] = Matrix4x4.Invert(bind[i], out Matrix4x4 inv)
                    ? inv
                    : Matrix4x4.Identity;

            int meshIndex = builder.AddMesh(leaf, skinnedPrimitives);

            // Left at the scene root with no transform of its own. The skinning result
            // already carries both wrappers through the joint matrices, and an identity
            // transform here means viewers that divide by this node's global transform
            // and viewers that ignore it land on the same answer.
            int meshNode = builder.AddNode(new JsonObject
            {
                ["name"] = leaf,
                ["mesh"] = meshIndex,
                ["skin"] = builder.AddSkin(leaf + "_skin", boneNodes,
                    builder.AddMatrices(inverseBind),
                    boneNodes.Length > 0 ? boneNodes[0] : null),
            });

            sceneRoots.Add(yUpIndex);
            sceneRoots.Add(meshNode);
        }
        else if (skinnedPrimitives.Count > 0)
        {
            // Skinned geometry with no rig to hang it on: export the bind pose.
            foreach (var primitive in skinnedPrimitives.ToList())
            {
                skinnedPrimitives.Remove(primitive);
                staticPrimitives.Add(primitive);
            }
        }

        if (staticPrimitives.Count > 0)
        {
            string name = writeSkin ? leaf + "_static" : leaf;
            var staticNode = new JsonObject
            {
                ["name"] = name,
                ["mesh"] = builder.AddMesh(name, staticPrimitives),
            };
            if (options.ConvertZUpToYUp)
                staticNode["rotation"] = ZUpToYUp();
            sceneRoots.Add(builder.AddNode(staticNode));
        }

        int clipCount = 0;
        if (boneNodes is not null && animation is not null)
            clipCount = WriteAnimations(builder, skeleton!, boneNodes, animation, warnings);

        var extras = new JsonObject
        {
            ["source"] = modelName,
            ["level"] = ws.Name,
            ["game"] = "Alien: Isolation",
            ["lod"] = options.OnlyLod?.ToString() ?? "все",
            ["bones"] = skeleton?.Count ?? 0,
            ["clips"] = clipCount,
        };

        long size = builder.Save(outPath, sceneRoots, leaf, extras);

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
            Bones = skeleton?.Count ?? 0,
            Clips = clipCount,
        };
        result.Warnings.AddRange(warnings);
        return result;
    }

    /// <summary>
    /// One node per bone, wired into the same parent/child shape as the Havok skeleton
    /// and posed at its bind transform. Root bones are attached to
    /// <paramref name="rootChildren"/>.
    /// </summary>
    /// <remarks>
    /// Transforms are written exactly as Havok stores them, with no space correction
    /// folded in, so that animation channels can overwrite them verbatim. The
    /// correction lives in a parent node instead.
    /// </remarks>
    private static int[] BuildSkeletonNodes(GltfBuilder builder, SkeletonData skeleton,
        JsonArray rootChildren)
    {
        var objects = new JsonObject[skeleton.Count];
        var indices = new int[skeleton.Count];

        for (int i = 0; i < skeleton.Count; i++)
        {
            var bone = skeleton.Bones[i];
            var node = new JsonObject { ["name"] = bone.Name };

            // Identity components are left out: they are the glTF default, and omitting
            // them keeps the JSON chunk small on a 600-bone skeleton.
            if (bone.Translation != Vector3.Zero)
                node["translation"] = Vec3Array(bone.Translation);
            if (bone.Rotation != Quaternion.Identity)
                node["rotation"] = QuatArray(bone.Rotation);
            if (bone.Scale != Vector3.One)
                node["scale"] = Vec3Array(bone.Scale);

            objects[i] = node;
            indices[i] = builder.AddNode(node);
        }

        for (int i = 0; i < skeleton.Count; i++)
        {
            int parent = skeleton.Bones[i].Parent;
            if (parent >= 0 && parent < skeleton.Count && parent != i)
            {
                if (objects[parent]["children"] is not JsonArray children)
                {
                    children = new JsonArray();
                    objects[parent]["children"] = children;
                }
                children.Add(indices[i]);
            }
            else
            {
                rootChildren.Add(indices[i]);
            }
        }
        return indices;
    }

    /// <summary>
    /// Turns decoded tracks into glTF animation channels, one channel per bone and
    /// property. Channels that never leave the bind value are skipped: the bone node
    /// already holds it, so the result is identical and the file is smaller.
    /// </summary>
    private static int WriteAnimations(GltfBuilder builder, SkeletonData skeleton,
        int[] boneNodes, AnimationBundle animation, List<string> warnings)
    {
        int written = 0;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var clip in animation.Clips)
        {
            var channels = new JsonArray();
            var samplers = new JsonArray();

            float fps = clip.Fps > 0f ? clip.Fps
                : animation.Fps > 0f ? animation.Fps : 30f;

            // Most tracks share a frame count, so the time accessor is built once per
            // distinct length instead of once per channel.
            var timeAccessors = new Dictionary<int, int>();
            int TimesFor(int count)
            {
                if (timeAccessors.TryGetValue(count, out int cached))
                    return cached;
                var times = new float[count];
                for (int i = 0; i < count; i++)
                    times[i] = i / fps;
                int accessor = builder.AddTimes(times);
                timeAccessors[count] = accessor;
                return accessor;
            }

            void AddChannel(int node, string path, int input, int output)
            {
                samplers.Add(new JsonObject
                {
                    ["input"] = input,
                    ["output"] = output,
                    ["interpolation"] = "LINEAR",
                });
                channels.Add(new JsonObject
                {
                    ["sampler"] = samplers.Count - 1,
                    ["target"] = new JsonObject { ["node"] = node, ["path"] = path },
                });
            }

            // Single-key channels are dropped, matching how the preview evaluates a
            // pose: one key means the bone is not animated, and the bone node already
            // holds the reference value. Honouring the placeholder instead would swing
            // the whole rig, because the Alien's root arrives as an identity rotation
            // while its reference pose carries the swap into CATHODE space.
            int minimumKeys = clip.Frames > 1 ? 2 : 1;

            foreach (var track in clip.Tracks)
            {
                if (track.Bone < 0 || track.Bone >= skeleton.Count)
                    continue;
                int node = boneNodes[track.Bone];

                if (track.Translation.Length >= minimumKeys)
                    AddChannel(node, "translation", TimesFor(track.Translation.Length),
                        builder.AddVec3Output(track.Translation));

                if (track.Rotation.Length >= minimumKeys)
                    AddChannel(node, "rotation", TimesFor(track.Rotation.Length),
                        builder.AddQuaternionOutput(track.Rotation));

                if (track.Scale.Length >= minimumKeys)
                    AddChannel(node, "scale", TimesFor(track.Scale.Length),
                        builder.AddVec3Output(track.Scale));
            }

            if (channels.Count == 0)
            {
                warnings.Add($"клип '{clip.Name}' пропущен: ни одна кость не движется");
                continue;
            }

            // Blender keys actions by name, so duplicates would overwrite each other.
            string name = clip.Name;
            for (int n = 2; !usedNames.Add(name); n++)
                name = $"{clip.Name}#{n}";

            builder.AddAnimation(name, channels, samplers);
            written++;
        }
        return written;
    }

    private static bool QuaternionClose(Quaternion a, Quaternion b)
        => MathF.Abs(a.X - b.X) < 1e-5f && MathF.Abs(a.Y - b.Y) < 1e-5f
           && MathF.Abs(a.Z - b.Z) < 1e-5f && MathF.Abs(a.W - b.W) < 1e-5f;

    private static JsonArray Vec3Array(Vector3 v) => new() { v.X, v.Y, v.Z };

    private static JsonArray QuatArray(Quaternion q)
    {
        if (q.LengthSquared() > 1e-8f)
            q = Quaternion.Normalize(q);
        else
            q = Quaternion.Identity;
        return new JsonArray { q.X, q.Y, q.Z, q.W };
    }

    private static int ResolveMaterial(GltfBuilder builder, AssetWorkspace ws,
        Materials.Material material, Dictionary<Materials.Material, int> materialIndex,
        Dictionary<Textures.TEX4, int> textureIndex, ModelExportOptions options,
        List<string> warnings)
    {
        if (materialIndex.TryGetValue(material, out int existing))
            return existing;

        // The material is read as a shader first: every texture's job comes from the
        // bracketed letter in its name, so the wiring is recovered rather than guessed
        // slot by slot.
        var layout = ShaderLayout.Read(material);

        var pbr = new JsonObject();
        var gltfMaterial = new JsonObject
        {
            ["name"] = material.Name ?? "material",
            ["doubleSided"] = true,
            ["pbrMetallicRoughness"] = pbr,
        };

        int? Embed(TextureRole role)
        {
            if (!options.EmbedTextures)
                return null;
            var slot = layout.First(role);
            return slot is null ? null : EmbedTexture(builder, slot.Texture, textureIndex, warnings);
        }

        int? baseColor = Embed(TextureRole.Diffuse) ?? Embed(TextureRole.Colour);
        if (baseColor is not null)
            pbr["baseColorTexture"] = new JsonObject { ["index"] = baseColor.Value };
        else
            pbr["baseColorFactor"] = new JsonArray { 0.5f, 0.5f, 0.52f, 1.0f };

        int? normalMap = Embed(TextureRole.Normal);
        if (normalMap is not null)
            gltfMaterial["normalTexture"] = new JsonObject { ["index"] = normalMap.Value };

        // The game's specular map is not a glTF metallic-roughness map, but it is the
        // channel that decides where the surface shines, so it is wired into that slot
        // rather than dropped. Blender shows it on Metallic and Roughness, which is
        // where a texture artist expects to find it, and the honest description of what
        // it really is goes into extras alongside.
        int? specularMap = Embed(TextureRole.Specular);
        if (specularMap is not null)
        {
            pbr["metallicRoughnessTexture"] = new JsonObject { ["index"] = specularMap.Value };
            pbr["metallicFactor"] = 1.0f;
            pbr["roughnessFactor"] = 1.0f;
        }
        else
        {
            pbr["metallicFactor"] = 0.0f;
            pbr["roughnessFactor"] = 0.6f;
        }

        int? occlusion = Embed(TextureRole.Occlusion);
        if (occlusion is not null)
            gltfMaterial["occlusionTexture"] = new JsonObject { ["index"] = occlusion.Value };

        int? emissive = Embed(TextureRole.Emissive);
        if (emissive is not null)
        {
            gltfMaterial["emissiveTexture"] = new JsonObject { ["index"] = emissive.Value };
            gltfMaterial["emissiveFactor"] = new JsonArray { 1.0f, 1.0f, 1.0f };
        }

        // An opacity map means the surface is meant to be cut out, and saying so is
        // what stops hair and grilles importing as solid slabs.
        if (layout.First(TextureRole.Opacity) is not null)
        {
            gltfMaterial["alphaMode"] = "MASK";
            gltfMaterial["alphaCutoff"] = 0.5f;
        }

        // Whatever glTF has no slot for is still written down: nothing about the source
        // material goes missing, so a shader can be rebuilt by hand from the export.
        var channels = new JsonArray();
        foreach (var slot in layout.Slots)
            channels.Add(new JsonObject
            {
                ["slot"] = slot.Index,
                ["role"] = slot.Role.ToString(),
                ["roleName"] = slot.RoleName,
                ["texture"] = slot.TextureName,
                ["gltfTarget"] = slot.GltfTarget,
            });

        gltfMaterial["extras"] = new JsonObject
        {
            ["shader"] = channels,
            ["environmentMapIndex"] = material.EnvironmentMapIndex,
            ["physicalMaterialIndex"] = material.PhysicalMaterialIndex,
            ["specularIsGameSpecular"] = specularMap is not null,
            ["note"] = "specular карта игры записана в metallicRoughness: своего слота " +
                       "для неё в glTF нет",
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
