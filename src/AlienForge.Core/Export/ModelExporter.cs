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
    /// Геометрия игры уже Y-up (как и в экспорте уровней), но левосторонняя (D3D);
    /// glTF — правосторонний. Зеркалим Z у вершин, костей и анимаций и меняем обход
    /// треугольников — ровно так же, как LevelExporter делает для уровней.
    /// Раньше здесь стоял поворот −90° по X «из Z-up» — он клал модель на спину.
    /// </summary>
    public bool MirrorZ { get; init; } = true;

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
        _mrCache.Clear(); // кэш MR-карт живёт в пределах одного экспорта

        SkeletonData? skeleton = options.ExportSkin ? animation?.Skeleton : null;
        if (skeleton is { Count: 0 })
            skeleton = null;
        if (options.MirrorZ && skeleton is not null)
            skeleton = MirrorSkeleton(skeleton);
        if (options.MirrorZ && animation is not null)
            animation = MirrorAnimation(animation, skeleton);

        // Skinned and static parts go into separate meshes. glTF ties a skin to the
        // node, so a node carrying a skin must not also hold primitives without bone
        // weights; splitting keeps the file valid when a model mixes the two.
        var skinnedPrimitives = new JsonArray();
        var staticPrimitives = new JsonArray();
        int skinned = 0;
        int highestJoint = -1;

        foreach (var mesh in meshes)
        {
            Vector3[] positions = mesh.Positions;
            Vector3[]? normals = mesh.Normals;
            Vector4[]? tangents = mesh.Tangents;
            int[] indices = mesh.Indices;
            if (options.MirrorZ)
            {
                positions = positions.Select(v => new Vector3(v.X, v.Y, -v.Z)).ToArray();
                normals = normals?.Select(v => new Vector3(v.X, v.Y, -v.Z)).ToArray();
                // знак W (направление бинормали) при зеркале меняется
                tangents = tangents?.Select(v => new Vector4(v.X, v.Y, -v.Z, -v.W)).ToArray();
                indices = new int[mesh.Indices.Length];
                for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
                {
                    indices[i] = mesh.Indices[i];
                    indices[i + 1] = mesh.Indices[i + 2];
                    indices[i + 2] = mesh.Indices[i + 1];
                }
            }
            var attributes = new JsonObject
            {
                ["POSITION"] = builder.AddPositions(positions),
            };
            if (normals is { Length: > 0 })
                attributes["NORMAL"] = builder.AddVec3(normals);
            if (tangents is { Length: > 0 })
                attributes["TANGENT"] = builder.AddVec4(tangents);
            if (mesh.Uv0 is { Length: > 0 })
                attributes["TEXCOORD_0"] = builder.AddVec2(mesh.Uv0);
            if (mesh.Uv1 is { Length: > 0 })
                attributes["TEXCOORD_1"] = builder.AddVec2(mesh.Uv1);
            if (mesh.Colors is { Length: > 0 })
                attributes["COLOR_0"] = builder.AddVec4(mesh.Colors);
            if (mesh.Colors1 is { Length: > 0 })
                attributes["COLOR_1"] = builder.AddVec4(mesh.Colors1);

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
                ["indices"] = builder.AddIndices(indices),
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

        var sceneRoots = new List<int>();
        int[]? boneNodes = null;
        bool writeSkin = skeleton is not null && skinnedPrimitives.Count > 0;

        if (writeSkin)
        {
            // Two wrapper nodes above the rig: the Havok-to-CATHODE correction cannot
            // sit on the root bone, because root bones get animated and the clip's raw
            // values would overwrite it. The outer node is a plain root for the rig.
            var yUpNode = new JsonObject { ["name"] = leaf + "_root" };
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

            // Одноключевые каналы: заглушки (identity / ноль / единица) пропускаем —
            // узел кости уже держит reference-значение, а заглушка на корне Чужого
            // развернула бы весь риг на 90°. Реальное одноключевое значение —
            // статическая поза: пишем его константой на всю длину клипа (два ключа),
            // иначе позы вроде ALIEN_COVERMAG_POSES вообще не попадали в файл
            // ("ни одна кость не движется"). То же правило — у PoseEvaluator.
            int constantKeys = clip.Frames > 1 ? 2 : 1;
            float clipEnd = clip.Frames > 1 ? (clip.Frames - 1) / fps : 0f;
            int constantTimes = -1;
            int ConstantTimes()
            {
                if (constantTimes < 0)
                    constantTimes = builder.AddTimes(constantKeys == 2 ? new[] { 0f, clipEnd } : new[] { 0f });
                return constantTimes;
            }

            foreach (var rawTrack in clip.Tracks)
            {
                if (rawTrack.Bone < 0 || rawTrack.Bone >= skeleton.Count)
                    continue;
                int node = boneNodes[rawTrack.Bone];
                // Корневая кость: в клипах её трек — дельта от reference-позы (в данных
                // он почти всегда identity, даже в разворотах на 180°), а в bind у Чужого
                // корень повёрнут на 180° вокруг (0,−1,1). Пишем как есть — и клипы с
                // корневым треком ложатся на бок относительно клипов без него.
                // Складываем с bind: R = Rbind·Rtrack, T = Tbind + Rbind·Ttrack.
                // Аддитивные клипы — дельты (identity/0 на первом кадре), их не трогаем.
                var track = rawTrack;
                var rootBone = skeleton.Bones[rawTrack.Bone];
                if (rootBone.Parent < 0 && !clip.IsAdditive)
                {
                    var rb = rootBone.Rotation;
                    track = new TrackData
                    {
                        Bone = rawTrack.Bone,
                        Translation = rawTrack.Translation.Select(t => rootBone.Translation + Vector3.Transform(t, rb)).ToArray(),
                        Rotation = rawTrack.Rotation.Select(q => Quaternion.Normalize(Quaternion.Concatenate(q, rb))).ToArray(),
                        Scale = rawTrack.Scale,
                    };
                }

                if (track.Translation.Length > 1)
                    AddChannel(node, "translation", TimesFor(track.Translation.Length),
                        builder.AddVec3Output(track.Translation));
                else if (PoseEvaluator.UseTranslation(track))
                    AddChannel(node, "translation", ConstantTimes(),
                        builder.AddVec3Output(Enumerable.Repeat(track.Translation[0], constantKeys).ToArray()));

                if (track.Rotation.Length > 1)
                    AddChannel(node, "rotation", TimesFor(track.Rotation.Length),
                        builder.AddQuaternionOutput(track.Rotation));
                else if (PoseEvaluator.UseRotation(track))
                    AddChannel(node, "rotation", ConstantTimes(),
                        builder.AddQuaternionOutput(Enumerable.Repeat(track.Rotation[0], constantKeys).ToArray()));

                if (track.Scale.Length > 1)
                    AddChannel(node, "scale", TimesFor(track.Scale.Length),
                        builder.AddVec3Output(track.Scale));
                else if (PoseEvaluator.UseScale(track))
                    AddChannel(node, "scale", ConstantTimes(),
                        builder.AddVec3Output(Enumerable.Repeat(track.Scale[0], constantKeys).ToArray()));
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

            builder.AddAnimation(name, channels, samplers, clip.IsAdditive ? new JsonObject { ["additive"] = true, ["blendHint"] = clip.BlendHint } : null);
            written++;
        }
        return written;
    }

    // ---- зеркало Z (левосторонняя система игры → правосторонний glTF)
    // Для локального преобразования M: M' = S·M·S, S = diag(1,1,−1):
    //   перенос (x,y,z) → (x,y,−z); кватернион (x,y,z,w) → (−x,−y,z,w); масштаб без изменений.
    private static readonly Matrix4x4 MirrorS = Matrix4x4.CreateScale(1f, 1f, -1f);
    private static Vector3 MirrorV(Vector3 v) => new(v.X, v.Y, -v.Z);
    private static Quaternion MirrorQ(Quaternion q) => new(-q.X, -q.Y, q.Z, q.W);

    private static SkeletonData MirrorSkeleton(SkeletonData skeleton)
    {
        var bones = skeleton.Bones.Select(b => new BoneData
        {
            Name = b.Name,
            Parent = b.Parent,
            Translation = MirrorV(b.Translation),
            Rotation = MirrorQ(b.Rotation),
            Scale = b.Scale,
        }).ToList();
        return new SkeletonData { Bones = bones, Correction = MirrorS * skeleton.Correction * MirrorS };
    }

    private static AnimationBundle MirrorAnimation(AnimationBundle bundle, SkeletonData? skeleton)
    {
        var clips = bundle.Clips.Select(c => new ClipData
        {
            Name = c.Name,
            Index = c.Index,
            Container = c.Container,
            Duration = c.Duration,
            Frames = c.Frames,
            Fps = c.Fps,
            BlendHint = c.BlendHint,
            Tracks = c.Tracks.Select(t => new TrackData
            {
                Bone = t.Bone,
                Translation = t.Translation.Select(MirrorV).ToArray(),
                Rotation = t.Rotation.Select(MirrorQ).ToArray(),
                Scale = t.Scale,
            }).ToList(),
        }).ToList();
        return new AnimationBundle { Skeleton = skeleton ?? bundle.Skeleton, Clips = clips, Fps = bundle.Fps };
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

        // Материал читается как шейдер: роль каждой текстуры — по привязке сэмплера
        // убершейдера (DIFFUSE_MAP, NORMAL_MAP...), см. ShaderLayout.
        var layout = ShaderLayout.Read(material);

        var pbr = new JsonObject();
        var gltfMaterial = new JsonObject
        {
            ["name"] = material.Name ?? "material",
            ["doubleSided"] = true,
            ["pbrMetallicRoughness"] = pbr,
        };
        var tint = layout.DiffuseTint;
        bool keepAlpha = layout.Transparent || layout.Cutout;

        int? Embed(TextureRole role)
        {
            if (!options.EmbedTextures)
                return null;
            var slot = layout.First(role);
            return slot is null ? null : EmbedTexture(builder, slot.Texture, textureIndex, warnings);
        }

        int? baseColor = Embed(TextureRole.Diffuse) ?? Embed(TextureRole.Colour);
        if (baseColor is not null)
        {
            var baseTex = new JsonObject { ["index"] = baseColor.Value };
            if (Math.Abs(layout.DiffuseUvMult - 1f) > 1e-4f)
                baseTex["extensions"] = new JsonObject
                {
                    ["KHR_texture_transform"] = new JsonObject
                    {
                        ["scale"] = new JsonArray { layout.DiffuseUvMult, layout.DiffuseUvMult },
                    },
                };
            pbr["baseColorTexture"] = baseTex;
            pbr["baseColorFactor"] = new JsonArray { tint.X, tint.Y, tint.Z, keepAlpha ? tint.W : 1f };
        }
        else
            pbr["baseColorFactor"] = new JsonArray { tint.X, tint.Y, tint.Z, keepAlpha ? tint.W : 1f };

        int? normalMap = Embed(TextureRole.Normal);
        if (normalMap is not null)
            gltfMaterial["normalTexture"] = new JsonObject { ["index"] = normalMap.Value };

        // The game's specular map is not a glTF metallic-roughness map, but it is the
        // channel that decides where the surface shines, so it is wired into that slot
        // rather than dropped. Blender shows it on Metallic and Roughness, which is
        // where a texture artist expects to find it, and the honest description of what
        // it really is goes into extras alongside.
        // Spec-карта игры — не metallicRoughness glTF: в её B лежит не металл, а
        // маска (у CA_CHARACTER — шероховатость диффуза, у кожи — маска нормалей),
        // и сырая карта делала половину поверхностей металлическими — они
        // отражали HDRI Blender и синели. Собираем правильную MR-карту:
        //   R = блик F0 (spec.r × SPECULAR_TINT), G = roughness = 1 − spec.g × SPECULAR_POWER,
        //   B = металл только при SPECULAR_MAPPING_METALNESS_MASKING, иначе 0.
        int? specularMap = null;
        var specSlot = layout.First(TextureRole.Specular);
        if (options.EmbedTextures && specSlot?.Texture is not null)
            specularMap = EmbedMetallicRoughness(builder, specSlot.Texture, layout, textureIndex, warnings);
        if (specularMap is not null)
        {
            pbr["metallicRoughnessTexture"] = new JsonObject { ["index"] = specularMap.Value };
            pbr["metallicFactor"] = 1.0f;
            pbr["roughnessFactor"] = 1.0f;
        }
        else
        {
            pbr["metallicFactor"] = 0.0f;
            pbr["roughnessFactor"] = Math.Clamp(1.0f - 0.5f * layout.Param("SPECULAR_POWER", 1.0f), 0.05f, 1.0f);
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
        else
        {
            // Фича EMISSIVE: светится сам диффуз (лампы, экраны, надписи)
            var (ec, es) = layout.Emission();
            if (es > 0f)
            {
                if (baseColor is not null)
                    gltfMaterial["emissiveTexture"] = new JsonObject { ["index"] = baseColor.Value };
                gltfMaterial["emissiveFactor"] = new JsonArray { ec.X, ec.Y, ec.Z };
                gltfMaterial["extensions"] = new JsonObject
                {
                    ["KHR_materials_emissive_strength"] = new JsonObject { ["emissiveStrength"] = Math.Max(1f, es * 4f) },
                };
                builder.UseExtension("KHR_materials_emissive_strength");
            }
        }

        // Режим альфы — по флагам шейдера (как в просмотрщике OpenCAGE): стекло и
        // декали блендятся, ALPHA_TEST режется; карта opacity без флагов — тоже вырез,
        // иначе волосы и решётки импортируются сплошными плитами.
        if (layout.Transparent)
            gltfMaterial["alphaMode"] = "BLEND";
        else if (layout.Cutout || layout.First(TextureRole.Opacity) is not null)
        {
            gltfMaterial["alphaMode"] = "MASK";
            gltfMaterial["alphaCutoff"] = layout.AlphaCutoff;
        }
        gltfMaterial["doubleSided"] = layout.DoubleSided || layout.Ubershader is null;

        // Whatever glTF has no slot for is still written down: nothing about the source
        // material goes missing, so a shader can be rebuilt by hand from the export.
        // Для переноса 1:1 в Blender кладём в файл и карты без своего glTF-слота
        // (AO, грязь, маски, вторичные normal/spec...): скрипт alienforge_blender.py
        // подключает их по extras. Кубические карты и LUT'ы пропускаем.
        if (options.EmbedTextures)
            foreach (var slot in layout.Slots)
            {
                if (slot.SamplerName is null || slot.Texture is null) continue;
                if (slot.Role is TextureRole.Environment) continue;
                if (slot.SamplerName is "CONVOLVED_BRDF_MAX_HACK" or "IRRADIANCE_CUBE_MAP" or "ENVIRONMENT_MAP") continue;
                EmbedTexture(builder, slot.Texture, textureIndex, warnings);
            }

        var channels = new JsonArray();
        foreach (var slot in layout.Slots)
        {
            var ch = layout.ChannelsOf(slot);
            channels.Add(new JsonObject
            {
                ["slot"] = slot.Index,
                ["role"] = slot.Role.ToString(),
                ["roleName"] = slot.RoleName,
                ["texture"] = slot.TextureName,
                ["sampler"] = slot.SamplerName,
                ["gltfTarget"] = slot.GltfTarget,
                ["image"] = slot.Texture is not null && textureIndex.TryGetValue(slot.Texture, out int ti) ? ti : null,
                ["R"] = ch.R, ["G"] = ch.G, ["B"] = ch.B, ["A"] = ch.A,
                ["blender"] = ch.Blender,
            });
        }
        var features = new JsonArray();
        foreach (var f in layout.Features) features.Add(f);
        var parameters = new JsonObject();
        foreach (var kv in layout.Parameters)
        {
            var arr = new JsonArray();
            foreach (var v in kv.Value) arr.Add(v);
            parameters[kv.Key] = arr;
        }

        var extras = new JsonObject
        {
            ["shader"] = channels,
            ["features"] = features,
            ["params"] = parameters,
            ["alpha"] = layout.Transparent ? "BLEND" : layout.Cutout ? "MASK" : "OPAQUE",
            ["ubershader"] = layout.Ubershader,
            ["environmentMapIndex"] = material.EnvironmentMapIndex,
            ["physicalMaterialIndex"] = material.PhysicalMaterialIndex,
            ["specularIsGameSpecular"] = specularMap is not null,
            ["note"] = "specular карта игры записана в metallicRoughness: своего слота " +
                       "для неё в glTF нет",
        };
        // Импортёр Blender не всегда доносит вложенные extras (списки строк,
        // списки объектов) — дублируем всё одной JSON-строкой для alienforge_blender.py
        extras["features_csv"] = string.Join(",", layout.Features);
        extras["alienforge_json"] = extras.ToJsonString();
        gltfMaterial["extras"] = extras;

        int index = builder.AddMaterial(gltfMaterial);
        materialIndex[material] = index;
        return index;
    }

    /// <summary>
    /// MR-карта из spec-карты игры (см. комментарий у metallicRoughnessTexture).
    /// Кэшируется отдельно от сырой карты: та тоже кладётся в файл для скрипта Blender.
    /// </summary>
    private static readonly Dictionary<(Textures.TEX4, string), int> _mrCache = new();
    private static int? EmbedMetallicRoughness(GltfBuilder builder, Textures.TEX4 tex, ShaderLayout layout,
        Dictionary<Textures.TEX4, int> cache, List<string> warnings)
    {
        float tint = layout.Param("SPECULAR_TINT", 1.0f);
        if (layout.Ubershader == "CA_SKIN") tint *= 0.28f; // кодировка блика кожи: F0 = 0.28·spec
        float power = layout.Param("SPECULAR_POWER", 1.0f);
        bool metal = layout.HasFeature("SPECULAR_MAPPING_METALNESS_MASKING");
        string key = $"{tint:0.###}|{power:0.###}|{metal}";
        if (_mrCache.TryGetValue((tex, key), out int existing))
            return existing;
        var result = TextureDecoder.Decode(tex);
        if (!result.Ok)
        {
            warnings.Add($"spec-текстура '{tex.Name}' пропущена: {result.Error}");
            return null;
        }
        var src = result.Image!;
        var mr = new RgbaImage(src.Width, src.Height);
        for (int i = 0; i < src.Pixels.Length; i += 4)
        {
            float r = src.Pixels[i] / 255f, g = src.Pixels[i + 1] / 255f, b = src.Pixels[i + 2] / 255f;
            mr.Pixels[i] = (byte)Math.Clamp(r * tint * 255f, 0, 255);
            mr.Pixels[i + 1] = (byte)Math.Clamp((1f - Math.Min(g * power, 0.999f)) * 255f, 0, 255);
            mr.Pixels[i + 2] = (byte)(metal ? Math.Clamp(b * 255f, 0, 255) : 0);
            mr.Pixels[i + 3] = 255;
        }
        byte[] png = PngEncoder.Encode(mr);
        int image = builder.AddPngImage(png, (tex.Name ?? "spec") + "#mr");
        int index = builder.AddTexture(image);
        _mrCache[(tex, key)] = index;
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
