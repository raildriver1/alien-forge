using System.Text;
using CATHODE;

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

    public override string ToString() => $"{RoleName}: {TextureName}";
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

    /// <summary>Slots whose role could not be read from the name.</summary>
    public int UnknownSlots => Slots.Count(s => s.Role == TextureRole.Unknown);

    public ShaderSlot? First(TextureRole role)
        => Slots.FirstOrDefault(s => s.Role == role);

    public static ShaderLayout Read(Materials.Material material)
    {
        var layout = new ShaderLayout
        {
            MaterialName = material.Name ?? "material",
            EnvironmentMapIndex = material.EnvironmentMapIndex,
            PhysicalMaterialIndex = material.PhysicalMaterialIndex,
        };

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
            layout.Slots.Add(new ShaderSlot
            {
                Role = RoleOf(name),
                TextureName = name,
                Texture = texture,
                Index = index,
            });
            index++;
        }
        return layout;
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
        sb.AppendLine($"материал: {MaterialName}");
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
                sb.AppendLine($"  [{slot.Index}] {slot.RoleName,-24} {slot.TextureName}");
                if (slot.Texture is not null)
                    sb.AppendLine($"       {slot.Texture.Format}, {Dimensions(slot.Texture)}");
                if (slot.GltfTarget is not null)
                    sb.AppendLine($"       в glTF: {slot.GltfTarget}");
                else
                    sb.AppendLine("       в glTF: своего слота нет, уходит в extras");
            }
        }

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
