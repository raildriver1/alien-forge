using System.Buffers.Binary;

namespace AlienForge.Core.Imaging;

/// <summary>
/// Wraps a raw mip chain in a DDS container.
/// </summary>
/// <remarks>
/// The game stores texture payloads with no header at all: just the compressed mip
/// chain, biggest first. A DDS wrapper is needed twice over, once so the data can
/// be handed to the platform BC7/BC6H decoder, and once so textures can be saved
/// in a form other tools accept.
/// </remarks>
public static class DdsWriter
{
    private const uint Magic = 0x20534444; // "DDS "

    // DDS_HEADER.dwFlags
    private const uint Caps = 0x1, Height = 0x2, Width = 0x4, PixelFormat = 0x1000;
    private const uint MipMapCount = 0x20000, LinearSize = 0x80000, Pitch = 0x8;

    // DDS_PIXELFORMAT.dwFlags
    private const uint FourCcFlag = 0x4, RgbFlag = 0x40, AlphaPixels = 0x1, LuminanceFlag = 0x20000;

    // DDSCAPS
    private const uint CapsTexture = 0x1000, CapsMipMap = 0x400000, CapsComplex = 0x8;

    public const int HeaderSize = 128;
    public const int Dx10HeaderSize = 148;

    /// <summary>
    /// Builds a DDS for a payload that is already laid out as a mip chain.
    /// </summary>
    public static byte[] Wrap(TextureLayout layout, ReadOnlySpan<byte> payload, int width,
        int height, int mipCount)
    {
        bool dx10 = layout.DxgiFormat != 0;
        int headerBytes = dx10 ? Dx10HeaderSize : HeaderSize;
        var output = new byte[headerBytes + payload.Length];
        var span = output.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        var h = span.Slice(4, 124);
        h.Clear();

        uint flags = Caps | Height | Width | PixelFormat;
        if (mipCount > 1)
            flags |= MipMapCount;
        flags |= layout.IsBlockCompressed ? LinearSize : Pitch;

        Write(h, 0, 124);                       // dwSize
        Write(h, 4, flags);
        Write(h, 8, (uint)height);
        Write(h, 12, (uint)width);
        Write(h, 16, (uint)(layout.IsBlockCompressed
            ? BlockDecoders.MipSize(width, height, layout.BytesPerBlock)
            : width * layout.BytesPerPixel));   // dwPitchOrLinearSize
        Write(h, 20, 0);                        // dwDepth
        Write(h, 24, (uint)Math.Max(1, mipCount));

        // DDS_PIXELFORMAT at offset 72 of the header
        int pf = 72;
        Write(h, pf + 0, 32);
        if (dx10)
        {
            Write(h, pf + 4, FourCcFlag);
            Write(h, pf + 8, 0x30315844); // "DX10"
        }
        else if (layout.FourCc != 0)
        {
            Write(h, pf + 4, FourCcFlag);
            Write(h, pf + 8, layout.FourCc);
        }
        else
        {
            uint pfFlags = layout.Masks.Luminance ? LuminanceFlag : RgbFlag;
            if (layout.Masks.AlphaMask != 0)
                pfFlags |= AlphaPixels;
            Write(h, pf + 4, pfFlags);
            Write(h, pf + 12, (uint)(layout.BytesPerPixel * 8));
            Write(h, pf + 16, layout.Masks.RedMask);
            Write(h, pf + 20, layout.Masks.GreenMask);
            Write(h, pf + 24, layout.Masks.BlueMask);
            Write(h, pf + 28, layout.Masks.AlphaMask);
        }

        uint caps = CapsTexture;
        if (mipCount > 1)
            caps |= CapsMipMap | CapsComplex;
        Write(h, 104, caps);

        if (dx10)
        {
            var ext = span.Slice(HeaderSize, 20);
            ext.Clear();
            Write(ext, 0, layout.DxgiFormat);
            Write(ext, 4, 3);  // D3D10_RESOURCE_DIMENSION_TEXTURE2D
            Write(ext, 8, 0);  // miscFlag
            Write(ext, 12, 1); // arraySize
            Write(ext, 16, 0); // miscFlags2: alpha mode unknown
        }

        payload.CopyTo(span.Slice(headerBytes));
        return output;
    }

    private static void Write(Span<byte> target, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(offset, 4), value);
}
