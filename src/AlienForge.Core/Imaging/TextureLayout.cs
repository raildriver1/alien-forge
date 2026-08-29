using CATHODE;
using Fmt = CATHODE.Textures.TextureFormat;

namespace AlienForge.Core.Imaging;

/// <summary>How one CATHODE texture format is stored in memory.</summary>
public readonly record struct ChannelMasks(
    uint RedMask, uint GreenMask, uint BlueMask, uint AlphaMask, bool Luminance = false)
{
    public static readonly ChannelMasks None = new(0, 0, 0, 0);
}

/// <summary>
/// Size and container facts for a texture format: enough to walk a mip chain and
/// to build a DDS header for it.
/// </summary>
public sealed record TextureLayout
{
    public required Fmt Format { get; init; }

    /// <summary>Bytes per 4x4 block, or 0 when the format is not block compressed.</summary>
    public int BytesPerBlock { get; init; }

    /// <summary>Bytes per pixel for uncompressed formats, otherwise 0.</summary>
    public int BytesPerPixel { get; init; }

    /// <summary>Legacy DDS FourCC, when one exists.</summary>
    public uint FourCc { get; init; }

    /// <summary>DXGI format for formats that need the DX10 header extension.</summary>
    public uint DxgiFormat { get; init; }

    public ChannelMasks Masks { get; init; } = ChannelMasks.None;

    /// <summary>True for a two-channel tangent-space normal map needing Z rebuilt.</summary>
    public bool IsTwoChannelNormal { get; init; }

    /// <summary>Null when this tool can decode the format itself.</summary>
    public string? UnsupportedReason { get; init; }

    public bool IsBlockCompressed => BytesPerBlock > 0;
    public bool CanDecode => UnsupportedReason is null;

    /// <summary>Compressed or raw byte count of one mip level.</summary>
    public int MipSize(int width, int height)
        => IsBlockCompressed
            ? BlockDecoders.MipSize(width, height, BytesPerBlock)
            : Math.Max(1, width) * Math.Max(1, height) * BytesPerPixel;

    // ------------------------------------------------------------------ table
    private const uint FourCcDxt1 = 0x31545844; // "DXT1"
    private const uint FourCcDxt3 = 0x33545844; // "DXT3"
    private const uint FourCcDxt5 = 0x35545844; // "DXT5"
    private const uint FourCcAti2 = 0x32495441; // "ATI2", how DXN is tagged in DDS

    private const uint DxgiBc5Unorm = 83;
    private const uint DxgiBc6HUf16 = 95;
    private const uint DxgiBc7Unorm = 98;
    private const uint DxgiR16Float = 54;
    private const uint DxgiR16G16B16A16Unorm = 11;
    private const uint DxgiR32G32B32A32Float = 2;

    public static TextureLayout For(Fmt format) => format switch
    {
        Fmt.DXT1 => new TextureLayout
        {
            Format = format, BytesPerBlock = 8, FourCc = FourCcDxt1,
        },
        Fmt.DXT3 => new TextureLayout
        {
            Format = format, BytesPerBlock = 16, FourCc = FourCcDxt3,
        },
        Fmt.DXT5 => new TextureLayout
        {
            Format = format, BytesPerBlock = 16, FourCc = FourCcDxt5,
        },
        // DXN is BC5: two channels, and the third has to be rebuilt from them.
        Fmt.DXN => new TextureLayout
        {
            Format = format, BytesPerBlock = 16, FourCc = FourCcAti2,
            DxgiFormat = DxgiBc5Unorm, IsTwoChannelNormal = true,
        },
        Fmt.BC7 => new TextureLayout
        {
            Format = format, BytesPerBlock = 16, DxgiFormat = DxgiBc7Unorm,
        },
        // BC6H is HDR and needs its own decoder. The platform DDS codec cannot help:
        // probing showed it handles only BC1/BC2/BC3. Saving as .dds still works,
        // so the data is not lost, it just cannot be shown as 8-bit RGBA yet.
        Fmt.BC6H => new TextureLayout
        {
            Format = format, BytesPerBlock = 16, DxgiFormat = DxgiBc6HUf16,
            UnsupportedReason = "BC6H (HDR) пока без декодера — экспортируйте как .dds.",
        },

        // D3D9 channel names, so the bytes in memory run B, G, R, A.
        Fmt.A8R8G8B8 => new TextureLayout
        {
            Format = format, BytesPerPixel = 4,
            Masks = new ChannelMasks(0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000),
        },
        Fmt.X8R8G8B8 => new TextureLayout
        {
            Format = format, BytesPerPixel = 4,
            Masks = new ChannelMasks(0x00FF0000, 0x0000FF00, 0x000000FF, 0),
        },
        Fmt.A8 => new TextureLayout
        {
            Format = format, BytesPerPixel = 1,
            Masks = new ChannelMasks(0, 0, 0, 0xFF),
        },
        Fmt.L8 => new TextureLayout
        {
            Format = format, BytesPerPixel = 1,
            Masks = new ChannelMasks(0xFF, 0, 0, 0, Luminance: true),
        },
        Fmt.A4R4G4B4 => new TextureLayout
        {
            Format = format, BytesPerPixel = 2,
            Masks = new ChannelMasks(0x0F00, 0x00F0, 0x000F, 0xF000),
        },
        Fmt.R16F => new TextureLayout
        {
            Format = format, BytesPerPixel = 2, DxgiFormat = DxgiR16Float,
        },
        Fmt.A16R16G16B16 => new TextureLayout
        {
            Format = format, BytesPerPixel = 8, DxgiFormat = DxgiR16G16B16A16Unorm,
        },
        Fmt.A32R32G32B32F => new TextureLayout
        {
            Format = format, BytesPerPixel = 16, DxgiFormat = DxgiR32G32B32A32Float,
        },

        // CTX1 is a console-era two-channel scheme; ASTC only ships in the mobile
        // ports. Neither appears in the PC data, so they are reported rather than
        // guessed at.
        Fmt.CTX1 => new TextureLayout
        {
            Format = format, BytesPerBlock = 8,
            UnsupportedReason = "CTX1 не поддержан (формат консольных сборок).",
        },
        Fmt.ASTC4X4 or Fmt.ASTC8X8 or Fmt.ASTC12X12 => new TextureLayout
        {
            Format = format, BytesPerBlock = 16,
            UnsupportedReason = "ASTC не поддержан (только мобильные порты Feral).",
        },
        _ => new TextureLayout
        {
            Format = format,
            UnsupportedReason = $"Неизвестный формат {format}.",
        },
    };
}
