namespace AlienForge.Core.Export;

/// <summary>
/// Что лежит в каналах RGBA каждой карты убершейдера — по дизассемблированным
/// пиксельным шейдерам игры (LEVEL_SHADERS_DX11.PAK, d3dcompiler D3DDisassemble).
/// </summary>
/// <remarks>
/// Как это читалось: в дизассемблере имена ресурсов сохраняются
/// (DIFFUSE_MAP_SAMPLER_TEX → t1), а у каждой инструкции sample видно, какие
/// компоненты текстуры реально уходят дальше (t1.xyz, t2.xy, t3.xyz…) и куда.
/// Главные факты:
///   • DIFFUSE_MAP.rgb возводится в квадрат (гамма 2 → линейное), умножается на
///     DIFFUSE_TINT и на цвет вершин (VERTEX_COLOUR); .a — непрозрачность.
///   • NORMAL_MAP читается только .rg (xy*2-1), z = sqrt(1-x²-y²): B и A не
///     используются никогда — карты можно хранить как BC5/двухканальные.
///   • SPECULAR_MAP: .r × SPECULAR_TINT — интенсивность блика (F0),
///     .g × SPECULAR_POWER — глянец (0..1, clamp 0.996; roughness = 1 - g),
///     .b — зависит от фичи: SPECULAR_MAPPING_METALNESS_MASKING → металличность
///     (CA_ENVIRONMENT), FRONT_ROUGHNESS/DIFFUSE_ROUGHNESS → маска шероховатости
///     диффуза (CA_CHARACTER), у CA_SKIN — маска вторичной normal-карты
///     (SECONDARY_SPEC_NORMAL_MASKING_MIN/MAX). .a не читается.
///   • AMBIENT_OCCLUSION_MAP, PARALLAX_MAP, ALPHABLEND_NOISE_MAP — только .g.
///   • SEPARATE_ALPHA_MAP — .r (или .g при SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL).
///   • DIRT_MAP.rgb (в квадрате) подмешивается к альбедо по весу из цвета вершин
///     (v3.x) с показателем DIRT_BLEND_MULT_SPEC_POWER.
///   • G-буфер: RT0 = свечение/forward + альфа, RT1 = нормаль*0.5+0.5,
///     RT2 = sqrt(альбедо/2) (кодировка), RT3 = (глянец, 0.5+0.5·F0, флаг, …).
/// </remarks>
public static class ChannelSemantics
{
    public sealed record Channels(string R, string G, string B, string A, string Blender)
    {
        public static readonly Channels Unknown = new("?", "?", "?", "?", "");
    }

    public static Channels Describe(string? ubershader, string? sampler, IReadOnlyCollection<string> features)
    {
        bool Has(string f) => features.Contains(f);
        string u = ubershader ?? "";
        switch (sampler)
        {
            case "DIFFUSE_MAP":
            case "SECONDARY_DIFFUSE_MAP":
            case "DIFFUSE_MAP_0":
            case "DIFFUSE_MAP_1":
            case "DIFFUSE_MAP_STATIC":
            case "TEXTURE_MAP":
            case "TEXTURE_MAP2":
                return new("альбедо R (гамма 2)", "альбедо G", "альбедо B",
                    u == "CA_HAIR" ? "непрозрачность волос" : (u.StartsWith("CA_PARTICLE") || u == "CA_RIBBON" || u == "CA_DECAL" || u == "CA_EFFECT_OVERLAY") ? "непрозрачность" : "непрозрачность (при ALPHA_TEST / блендинге)",
                    "Image (sRGB) → Base Color; × DIFFUSE_TINT; × Color Attribute при VERTEX_COLOUR; A → Alpha если не OPAQUE");
            case "NORMAL_MAP":
            case "SECONDARY_NORMAL_MAP":
            case "WRINKLE_NORMAL_MAP":
            case "ATMOSPHERE_NORMAL_MAP":
            case "TERRAIN_NORMAL_MAP":
                return new("нормаль X (tangent)", "нормаль Y (tangent)", "не читается (z = sqrt(1-x²-y²))", "не читается",
                    "Image (Non-Color) → Separate → z=sqrt(1-(2R-1)²-(2G-1)²) → Combine(R,G,(z+1)/2) → Normal Map (Tangent) → Normal");
            case "SPECULAR_MAP":
            case "SECONDARY_SPECULAR_MAP":
            {
                string b = u == "CA_SKIN" ? "маска вторичной normal-карты (SECONDARY_SPEC_NORMAL_MASKING_MIN..MAX)"
                    : Has("SPECULAR_MAPPING_METALNESS_MASKING") ? "металличность"
                    : Has("FRONT_ROUGHNESS") || Has("DIFFUSE_ROUGHNESS") ? "маска шероховатости диффуза (× DIFFUSE_ROUGHNESS_FACTOR)"
                    : u == "CA_HAIR" ? "не читается" : "не читается в этом варианте шейдера";
                if (u == "CA_HAIR")
                    return new("интенсивность блика", "не читается", "не читается", "не читается",
                        "Image (Non-Color) → R → Specular IOR Level");
                return new("интенсивность блика F0 (× SPECULAR_TINT)", "глянец (× SPECULAR_POWER); roughness = 1 − G", b, "не читается",
                    "Image (Non-Color) → Separate: R×SPECULAR_TINT/0.08 → Specular IOR Level; 1−G×SPECULAR_POWER → Roughness"
                    + (Has("SPECULAR_MAPPING_METALNESS_MASKING") ? "; B → Metallic" : ""));
            }
            case "AMBIENT_OCCLUSION_MAP":
                return new("не читается", "затенение (AO)", "не читается", "не читается", "G → Mix Multiply с Base Color (или AO-нода)");
            case "PARALLAX_MAP":
            case "DISPLACEMENT_MAP":
                return new("не читается", "высота (parallax)", "не читается", "не читается", "G → Bump/Displacement Height");
            case "ALPHABLEND_NOISE_MAP":
                return new("не читается", "шум растворения (ALPHABLEND_NOISE)", "не читается", "не читается", "не нужно в Blender");
            case "SEPARATE_ALPHA_MAP":
                return Has("SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL")
                    ? new("не читается", "непрозрачность", "не читается", "не читается", "G → Alpha (Blend)")
                    : new("непрозрачность", "не читается", "не читается", "не читается", "R → Alpha (Blend)");
            case "ALPHATHRESHOLD_MAP":
                return new("не читается", "не читается", "не читается", "порог вырезания", "A → Alpha Clip threshold");
            case "DIRT_MAP":
            case "DUST_MAP":
            case "UNSCALED_DIRT_MAP":
                return new("грязь R (гамма 2)", "грязь G", "грязь B", "маска (редко)",
                    "Image (sRGB) → Mix(Multiply) с Base Color, фактор = Color Attribute R (вес грязи по вершинам) ^ DIRT_BLEND_MULT_SPEC_POWER");
            case "ENVIRONMENT_MAP":
                return new("отражение R", "отражение G", "отражение B", "не читается", "кубическая карта — в Blender заменяется World/Reflection Probe");
            case "IRRADIANCE_CUBE_MAP":
                return new("рассеянный свет R", "G", "B", "не читается", "не нужно (Blender считает GI сам)");
            case "LIGHTMAP_MAP":
                return new("запечённый свет R", "G", "B", "не читается", "Image (sRGB) → Mix Multiply / Emission");
            case "GLOW_MAP":
                return new("свечение R", "свечение G", "свечение B", "маска свечения", "Image (sRGB) → Emission Color/Strength");
            case "INTENSITY_MAP":
                return new("интенсивность R", "G", "B", "не читается", "Emission");
            case "FLOW_MAP":
            case "FLOW_TEXTURE_MAP":
                return new("направление X", "направление Y", "не читается", "не читается",
                    u == "CA_HAIR" ? "направление прядей (анизотропия) → Tangent для Anisotropic" : "flow map для анимации UV");
            case "WRINKLE_MASK":
                return new("вес морщин, зона 1", "зона 2", "зона 3", "зона 4", "маски смешивания WRINKLE_NORMAL_MAP (две карты в одной: левая/правая половины по U)");
            case "IRIS_MAP":
                return new("радужка R", "G", "B", "не читается", "Base Color радужки");
            case "VEINS_MAP":
                return new("склера/сосуды R", "G", "B", "не читается", "Base Color склеры");
            case "SCATTER_MAP":
                return new("подповерхностное рассеивание R", "G", "B", "не читается", "Subsurface Color");
            case "FACE_MAP":
                return new("лицо за визором R", "G", "B", "не читается", "Base Color");
            case "MASKING_MAP":
            case "LOW_LOD_CHARACTER_MASK_TEX":
                return new("маска", "маска", "маска", "маска (.a читается)", "маска смешивания");
            case "COLOUR_RAMP_MAP":
                return new("градиент R", "G", "B", "A", "ColorRamp по возрасту частицы");
            case "CONVOLVED_BRDF_MAX_HACK":
                return new("LUT BRDF кожи", "", "", "", "не нужно");
            case "SPARKLE_MAP":
            case "FROST_MAP":
            case "BURNTHROUGH_MAP":
            case "LIQUIFX_MAP":
            case "LIQUIFX2_MAP":
                return new("эффект", "эффект", "эффект", "эффект", "спецэффект, в Blender не переносится");
            default:
                return Channels.Unknown;
        }
    }
}
