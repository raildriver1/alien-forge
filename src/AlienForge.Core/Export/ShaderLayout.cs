using System.Numerics;
using System.Text;
using CATHODE;
using CATHODE.ShaderTypes;

namespace AlienForge.Core.Export;

/// <summary>What one texture does in a material.</summary>
public enum TextureRole
{
    Unknown,
    Diffuse,
    Normal,
    Specular,
    Opacity,
    Emissive,
    Occlusion,
    Environment,
    Detail,
    Mask,
    Flow,
    Colour,
}

/// <summary>One texture slot of a material, with the job it does.</summary>
public sealed class ShaderSlot
{
    public required TextureRole Role { get; init; }
    public required string TextureName { get; init; }
    public Textures.TEX4? Texture { get; init; }

    /// <summary>Имя сэмплера убершейдера (DIFFUSE_MAP, NORMAL_MAP...), если слот привязан.</summary>
    public string? SamplerName { get; init; }

    /// <summary>Position in the material's own texture list.</summary>
    public int Index { get; init; }

    /// <summary>Where this ends up in a glTF material, or null when there is no slot.</summary>
    public string? GltfTarget => Role switch
    {
        TextureRole.Diffuse or TextureRole.Colour => "pbrMetallicRoughness.baseColorTexture",
        TextureRole.Normal => "normalTexture",
        TextureRole.Specular => "pbrMetallicRoughness.metallicRoughnessTexture",
        TextureRole.Occlusion => "occlusionTexture",
        TextureRole.Emissive => "emissiveTexture",
        TextureRole.Opacity => "pbrMetallicRoughness.baseColorTexture (альфа)",
        _ => null,
    };

    public string RoleName => Role switch
    {
        TextureRole.Diffuse => "цвет (diffuse)",
        TextureRole.Colour => "цвет",
        TextureRole.Normal => "нормали",
        TextureRole.Specular => "блик / металличность",
        TextureRole.Opacity => "прозрачность",
        TextureRole.Emissive => "свечение",
        TextureRole.Occlusion => "затенение",
        TextureRole.Environment => "отражение окружения",
        TextureRole.Detail => "детализация",
        TextureRole.Mask => "маска",
        TextureRole.Flow => "поток",
        _ => "неизвестно",
    };

    public override string ToString() => SamplerName is null ? $"{RoleName}: {TextureName}" : $"{SamplerName} ({RoleName}): {TextureName}";
}

/// <summary>
/// Reads a CATHODE material as a shader: which texture feeds which channel.
/// </summary>
/// <remarks>
/// A texture on its own is just a picture. What makes it a surface is the channel it
/// is wired into, and this game states that in the texture's own name: the role is a
/// bracketed letter, so <c>alien_body[d].tga</c> is the colour map, <c>[n]</c> the
/// normals and <c>[s]</c> the specular. Reading those out turns a flat list of images
/// into an explanation of how the material responds to light, and it is also exactly
/// what the glTF exporter needs to know to wire the same textures up again.
/// </remarks>
public sealed class ShaderLayout
{
    public required string MaterialName { get; init; }
    public List<ShaderSlot> Slots { get; } = new();

    public int EnvironmentMapIndex { get; init; }
    public int PhysicalMaterialIndex { get; init; }

    /// <summary>Убершейдер материала (CA_ENVIRONMENT, CA_CHARACTER...), если шейдер найден.</summary>
    public string? Ubershader { get; init; }

    /// <summary>DIFFUSE_TINT из констант пиксельного шейдера, 0..1; единица, если нет.</summary>
    public Vector4 DiffuseTint { get; init; } = Vector4.One;

    /// <summary>Множитель UV диффуза (DIFFUSE_UV_MULT); 1, если нет.</summary>
    public float DiffuseUvMult { get; init; } = 1f;

    /// <summary>Шейдер блендит по альфе (стекло, decal, separate alpha...).</summary>
    public bool Transparent { get; init; }

    /// <summary>Шейдер режет по альфе (ALPHA_TEST).</summary>
    public bool Cutout { get; init; }

    /// <summary>Порог для cutout, 0..1.</summary>
    public float AlphaCutoff { get; init; } = 0.5f;

    public bool DoubleSided { get; init; }

    /// <summary>Отдельная карта альфы берётся из зелёного канала.</summary>
    public bool SeparateAlphaFromGreen { get; init; }

    /// <summary>Включённые фичи убершейдера (VERTEX_COLOUR, NORMAL_MAPPING, GLASS...).</summary>
    public List<string> Features { get; } = new();

    /// <summary>Все параметры пиксельного шейдера материала по именам (DIFFUSE_TINT → [r,g,b,a]...).</summary>
    public Dictionary<string, float[]> Parameters { get; } = new();

    public bool HasFeature(string name) => Features.Contains(name);

    public float Param(string name, float fallback)
        => Parameters.TryGetValue(name, out var v) && v.Length > 0 ? v[0] : fallback;

    /// <summary>
    /// Свечение (фича EMISSIVE): диффуз × DIFFUSE_TINT × EMISSIVE_TINT × EMISSIVE_MULT.
    /// Возвращает цвет (0..1) и силу (EMISSIVE_MULT); сила 0 — не светится.
    /// Так светятся лампы, экраны, надписи — без этого они выходили плоскими
    /// синеватыми плитками (DIFFUSE_TINT «холодного» света 0.56/0.79/0.93).
    /// </summary>
    public (Vector3 colour, float strength) Emission()
    {
        if (!HasFeature("EMISSIVE"))
            return (Vector3.Zero, 0f);
        float mult = Param("EMISSIVE_MULT", 1f);
        if (mult <= 0.001f)
            return (Vector3.Zero, 0f);
        var t = DiffuseTint;
        var et = Vector3.One;
        if (Parameters.TryGetValue("EMISSIVE_TINT", out var v) && v.Length >= 3)
            et = new Vector3(Math.Clamp(v[0], 0, 1), Math.Clamp(v[1], 0, 1), Math.Clamp(v[2], 0, 1));
        var c = new Vector3(t.X * et.X, t.Y * et.Y, t.Z * et.Z);
        if (c.LengthSquared() < 1e-6f)
            return (Vector3.Zero, 0f);
        return (c, mult);
    }

    /// <summary>Что лежит в каналах слота (по дизассемблированным шейдерам).</summary>
    public ChannelSemantics.Channels ChannelsOf(ShaderSlot slot)
        => ChannelSemantics.Describe(Ubershader, slot.SamplerName, Features);

    /// <summary>Slots whose role could not be read from the name.</summary>
    public int UnknownSlots => Slots.Count(s => s.Role == TextureRole.Unknown);

    public ShaderSlot? First(TextureRole role)
        => Slots.FirstOrDefault(s => s.Role == role);

    public static ShaderLayout Read(Materials.Material material)
    {
        var shader = material.Shader;

        // Роль каждого слота — по привязке сэмплера убершейдера (SamplerRemaps:
        // сэмплер -> индекс в списке текстур материала). Имя текстуры вида "[d]"
        // ненадёжно: у 70% материалов уровня буквы в имени нет вовсе
        // (ayz\fx\fx_alien_blood_decal.tga), и без шейдера они шли «без цвета».
        var bySlot = new Dictionary<int, (TextureRole role, string sampler)>();
        if (shader is not null)
        {
            List<string> samplers;
            try { samplers = ShaderUtility.GetSamplers(shader.Ubershader); }
            catch { samplers = new List<string>(); }
            for (int i = 0; i < samplers.Count && i < shader.SamplerRemaps.Count; i++)
            {
                int ti = shader.SamplerRemaps[i];
                if (ti == 255 || ti < 0 || ti >= material.TextureReferences.Count)
                    continue;
                var role = RoleOfSampler(samplers[i]);
                // Первый осмысленный сэмплер на слот побеждает; DIFFUSE важнее любого.
                if (!bySlot.TryGetValue(ti, out var have) || have.role == TextureRole.Unknown
                    || (role == TextureRole.Diffuse && have.role != TextureRole.Diffuse))
                    bySlot[ti] = (role, samplers[i]);
            }
        }

        bool transparent = false, cutout = false, doubleSided = false, alphaGreen = false;
        float cutoff = 0.5f, uvMult = 1f;
        Vector4 tint = Vector4.One;
        if (shader is not null)
        {
            bool hasSeparateAlpha = HasFeature(shader, "SEPARATE_ALPHA")
                                    && bySlot.Values.Any(v => v.sampler == "SEPARATE_ALPHA_MAP");
            transparent = ShouldUseTransparentBlend(shader, hasSeparateAlpha)
                          || shader.Ubershader == SHADER_LIST.CA_DECAL;
            cutout = HasFeature(shader, "ALPHA_TEST") && IsRenderStateEnabled(shader, Shaders.RenderState.AlphaTestEnable);
            doubleSided = HasFeature(shader, "DOUBLE_SIDED");
            alphaGreen = HasFeature(shader, "SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL");
            tint = ReadTint(material, shader);
            uvMult = ReadParam(material, shader, "DIFFUSE_UV_MULT", 1f);
            if (shader.Ubershader == SHADER_LIST.CA_DECAL_ENVIRONMENT)
                cutoff = Math.Clamp(ReadParam(material, shader, "ALPHA_THRESHOLD", 0.5f), 0f, 1f);
        }

        var layout = new ShaderLayout
        {
            MaterialName = material.Name ?? "material",
            EnvironmentMapIndex = material.EnvironmentMapIndex,
            PhysicalMaterialIndex = material.PhysicalMaterialIndex,
            Ubershader = shader?.Ubershader.ToString(),
            DiffuseTint = tint,
            DiffuseUvMult = uvMult,
            Transparent = transparent,
            Cutout = cutout,
            AlphaCutoff = cutoff,
            DoubleSided = doubleSided,
            SeparateAlphaFromGreen = alphaGreen,
        };

        if (shader is not null)
        {
            // Фичи: по значениям enum (в списке есть пропуски — VERTEX_COLOUR=0, FOG_ALPHA=1, ... ALPHA_TEST=7)
            List<string> featureNames;
            try { featureNames = ShaderUtility.GetFeatures(shader.Ubershader); } catch { featureNames = new List<string>(); }
            foreach (var f in featureNames)
                if (HasFeature(shader, f)) layout.Features.Add(f);

            List<string> paramNames;
            try { paramNames = ShaderUtility.GetParameters(shader.Ubershader); } catch { paramNames = new List<string>(); }
            foreach (var pn in paramNames)
            {
                int? idx = ShaderUtility.GetShaderFunctionalityIndex(shader.Ubershader, ShaderIndexType.PARAMETERS, pn);
                if (idx is null || idx.Value < 0 || idx.Value >= shader.PixelShaderParameterRemaps.Count) continue;
                int r = shader.PixelShaderParameterRemaps[idx.Value];
                if (r == 255 || r < 0 || r >= material.PixelShaderConstants.Count) continue;
                int comps = ShaderUtility.GetParameterType(shader.Ubershader, pn) switch
                {
                    UberShaderParameterType.Float2 or UberShaderParameterType.Half2 => 2,
                    UberShaderParameterType.Float3 or UberShaderParameterType.Half3 => 3,
                    UberShaderParameterType.Float4 or UberShaderParameterType.Half4 => 4,
                    _ => 1,
                };
                var vals = new float[Math.Min(comps, material.PixelShaderConstants.Count - r)];
                for (int i = 0; i < vals.Length; i++) vals[i] = material.PixelShaderConstants[r + i];
                layout.Parameters[pn] = vals;
            }
        }

        int index = 0;
        foreach (var pointer in material.TextureReferences)
        {
            var texture = pointer?.Texture;
            if (texture is null)
            {
                index++;
                continue;
            }
            string name = texture.Name ?? string.Empty;
            // Привязка сэмплера — истина в последней инстанции: если шейдер есть,
            // имя файла не смотрим (иначе LUT skin_convolved_diffuse_brdf.dds
            // становился «диффузом» CA_SKIN, и кожа/Чужой выходили чёрными).
            // Слот, который шейдер не читает, роли не имеет.
            TextureRole role;
            string? samplerName = null;
            if (shader is not null)
            {
                role = bySlot.TryGetValue(index, out var bound) ? bound.role : TextureRole.Unknown;
                samplerName = bySlot.TryGetValue(index, out bound) ? bound.sampler : null;
            }
            else
                role = RoleOf(name);
            layout.Slots.Add(new ShaderSlot
            {
                Role = role,
                TextureName = name,
                Texture = texture,
                Index = index,
                SamplerName = samplerName,
            });
            index++;
        }
        return layout;
    }

    /// <summary>Роль по имени сэмплера убершейдера.</summary>
    private static TextureRole RoleOfSampler(string sampler)
    {
        switch (sampler)
        {
            case "DIFFUSE_MAP": case "TEXTURE_MAP": case "DIFFUSE_MAP_0": case "DIFFUSE_MAP_STATIC":
            case "FACE_MAP": case "IRIS_MAP": case "TERRAIN_MAP":
                return TextureRole.Diffuse;
            case "LIGHTMAP_MAP":
                return TextureRole.Detail; // запечённый свет — не базовый цвет
            case "NORMAL_MAP": case "ATMOSPHERE_NORMAL_MAP": case "TERRAIN_NORMAL_MAP":
                return TextureRole.Normal;
            case "SPECULAR_MAP":
                return TextureRole.Specular;
            case "SEPARATE_ALPHA_MAP": case "ALPHA_MASK": case "ALPHATHRESHOLD_MAP":
                return TextureRole.Opacity;
            case "GLOW_MAP": case "INTENSITY_MAP":
                return TextureRole.Emissive;
            case "AMBIENT_OCCLUSION_MAP":
                return TextureRole.Occlusion;
            case "ENVIRONMENT_MAP": case "IRRADIANCE_CUBE_MAP":
                return TextureRole.Environment;
            case "FLOW_MAP": case "FLOW_TEXTURE_MAP":
                return TextureRole.Flow;
            case "DIRT_MAP": case "DUST_MAP": case "MASKING_MAP": case "WRINKLE_MASK":
            case "LOW_LOD_CHARACTER_MASK_TEX": case "UNSCALED_DIRT_MAP": case "ALPHABLEND_NOISE_MAP":
                return TextureRole.Mask;
            case "SECONDARY_DIFFUSE_MAP": case "SECONDARY_NORMAL_MAP": case "SECONDARY_SPECULAR_MAP":
            case "PARALLAX_MAP": case "DISPLACEMENT_MAP": case "DETAIL_MAP": case "WRINKLE_NORMAL_MAP":
                return TextureRole.Detail;
            default:
                return TextureRole.Unknown;
        }
    }

    private static bool HasFeature(Shaders.Shader shader, string feature)
    {
        int? idx = ShaderUtility.GetShaderFunctionalityIndex(shader.Ubershader, ShaderIndexType.FEATURES, feature);
        return idx.HasValue && (shader.UbershaderFeatureFlags & (1L << idx.Value)) != 0;
    }

    private static readonly string[] AlphaBlendFeatures =
        { "USE_ALPHA_AS_BLENDFACTOR", "FORCE_TO_ALPHA", "GLASS", "FOG_ALPHA", "VERTEX_ALPHA_OPACITY_ONLY" };

    // Правила из просмотрщика OpenCAGE: какие материалы рисуются с блендингом.
    private static bool ShouldUseTransparentBlend(Shaders.Shader shader, bool hasSeparateAlpha)
    {
        if (shader.Ubershader == SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT)
            return AlphaBlendFeatures.Any(f => HasFeature(shader, f));
        if (hasSeparateAlpha) return true;
        long req = shader.UbershaderRequirementFlags;
        if ((req & 0x1000000L) != 0 || (req & 0x400000000L) != 0 || (req & 0x400000000000L) != 0
            || (req & 0x40000000L) != 0 || (req & 0x4000000000000L) != 0)
            return true;
        if (AlphaBlendFeatures.Any(f => HasFeature(shader, f))) return true;
        if (HasFeature(shader, "DECAL")) return true;
        return false;
    }

    private static bool IsRenderStateEnabled(Shaders.Shader shader, Shaders.RenderState state)
    {
        var entries = shader.RenderStates?.Entries;
        if (entries is null) return false;
        foreach (var e in entries)
            if (e.StateId == (int)state) return e.Value != 0;
        return false;
    }

    /// <summary>Скалярный параметр пиксельного шейдера по имени из PARAMETERS убершейдера.</summary>
    private static float ReadParam(Materials.Material material, Shaders.Shader shader, string name, float fallback)
    {
        int? idx = ShaderUtility.GetShaderFunctionalityIndex(shader.Ubershader, ShaderIndexType.PARAMETERS, name);
        if (idx is null || idx.Value < 0 || idx.Value >= shader.PixelShaderParameterRemaps.Count) return fallback;
        int r = shader.PixelShaderParameterRemaps[idx.Value];
        if (r == 255 || r < 0 || r >= material.PixelShaderConstants.Count) return fallback;
        return material.PixelShaderConstants[r];
    }

    private static Vector4 ReadTint(Materials.Material material, Shaders.Shader shader)
    {
        string name = shader.Ubershader switch
        {
            SHADER_LIST.CA_EFFECT_OVERLAY => "COLOUR_TINT",
            SHADER_LIST.CA_PLANET => "ATMOSPHERE_RIM_COLOUR",
            _ => "DIFFUSE_TINT",
        };
        int? idx = ShaderUtility.GetShaderFunctionalityIndex(shader.Ubershader, ShaderIndexType.PARAMETERS, name);
        if (idx is null || idx.Value < 0 || idx.Value >= shader.PixelShaderParameterRemaps.Count) return Vector4.One;
        int r = shader.PixelShaderParameterRemaps[idx.Value];
        var consts = material.PixelShaderConstants;
        if (r == 255 || r < 0 || r >= consts.Count) return Vector4.One;
        var type = ShaderUtility.GetParameterType(shader.Ubershader, name);
        int comps = type is UberShaderParameterType.Float3 or UberShaderParameterType.Half3 ? 3 : 4;
        float x = consts[r];
        float y = r + 1 < consts.Count ? consts[r + 1] : 0;
        float z = r + 2 < consts.Count ? consts[r + 2] : 0;
        float w = comps == 4 && r + 3 < consts.Count ? consts[r + 3] : 1;
        return new Vector4(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), Math.Clamp(z, 0, 1), Math.Clamp(w, 0, 1));
    }

    /// <summary>
    /// The bracketed letter in a texture name says what it is for. Several letters map
    /// to the same idea, and a few names spell the role out instead.
    /// </summary>
    private static TextureRole RoleOf(string name)
    {
        if (Has(name, 'd')) return TextureRole.Diffuse;
        if (Has(name, 'n')) return TextureRole.Normal;
        if (Has(name, 's')) return TextureRole.Specular;
        if (Has(name, 'o')) return TextureRole.Opacity;
        if (Has(name, 'e')) return TextureRole.Emissive;
        if (Has(name, 'a')) return TextureRole.Occlusion;
        if (Has(name, 'c')) return TextureRole.Colour;
        if (Has(name, 'm')) return TextureRole.Mask;
        if (Has(name, 'f')) return TextureRole.Flow;

        string upper = name.ToUpperInvariant();
        if (upper.Contains("NORMAL")) return TextureRole.Normal;
        if (upper.Contains("SPEC")) return TextureRole.Specular;
        if (upper.Contains("DIFF") || upper.Contains("ALBEDO")) return TextureRole.Diffuse;
        if (upper.Contains("ENV") || upper.Contains("CUBE")) return TextureRole.Environment;
        if (upper.Contains("DETAIL")) return TextureRole.Detail;
        if (upper.Contains("OPACITY") || upper.Contains("ALPHA")) return TextureRole.Opacity;
        if (upper.Contains("GLOW") || upper.Contains("EMISS")) return TextureRole.Emissive;
        return TextureRole.Unknown;
    }

    private static bool Has(string name, char role)
        => name.Contains($"[{role}]", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pixel size, taken from whichever part carries data. A texture is stored as a
    /// streamed part and a persistent part, and either one can be the empty stub.
    /// </summary>
    private static string Dimensions(Textures.TEX4 texture)
    {
        var streamed = texture.TextureStreamed;
        if (streamed?.Content is { Length: > 0 } && streamed.Width > 0)
            return $"{streamed.Width}x{streamed.Height}, мипов {streamed.MipLevels}";

        var persistent = texture.TexturePersistent;
        if (persistent?.Content is { Length: > 0 } && persistent.Width > 0)
            return $"{persistent.Width}x{persistent.Height}, мипов {persistent.MipLevels}";

        return "размер не заявлен";
    }

    /// <summary>A readable account of the material, channel by channel.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"материал: {MaterialName}" + (Ubershader is null ? "" : $"  [{Ubershader}]"));
        sb.AppendLine($"текстур: {Slots.Count}" +
                      (UnknownSlots > 0 ? $", из них с неясной ролью {UnknownSlots}" : ""));
        sb.AppendLine();

        if (Slots.Count == 0)
        {
            sb.AppendLine("  текстур нет, поверхность рисуется одним цветом");
        }
        else
        {
            sb.AppendLine("что во что идёт:");
            foreach (var slot in Slots)
            {
                sb.AppendLine($"  [{slot.Index}] {slot.RoleName,-24} {slot.TextureName}" + (slot.SamplerName is null ? "" : $"   ← {slot.SamplerName}"));
                if (slot.Texture is not null)
                    sb.AppendLine($"       {slot.Texture.Format}, {Dimensions(slot.Texture)}");
                var ch = ChannelsOf(slot);
                if (ch != ChannelSemantics.Channels.Unknown)
                {
                    sb.AppendLine($"       R: {ch.R}   G: {ch.G}");
                    sb.AppendLine($"       B: {ch.B}   A: {ch.A}");
                    if (ch.Blender.Length > 0) sb.AppendLine($"       Blender: {ch.Blender}");
                }
                if (slot.GltfTarget is not null)
                    sb.AppendLine($"       в glTF: {slot.GltfTarget}");
                else
                    sb.AppendLine("       в glTF: своего слота нет, уходит в extras");
            }
        }

        if (Features.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("фичи шейдера: " + string.Join(", ", Features));
        }
        if (Parameters.Count > 0)
        {
            sb.AppendLine("параметры:");
            foreach (var kv in Parameters)
                sb.AppendLine($"  {kv.Key} = {string.Join(", ", kv.Value.Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))}");
        }
        sb.AppendLine($"альфа: {(Transparent ? "BLEND" : Cutout ? $"MASK (порог {AlphaCutoff:0.##})" : "OPAQUE")}, {(DoubleSided ? "двусторонний" : "односторонний")}");
        sb.AppendLine();
        sb.AppendLine($"карта окружения: {EnvironmentMapIndex}");
        sb.AppendLine($"физический материал: {PhysicalMaterialIndex}");
        sb.AppendLine();
        sb.AppendLine("как это влияет на свет:");
        sb.AppendLine(Explain());
        return sb.ToString();
    }

    /// <summary>
    /// Plain words for what the maps do together, which is the part a texture list
    /// never says.
    /// </summary>
    private string Explain()
    {
        var sb = new StringBuilder();
        var diffuse = First(TextureRole.Diffuse) ?? First(TextureRole.Colour);
        var normal = First(TextureRole.Normal);
        var specular = First(TextureRole.Specular);

        sb.AppendLine(diffuse is not null
            ? "  цвет берётся из карты diffuse — это собственная окраска поверхности,"
            : "  карты цвета нет, поверхность окрашена постоянным цветом,");
        sb.AppendLine("  она не зависит от того, откуда падает свет.");

        if (normal is not null)
            sb.AppendLine("  карта нормалей наклоняет нормаль каждого пиксела, поэтому " +
                          "плоский треугольник\n  отражает свет как рельефный: мелкие детали " +
                          "появляются без лишних вершин.");
        else
            sb.AppendLine("  карты нормалей нет, свет считается по нормалям вершин, " +
                          "поверхность гладкая.");

        if (specular is not null)
            sb.AppendLine("  карта блика решает, где поверхность отражает как металл или " +
                          "мокрое,\n  а где гасит отражение: она управляет силой и " +
                          "резкостью светового пятна.");
        else
            sb.AppendLine("  карты блика нет, отражение одинаково по всей поверхности.");

        if (EnvironmentMapIndex > 0)
            sb.AppendLine("  задана карта окружения, поэтому в поверхности отражается сцена, " +
                          "а не только источники света.");
        return sb.ToString();
    }
}
