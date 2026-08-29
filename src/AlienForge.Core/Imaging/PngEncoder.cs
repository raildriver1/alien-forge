using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace AlienForge.Core.Imaging;

/// <summary>
/// Minimal PNG writer: 8-bit RGBA, filter 0, zlib-deflated.
/// </summary>
/// <remarks>
/// Written by hand rather than through WIC or System.Drawing so that extraction
/// output is byte-for-byte reproducible and does not depend on codec behaviour.
/// </remarks>
public static class PngEncoder
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static void Save(RgbaImage image, string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using var fs = File.Create(path);
        Write(image, fs);
    }

    public static byte[] Encode(RgbaImage image)
    {
        using var ms = new MemoryStream();
        Write(image, ms);
        return ms.ToArray();
    }

    public static void Write(RgbaImage image, Stream output)
    {
        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], image.Height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type: truecolour with alpha
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filtering
        ihdr[12] = 0; // no interlace
        WriteChunk(output, "IHDR", ihdr);

        WriteChunk(output, "IDAT", Compress(image));
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static byte[] Compress(RgbaImage image)
    {
        // One filter byte per scanline, then the raw RGBA row.
        var raw = new byte[(image.Stride + 1) * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            int dst = y * (image.Stride + 1);
            raw[dst] = 0; // filter: None
            Buffer.BlockCopy(image.Pixels, y * image.Stride, raw, dst + 1, image.Stride);
        }

        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        Span<byte> tag = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
            tag[i] = (byte)type[i];
        output.Write(tag);
        output.Write(data);

        uint crc = Crc32.Compute(tag, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = Build();

        private static uint[] Build()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte x in a)
                c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
            foreach (byte x in b)
                c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
