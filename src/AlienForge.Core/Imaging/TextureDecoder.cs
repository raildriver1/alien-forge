using System.Buffers.Binary;
using CATHODE;
using Fmt = CATHODE.Textures.TextureFormat;

namespace AlienForge.Core.Imaging;

/// <summary>Which of a TEX4's two payloads a mip came from.</summary>
public enum TextureSource
{
    Streamed,
    Persistent,
}

/// <summary>One mip level located inside a payload.</summary>
public readonly record struct MipInfo(int Level, int Width, int Height, int Offset, int Size);

/// <summary>Result of decoding, including what had to be skipped.</summary>
public sealed class TextureDecodeResult
{
    public RgbaImage? Image { get; init; }
    public string? Error { get; init; }
    public TextureSource Source { get; init; }
    public MipInfo Mip { get; init; }
    public Fmt Format { get; init; }

    public bool Ok => Image is not null;
}

/// <summary>
/// Turns a CATHODE TEX4 into pixels.
/// </summary>
/// <remarks>
/// Payloads carry no header: just the mip chain, largest first, tightly packed.
/// That was confirmed against the shipped sizes, for example the Alien's 2048x2048
/// DXN normal map reports 12 mips and 5461 KB, which is exactly the sum of a full
/// BC5 chain at 16 bytes per 4x4 block down to 1x1.
/// </remarks>
public static class TextureDecoder
{
    /// <summary>
    /// Picks the payload to read. Streamed holds full resolution and is preferred;
    /// persistent is the always-loaded smaller copy and not every texture has both.
    /// </summary>
    public static (Textures.TEX4.Texture part, TextureSource source)? SelectPart(
        Textures.TEX4 tex, bool preferStreamed = true)
    {
        var streamed = tex.TextureStreamed;
        var persistent = tex.TexturePersistent;
        bool streamedOk = streamed?.Content is { Length: > 0 } && streamed.Width > 0;
        bool persistentOk = persistent?.Content is { Length: > 0 } && persistent.Width > 0;

        if (preferStreamed && streamedOk)
            return (streamed!, TextureSource.Streamed);
        if (persistentOk)
            return (persistent!, TextureSource.Persistent);
        if (streamedOk)
            return (streamed!, TextureSource.Streamed);
        return null;
    }

    /// <summary>
    /// Walks the mip chain. Stops early when the payload runs out, so a truncated
    /// texture reports the mips it really has instead of trusting the header.
    /// </summary>
    public static List<MipInfo> EnumerateMips(Textures.TEX4.Texture part, TextureLayout layout)
    {
        var mips = new List<MipInfo>();
        int available = part.Content?.Length ?? 0;
        int width = part.Width;
        int height = part.Height;
        int declared = Math.Max(1, (int)part.MipLevels);
        int offset = 0;

        for (int level = 0; level < declared; level++)
        {
            int size = layout.MipSize(width, height);
            if (size <= 0 || offset + size > available)
                break;
            mips.Add(new MipInfo(level, width, height, offset, size));
            offset += size;
            if (width == 1 && height == 1)
                break;
            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
        }
        return mips;
    }

    /// <summary>
    /// For a BC7 texture, reports which block modes and partitions appear in blocks
    /// whose decoded pixels differ from a reference image. A decoder bug shows up as
    /// a single mode dominating; a spread across modes points at the reference.
    /// </summary>
    public static string DescribeBc7Mismatch(Textures.TEX4 tex, RgbaImage mine,
        RgbaImage reference, int mipLevel = 0, bool preferStreamed = true)
    {
        if (tex.Format != Fmt.BC7)
            return "не BC7";
        if (mine.Width != reference.Width || mine.Height != reference.Height)
            return "размеры не совпадают";

        var layout = TextureLayout.For(tex.Format);
        var picked = SelectPart(tex, preferStreamed);
        if (picked is null)
            return "нет данных";
        var (part, _) = picked.Value;
        var mips = EnumerateMips(part, layout);
        if (mips.Count == 0)
            return "цепочка мипов пуста";
        MipInfo mip = mips[Math.Clamp(mipLevel, 0, mips.Count - 1)];

        int bw = BlockDecoders.BlocksWide(mip.Width);
        int bh = BlockDecoders.BlocksHigh(mip.Height);
        var totalByMode = new int[9];
        var badByMode = new int[9];
        var badPartitions = new SortedSet<int>();
        int badBlocks = 0;

        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int at = mip.Offset + (by * bw + bx) * Bc7Decoder.BlockBytes;
                if (at + Bc7Decoder.BlockBytes > part.Content!.Length)
                    continue;
                var block = new ReadOnlySpan<byte>(part.Content, at, Bc7Decoder.BlockBytes);
                int mode = Bc7Decoder.ModeOf(block);
                totalByMode[Math.Clamp(mode, 0, 8)]++;

                bool differs = false;
                for (int py = 0; py < 4 && !differs; py++)
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= mine.Width || y >= mine.Height)
                            continue;
                        int i = (y * mine.Width + x) * 4;
                        if (mine.Pixels[i] != reference.Pixels[i]
                            || mine.Pixels[i + 1] != reference.Pixels[i + 1]
                            || mine.Pixels[i + 2] != reference.Pixels[i + 2])
                        {
                            differs = true;
                            break;
                        }
                    }
                if (differs)
                {
                    badBlocks++;
                    badByMode[Math.Clamp(mode, 0, 8)]++;
                    int partition = Bc7Decoder.PartitionOf(block);
                    if (partition >= 0)
                        badPartitions.Add(partition);
                }
            }

        var parts = new List<string>();
        for (int m = 0; m <= 8; m++)
            if (totalByMode[m] > 0)
                parts.Add($"реж{m}: {badByMode[m]}/{totalByMode[m]}");
        string partitionNote = badPartitions.Count == 0
            ? ""
            : $"; разбиения: {string.Join(",", badPartitions.Take(20))}" +
              (badPartitions.Count > 20 ? "..." : "");
        return $"блоков с отличиями {badBlocks} из {bw * bh} [{string.Join("  ", parts)}]{partitionNote}";
    }

    /// <summary>
    /// Builds the DDS this tool would hand to the platform decoder, and reports
    /// whether that decoder accepts it. Used to tell a bad header apart from a
    /// format the platform simply does not implement.
    /// </summary>
    public static (bool ok, string? error, int width, int height, byte[]? dds) ProbeWic(
        Textures.TEX4 tex, int mipLevel = 0, bool preferStreamed = true)
    {
        var layout = TextureLayout.For(tex.Format);
        var picked = SelectPart(tex, preferStreamed);
        if (picked is null)
            return (false, "нет данных", 0, 0, null);

        var (part, _) = picked.Value;
        var mips = EnumerateMips(part, layout);
        if (mips.Count == 0)
            return (false, "цепочка мипов пуста", 0, 0, null);

        MipInfo mip = mips[Math.Clamp(mipLevel, 0, mips.Count - 1)];
        var slice = new ReadOnlySpan<byte>(part.Content!, mip.Offset, mip.Size);
        byte[] dds = DdsWriter.Wrap(layout, slice, mip.Width, mip.Height, mipCount: 1);

        var image = WicDecoder.TryDecode(dds, out string? error);
        return (image is not null, error, mip.Width, mip.Height, dds);
    }

    public static TextureDecodeResult Decode(Textures.TEX4 tex, int mipLevel = 0,
        bool preferStreamed = true)
    {
        var layout = TextureLayout.For(tex.Format);
        if (!layout.CanDecode)
            return new TextureDecodeResult { Error = layout.UnsupportedReason, Format = tex.Format };

        var picked = SelectPart(tex, preferStreamed);
        if (picked is null)
            return new TextureDecodeResult
            {
                Error = "У текстуры нет данных ни в streamed, ни в persistent.",
                Format = tex.Format,
            };

        var (part, source) = picked.Value;
        var mips = EnumerateMips(part, layout);
        if (mips.Count == 0)
            return new TextureDecodeResult
            {
                Error = $"Не удалось разобрать цепочку мипов: заявлено {part.MipLevels} уровней, " +
                        $"{part.Width}x{part.Height}, данных {part.Content?.Length ?? 0} байт.",
                Format = tex.Format,
                Source = source,
            };

        MipInfo mip = mips[Math.Clamp(mipLevel, 0, mips.Count - 1)];
        var slice = new ReadOnlySpan<byte>(part.Content!, mip.Offset, mip.Size);

        RgbaImage? image;
        string? error = null;
        if (layout.IsBlockCompressed)
            image = DecodeBlocks(layout, slice, mip, out error);
        else
            image = DecodeRaw(layout, slice, mip, out error);

        if (image is not null && layout.IsTwoChannelNormal)
            image.ReconstructNormalZ();

        return new TextureDecodeResult
        {
            Image = image,
            Error = error,
            Source = source,
            Mip = mip,
            Format = tex.Format,
        };
    }

    // ---------------------------------------------------------------- blocks
    private static RgbaImage? DecodeBlocks(TextureLayout layout, ReadOnlySpan<byte> data,
        MipInfo mip, out string? error)
    {
        error = null;
        switch (layout.Format)
        {
            case Fmt.DXT1:
                return BlockDecoders.DecodeBc1(data, mip.Width, mip.Height);
            case Fmt.DXT3:
                return BlockDecoders.DecodeBc2(data, mip.Width, mip.Height);
            case Fmt.DXT5:
                return BlockDecoders.DecodeBc3(data, mip.Width, mip.Height);
            case Fmt.DXN:
                return BlockDecoders.DecodeBc5(data, mip.Width, mip.Height);

            case Fmt.BC7:
                return Bc7Decoder.Decode(data, mip.Width, mip.Height);

            default:
                error = $"Нет декодера для {layout.Format}.";
                return null;
        }
    }

    // ------------------------------------------------------------------- raw
    private static RgbaImage? DecodeRaw(TextureLayout layout, ReadOnlySpan<byte> data,
        MipInfo mip, out string? error)
    {
        error = null;
        var img = new RgbaImage(mip.Width, mip.Height);
        int bpp = layout.BytesPerPixel;
        if (bpp <= 0)
        {
            error = $"Неизвестный размер пикселя для {layout.Format}.";
            return null;
        }

        for (int y = 0; y < mip.Height; y++)
            for (int x = 0; x < mip.Width; x++)
            {
                int at = (y * mip.Width + x) * bpp;
                if (at + bpp > data.Length)
                    return img;

                switch (layout.Format)
                {
                    // D3D9 order: the bytes in memory are B, G, R, A.
                    case Fmt.A8R8G8B8:
                        img.Set(x, y, data[at + 2], data[at + 1], data[at + 0], data[at + 3]);
                        break;
                    case Fmt.X8R8G8B8:
                        img.Set(x, y, data[at + 2], data[at + 1], data[at + 0], 255);
                        break;

                    case Fmt.A8:
                        img.Set(x, y, 255, 255, 255, data[at]);
                        break;
                    case Fmt.L8:
                        img.Set(x, y, data[at], data[at], data[at], 255);
                        break;

                    case Fmt.A4R4G4B4:
                    {
                        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at, 2));
                        byte a = Expand4((v >> 12) & 0xF);
                        byte r = Expand4((v >> 8) & 0xF);
                        byte g = Expand4((v >> 4) & 0xF);
                        byte b = Expand4(v & 0xF);
                        img.Set(x, y, r, g, b, a);
                        break;
                    }

                    case Fmt.R16F:
                    {
                        float f = (float)BitConverter.UInt16BitsToHalf(
                            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at, 2)));
                        byte v = ToByte(f);
                        img.Set(x, y, v, v, v, 255);
                        break;
                    }

                    case Fmt.A16R16G16B16:
                    {
                        byte r = (byte)(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at + 0, 2)) >> 8);
                        byte g = (byte)(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at + 2, 2)) >> 8);
                        byte b = (byte)(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at + 4, 2)) >> 8);
                        byte a = (byte)(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at + 6, 2)) >> 8);
                        img.Set(x, y, r, g, b, a);
                        break;
                    }

                    case Fmt.A32R32G32B32F:
                    {
                        float r = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(at + 0, 4));
                        float g = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(at + 4, 4));
                        float b = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(at + 8, 4));
                        float a = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(at + 12, 4));
                        img.Set(x, y, ToByte(r), ToByte(g), ToByte(b), ToByte(a));
                        break;
                    }

                    default:
                        error = $"Нет декодера для {layout.Format}.";
                        return null;
                }
            }
        return img;
    }

    private static byte Expand4(int v) => (byte)(v * 17);

    private static byte ToByte(float v)
        => (byte)Math.Clamp((int)MathF.Round(v * 255.0f), 0, 255);
}
