using System.Buffers.Binary;

namespace AlienForge.Core.Imaging;

/// <summary>
/// Decoders for the block-compressed formats this game uses, written out directly
/// because they are short and exact: BC1/BC2/BC3 colour, BC4 single channel and
/// BC5 (shipped here under its ATI name, DXN) two channel.
/// </summary>
public static class BlockDecoders
{
    public static int BlocksWide(int width) => Math.Max(1, (width + 3) / 4);

    public static int BlocksHigh(int height) => Math.Max(1, (height + 3) / 4);

    /// <summary>Compressed byte count of one mip level.</summary>
    public static int MipSize(int width, int height, int bytesPerBlock)
        => BlocksWide(width) * BlocksHigh(height) * bytesPerBlock;

    // ------------------------------------------------------------------- BC1
    public static RgbaImage DecodeBc1(ReadOnlySpan<byte> data, int width, int height)
    {
        var img = new RgbaImage(width, height);
        int bw = BlocksWide(width), bh = BlocksHigh(height);
        Span<uint> colours = stackalloc uint[4];

        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int at = (by * bw + bx) * 8;
                if (at + 8 > data.Length)
                    return img;
                DecodeBc1Colours(data.Slice(at, 8), colours);
                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at + 4, 4));
                EmitColours(img, bx, by, colours, bits);
            }
        return img;
    }

    // ------------------------------------------------------------------- BC2
    public static RgbaImage DecodeBc2(ReadOnlySpan<byte> data, int width, int height)
    {
        var img = new RgbaImage(width, height);
        int bw = BlocksWide(width), bh = BlocksHigh(height);
        Span<uint> colours = stackalloc uint[4];

        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int at = (by * bw + bx) * 16;
                if (at + 16 > data.Length)
                    return img;

                // BC2 colour half is always in four-colour mode, so decode it that way
                // regardless of the c0 > c1 test.
                DecodeBc1Colours(data.Slice(at + 8, 8), colours, forceFourColour: true);
                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at + 12, 4));
                ulong alpha = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(at, 8));

                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++)
                    {
                        int i = py * 4 + px;
                        uint c = colours[(int)((bits >> (i * 2)) & 3)];
                        int nibble = (int)((alpha >> (i * 4)) & 0xF);
                        byte a = (byte)(nibble * 17); // 0..15 spread over 0..255
                        img.Set(bx * 4 + px, by * 4 + py,
                            (byte)(c >> 16), (byte)(c >> 8), (byte)c, a);
                    }
            }
        return img;
    }

    // ------------------------------------------------------------------- BC3
    public static RgbaImage DecodeBc3(ReadOnlySpan<byte> data, int width, int height)
    {
        var img = new RgbaImage(width, height);
        int bw = BlocksWide(width), bh = BlocksHigh(height);
        Span<uint> colours = stackalloc uint[4];
        Span<byte> alpha = stackalloc byte[16];

        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int at = (by * bw + bx) * 16;
                if (at + 16 > data.Length)
                    return img;

                DecodeBc4Block(data.Slice(at, 8), alpha);
                DecodeBc1Colours(data.Slice(at + 8, 8), colours, forceFourColour: true);
                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at + 12, 4));

                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++)
                    {
                        int i = py * 4 + px;
                        uint c = colours[(int)((bits >> (i * 2)) & 3)];
                        img.Set(bx * 4 + px, by * 4 + py,
                            (byte)(c >> 16), (byte)(c >> 8), (byte)c, alpha[i]);
                    }
            }
        return img;
    }

    // ------------------------------------------------------------------- BC4
    /// <summary>Single channel, written into R with G=B=R and opaque alpha.</summary>
    public static RgbaImage DecodeBc4(ReadOnlySpan<byte> data, int width, int height)
    {
        var img = new RgbaImage(width, height);
        int bw = BlocksWide(width), bh = BlocksHigh(height);
        Span<byte> values = stackalloc byte[16];

        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int at = (by * bw + bx) * 8;
                if (at + 8 > data.Length)
                    return img;
                DecodeBc4Block(data.Slice(at, 8), values);
                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++)
                    {
                        byte v = values[py * 4 + px];
                        img.Set(bx * 4 + px, by * 4 + py, v, v, v, 255);
                    }
            }
        return img;
    }

    // ------------------------------------------------------------- BC5 / DXN
    /// <summary>
    /// Two independent BC4 blocks: the first is X (red), the second Y (green).
    /// Blue is left at zero here; callers that know this is a normal map should
    /// follow up with <see cref="RgbaImage.ReconstructNormalZ"/>.
    /// </summary>
    public static RgbaImage DecodeBc5(ReadOnlySpan<byte> data, int width, int height)
    {
        var img = new RgbaImage(width, height);
        int bw = BlocksWide(width), bh = BlocksHigh(height);
        Span<byte> red = stackalloc byte[16];
        Span<byte> green = stackalloc byte[16];

        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int at = (by * bw + bx) * 16;
                if (at + 16 > data.Length)
                    return img;
                DecodeBc4Block(data.Slice(at, 8), red);
                DecodeBc4Block(data.Slice(at + 8, 8), green);
                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++)
                    {
                        int i = py * 4 + px;
                        img.Set(bx * 4 + px, by * 4 + py, red[i], green[i], 0, 255);
                    }
            }
        return img;
    }

    // --------------------------------------------------------------- helpers
    /// <summary>
    /// The four palette entries of a BC1-style colour block, packed as 0xRRGGBB.
    /// When c0 &lt;= c1 the block is in three-colour mode and index 3 is fully
    /// transparent, which is how DXT1 encodes cutout alpha.
    /// </summary>
    private static void DecodeBc1Colours(ReadOnlySpan<byte> block, Span<uint> colours,
        bool forceFourColour = false)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block[..2]);
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(2, 2));

        (byte r0, byte g0, byte b0) = From565(c0);
        (byte r1, byte g1, byte b1) = From565(c1);

        colours[0] = Pack(r0, g0, b0, 255);
        colours[1] = Pack(r1, g1, b1, 255);

        if (c0 > c1 || forceFourColour)
        {
            colours[2] = Pack((byte)((2 * r0 + r1) / 3), (byte)((2 * g0 + g1) / 3),
                (byte)((2 * b0 + b1) / 3), 255);
            colours[3] = Pack((byte)((r0 + 2 * r1) / 3), (byte)((g0 + 2 * g1) / 3),
                (byte)((b0 + 2 * b1) / 3), 255);
        }
        else
        {
            colours[2] = Pack((byte)((r0 + r1) / 2), (byte)((g0 + g1) / 2),
                (byte)((b0 + b1) / 2), 255);
            colours[3] = Pack(0, 0, 0, 0);
        }
    }

    private static void EmitColours(RgbaImage img, int bx, int by, ReadOnlySpan<uint> colours,
        uint bits)
    {
        for (int py = 0; py < 4; py++)
            for (int px = 0; px < 4; px++)
            {
                int i = py * 4 + px;
                uint c = colours[(int)((bits >> (i * 2)) & 3)];
                img.Set(bx * 4 + px, by * 4 + py,
                    (byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24));
            }
    }

    /// <summary>
    /// Expands one 8-byte BC4 block into sixteen bytes.
    /// </summary>
    private static void DecodeBc4Block(ReadOnlySpan<byte> block, Span<byte> output)
    {
        byte a0 = block[0];
        byte a1 = block[1];
        Span<byte> palette = stackalloc byte[8];
        palette[0] = a0;
        palette[1] = a1;

        if (a0 > a1)
        {
            // six interpolated steps between the endpoints
            for (int i = 1; i <= 6; i++)
                palette[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
        }
        else
        {
            // four interpolated steps, then hard 0 and 255
            for (int i = 1; i <= 4; i++)
                palette[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
            palette[6] = 0;
            palette[7] = 255;
        }

        // 16 three-bit indices packed into the remaining 6 bytes
        ulong bits = 0;
        for (int i = 0; i < 6; i++)
            bits |= (ulong)block[2 + i] << (8 * i);

        for (int i = 0; i < 16; i++)
            output[i] = palette[(int)((bits >> (i * 3)) & 7)];
    }

    private static (byte r, byte g, byte b) From565(ushort c)
    {
        int r5 = (c >> 11) & 0x1F;
        int g6 = (c >> 5) & 0x3F;
        int b5 = c & 0x1F;
        // replicate high bits into the low ones so 31 maps to 255, not 248
        return ((byte)((r5 << 3) | (r5 >> 2)),
                (byte)((g6 << 2) | (g6 >> 4)),
                (byte)((b5 << 3) | (b5 >> 2)));
    }

    private static uint Pack(byte r, byte g, byte b, byte a)
        => ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
}
