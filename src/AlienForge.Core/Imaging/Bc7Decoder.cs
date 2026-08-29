using System.Buffers.Binary;

namespace AlienForge.Core.Imaging;

/// <summary>
/// BC7 (BPTC UNORM) decoder.
/// </summary>
/// <remarks>
/// Written here rather than delegated to the platform because the Windows DDS
/// codec turned out to handle only BC1, BC2 and BC3: probing it with the Alien's
/// own textures gave 2 of 2 for DXT1 and 0 of 6 for BC7, and 0 of 4 for plain
/// A8R8G8B8, so it is the codec that is narrow, not the DDS header.
///
/// Follows the Khronos BPTC specification. Field order inside a block, after the
/// mode bits, is fixed for every mode: partition, rotation, index selection,
/// colour endpoints, alpha endpoints, P-bits, primary indices, secondary indices.
/// </remarks>
internal static class Bc7Decoder
{
    public const int BlockBytes = 16;

    /// <summary>
    /// The mode of a block, or 8 for the reserved all-zero encoding. Exposed so a
    /// mismatch against a reference image can be attributed to a specific mode
    /// instead of guessed at.
    /// </summary>
    public static int ModeOf(ReadOnlySpan<byte> block)
    {
        var bits = new Bits(block);
        int mode = 0;
        while (mode < 8 && bits.Read(1) == 0)
            mode++;
        return mode;
    }

    /// <summary>Partition index of a block, or -1 when the mode has no partition.</summary>
    public static int PartitionOf(ReadOnlySpan<byte> block)
    {
        var bits = new Bits(block);
        int mode = 0;
        while (mode < 8 && bits.Read(1) == 0)
            mode++;
        if (mode >= 8)
            return -1;
        var m = Bc7Tables.Modes[mode];
        return m.PartitionBits > 0 ? (int)bits.Read(m.PartitionBits) : -1;
    }

    public static RgbaImage Decode(ReadOnlySpan<byte> data, int width, int height)
    {
        var img = new RgbaImage(width, height);
        int bw = BlockDecoders.BlocksWide(width);
        int bh = BlockDecoders.BlocksHigh(height);
        Span<byte> texels = stackalloc byte[16 * 4];

        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int at = (by * bw + bx) * BlockBytes;
                if (at + BlockBytes > data.Length)
                    return img;

                DecodeBlock(data.Slice(at, BlockBytes), texels);
                for (int t = 0; t < 16; t++)
                {
                    int px = bx * 4 + (t & 3);
                    int py = by * 4 + (t >> 2);
                    img.Set(px, py, texels[t * 4], texels[t * 4 + 1], texels[t * 4 + 2],
                        texels[t * 4 + 3]);
                }
            }
        return img;
    }

    /// <summary>Decodes one 16-byte block into 16 RGBA texels, x-major.</summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> output)
    {
        output.Clear();
        var bits = new Bits(block);

        // The mode is a run of zeros terminated by a one. All eight bits zero is
        // reserved and decodes to transparent black.
        int mode = 0;
        while (mode < 8 && bits.Read(1) == 0)
            mode++;
        if (mode >= 8)
            return;

        var m = Bc7Tables.Modes[mode];

        int partition = m.PartitionBits > 0 ? (int)bits.Read(m.PartitionBits) : 0;
        int rotation = m.RotationBits > 0 ? (int)bits.Read(m.RotationBits) : 0;
        int indexSelection = m.IndexSelectionBits > 0 ? (int)bits.Read(m.IndexSelectionBits) : 0;

        int endpointCount = 2 * m.Subsets;
        Span<int> ep = stackalloc int[6 * 4];

        // Colour first, ordered by channel then by endpoint; alpha follows the same way.
        for (int c = 0; c < 3; c++)
            for (int e = 0; e < endpointCount; e++)
                ep[e * 4 + c] = (int)bits.Read(m.ColourBits);
        if (m.AlphaBits > 0)
            for (int e = 0; e < endpointCount; e++)
                ep[e * 4 + 3] = (int)bits.Read(m.AlphaBits);

        int colourWidth = m.ColourBits;
        int alphaWidth = m.AlphaBits;

        if (m.EndpointPBits > 0)
        {
            // One extra low bit per endpoint.
            for (int e = 0; e < endpointCount; e++)
            {
                int p = (int)bits.Read(1);
                for (int c = 0; c < 3; c++)
                    ep[e * 4 + c] = (ep[e * 4 + c] << 1) | p;
                if (m.AlphaBits > 0)
                    ep[e * 4 + 3] = (ep[e * 4 + 3] << 1) | p;
            }
            colourWidth++;
            if (m.AlphaBits > 0)
                alphaWidth++;
        }
        else if (m.SharedPBits > 0)
        {
            // One bit per subset, shared by both of its endpoints. The first bit read
            // is the low one and belongs to subset 0.
            Span<int> shared = stackalloc int[3];
            for (int s = 0; s < m.Subsets; s++)
                shared[s] = (int)bits.Read(1);
            for (int e = 0; e < endpointCount; e++)
            {
                int p = shared[e / 2];
                for (int c = 0; c < 3; c++)
                    ep[e * 4 + c] = (ep[e * 4 + c] << 1) | p;
                if (m.AlphaBits > 0)
                    ep[e * 4 + 3] = (ep[e * 4 + 3] << 1) | p;
            }
            colourWidth++;
            if (m.AlphaBits > 0)
                alphaWidth++;
        }

        // Endpoint values sit in the high bits of a byte; the top bits are copied
        // down into whatever is left over.
        for (int e = 0; e < endpointCount; e++)
        {
            for (int c = 0; c < 3; c++)
                ep[e * 4 + c] = Unquantise(ep[e * 4 + c], colourWidth);
            ep[e * 4 + 3] = m.AlphaBits > 0 ? Unquantise(ep[e * 4 + 3], alphaWidth) : 255;
        }

        // Anchors: the one index per subset that is stored with its top bit dropped.
        Span<int> anchors = stackalloc int[3];
        anchors[0] = 0;
        if (m.Subsets == 2)
            anchors[1] = Bc7Tables.Anchor2[partition];
        else if (m.Subsets == 3)
        {
            anchors[1] = Bc7Tables.Anchor3Second[partition];
            anchors[2] = Bc7Tables.Anchor3Third[partition];
        }

        Span<int> primary = stackalloc int[16];
        for (int t = 0; t < 16; t++)
        {
            int subset = SubsetOf(m.Subsets, partition, t);
            int count = m.IndexBits - (anchors[subset] == t ? 1 : 0);
            primary[t] = (int)bits.Read(count);
        }

        Span<int> secondary = stackalloc int[16];
        if (m.IndexBits2 > 0)
        {
            for (int t = 0; t < 16; t++)
            {
                int subset = SubsetOf(m.Subsets, partition, t);
                int count = m.IndexBits2 - (anchors[subset] == t ? 1 : 0);
                secondary[t] = (int)bits.Read(count);
            }
        }

        bool hasSecondary = m.IndexBits2 > 0;
        bool hasSelection = m.IndexSelectionBits > 0;

        for (int t = 0; t < 16; t++)
        {
            int subset = SubsetOf(m.Subsets, partition, t);
            int e0 = subset * 2 * 4;
            int e1 = (subset * 2 + 1) * 4;

            // Colour takes the secondary index only when a selection bit is present
            // and set; alpha takes the secondary index in the opposite case.
            int colourIndex, colourBits, alphaIndex, alphaBits;
            if (!hasSecondary)
            {
                colourIndex = alphaIndex = primary[t];
                colourBits = alphaBits = m.IndexBits;
            }
            else if (hasSelection && indexSelection == 1)
            {
                colourIndex = secondary[t];
                colourBits = m.IndexBits2;
                alphaIndex = primary[t];
                alphaBits = m.IndexBits;
            }
            else
            {
                colourIndex = primary[t];
                colourBits = m.IndexBits;
                alphaIndex = secondary[t];
                alphaBits = m.IndexBits2;
            }

            byte[] colourWeights = Bc7Tables.WeightsFor(colourBits);
            byte[] alphaWeights = Bc7Tables.WeightsFor(alphaBits);
            int cw = colourWeights[Math.Clamp(colourIndex, 0, colourWeights.Length - 1)];
            int aw = alphaWeights[Math.Clamp(alphaIndex, 0, alphaWeights.Length - 1)];

            byte r = Interpolate(ep[e0 + 0], ep[e1 + 0], cw);
            byte g = Interpolate(ep[e0 + 1], ep[e1 + 1], cw);
            byte b = Interpolate(ep[e0 + 2], ep[e1 + 2], cw);
            byte a = Interpolate(ep[e0 + 3], ep[e1 + 3], aw);

            // Modes 4 and 5 may have moved a colour channel into alpha.
            switch (rotation)
            {
                case 1: (a, r) = (r, a); break;
                case 2: (a, g) = (g, a); break;
                case 3: (a, b) = (b, a); break;
            }

            output[t * 4 + 0] = r;
            output[t * 4 + 1] = g;
            output[t * 4 + 2] = b;
            output[t * 4 + 3] = a;
        }
    }

    private static int SubsetOf(int subsets, int partition, int texel) => subsets switch
    {
        1 => 0,
        2 => Bc7Tables.Partition2[partition * 16 + texel],
        _ => Bc7Tables.Partition3[partition * 16 + texel],
    };

    private static int Unquantise(int value, int width)
    {
        if (width >= 8)
            return value & 0xFF;
        int v = value << (8 - width);
        return (v | (v >> width)) & 0xFF;
    }

    private static byte Interpolate(int e0, int e1, int weight)
        => (byte)(((64 - weight) * e0 + weight * e1 + 32) >> 6);

    /// <summary>
    /// LSB-first bit reader over a 128-bit block, reading straight across the
    /// 64-bit halves.
    /// </summary>
    private ref struct Bits
    {
        private readonly ulong _low;
        private readonly ulong _high;
        private int _position;

        public Bits(ReadOnlySpan<byte> block)
        {
            _low = BinaryPrimitives.ReadUInt64LittleEndian(block[..8]);
            _high = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(8, 8));
            _position = 0;
        }

        public uint Read(int count)
        {
            uint result = 0;
            for (int i = 0; i < count; i++)
            {
                int bit = _position + i;
                if (bit >= 128)
                    break;
                ulong source = bit < 64 ? _low : _high;
                int shift = bit < 64 ? bit : bit - 64;
                result |= (uint)((source >> shift) & 1UL) << i;
            }
            _position += count;
            return result;
        }
    }
}
