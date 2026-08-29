namespace AlienForge.Core.Imaging;

/// <summary>
/// Constant tables for BC7 (BPTC) decoding.
/// </summary>
/// <remarks>
/// Transcribed from the Khronos BPTC specification, tables M, P2, P3, A2, A3a and
/// A3b:
/// https://registry.khronos.org/OpenGL/extensions/ARB/ARB_texture_compression_bptc.txt
///
/// These are format constants, not an implementation. They are kept in one place
/// and in the spec's own row order so they can be checked against it line by line.
/// </remarks>
internal static class Bc7Tables
{
    /// <summary>Per-mode field widths. Index is the mode number, 0..7.</summary>
    internal readonly record struct Mode(
        int Subsets,
        int PartitionBits,
        int RotationBits,
        int IndexSelectionBits,
        int ColourBits,
        int AlphaBits,
        int EndpointPBits,
        int SharedPBits,
        int IndexBits,
        int IndexBits2);

    // Table.M
    public static readonly Mode[] Modes =
    {
        //          NS PB RB ISB CB AB EPB SPB IB IB2
        new Mode(3,  4, 0, 0,  4, 0, 1,  0,  3, 0), // 0
        new Mode(2,  6, 0, 0,  6, 0, 0,  1,  3, 0), // 1
        new Mode(3,  6, 0, 0,  5, 0, 0,  0,  2, 0), // 2
        new Mode(2,  6, 0, 0,  7, 0, 1,  0,  2, 0), // 3
        new Mode(1,  0, 2, 1,  5, 6, 0,  0,  2, 3), // 4
        new Mode(1,  0, 2, 0,  7, 8, 0,  0,  2, 2), // 5
        new Mode(1,  0, 0, 0,  7, 7, 1,  0,  4, 0), // 6
        new Mode(2,  6, 0, 0,  5, 5, 1,  0,  2, 0), // 7
    };

    /// <summary>Interpolation factors, always on a 0..64 scale.</summary>
    public static readonly byte[] Weights2 = { 0, 21, 43, 64 };
    public static readonly byte[] Weights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
    public static readonly byte[] Weights4 =
        { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

    public static byte[] WeightsFor(int bits) => bits switch
    {
        2 => Weights2,
        3 => Weights3,
        4 => Weights4,
        _ => Weights2,
    };

    // Table.P2 — one row per partition, sixteen texels in x-major order.
    private static readonly string[] Partition2Rows =
    {
        "0011001100110011", "0001000100010001", "0111011101110111", "0001001100110111",
        "0000000100010011", "0011011101111111", "0001001101111111", "0000000100110111",
        "0000000000010011", "0011011111111111", "0000000101111111", "0000000000010111",
        "0001011111111111", "0000000011111111", "0000111111111111", "0000000000001111",
        "0000100011101111", "0111000100000000", "0000000010001110", "0111001100010000",
        "0011000100000000", "0000100011001110", "0000000010001100", "0111001100110001",
        "0011000100010000", "0000100010001100", "0110011001100110", "0011011001101100",
        "0001011111101000", "0000111111110000", "0111000110001110", "0011100110011100",
        "0101010101010101", "0000111100001111", "0101101001011010", "0011001111001100",
        "0011110000111100", "0101010110101010", "0110100101101001", "0101101010100101",
        "0111001111001110", "0001001111001000", "0011001001001100", "0011101111011100",
        "0110100110010110", "0011110011000011", "0110011010011001", "0000011001100000",
        "0100111001000000", "0010011100100000", "0000001001110010", "0000010011100100",
        "0110110010010011", "0011011011001001", "0110001110011100", "0011100111000110",
        "0110110011001001", "0110001100111001", "0111111010000001", "0001100011100111",
        "0000111100110011", "0011001111110000", "0010001011101110", "0100010001110111",
    };

    // Table.P3
    private static readonly string[] Partition3Rows =
    {
        "0011001102212222", "0001001122112221", "0000200122112211", "0222002200110111",
        "0000000011221122", "0011001100220022", "0022002211111111", "0011001122112211",
        "0000000011112222", "0000111111112222", "0000111122222222", "0012001200120012",
        "0112011201120112", "0122012201220122", "0011011211221222", "0011200122002220",
        "0001001101121122", "0111001120012200", "0000112211221122", "0022002200221111",
        "0111011102220222", "0001000122212221", "0000001101220122", "0000110022102210",
        "0122012200110000", "0012001211222222", "0110122112210110", "0000011012211221",
        "0022110211020022", "0110011020022222", "0011012201220011", "0000200022112221",
        "0000000211221222", "0222002200120011", "0011001200220222", "0120012001200120",
        "0000111122220000", "0120120120120120", "0120201212010120", "0011220011220011",
        "0011112222000011", "0101010122222222", "0000000021212121", "0022112200221122",
        "0022001100220011", "0220122102201221", "0101222222220101", "0000212121212121",
        "0101010101012222", "0222011102220111", "0002111200021112", "0000211221122112",
        "0222011101110222", "0002111211120002", "0110011001102222", "0000000021122112",
        "0110011022222222", "0022001100110022", "0022112211220022", "0000000000002112",
        "0002000100020001", "0222122202221222", "0101222222222222", "0111201122012220",
    };

    /// <summary>Table.A2: anchor for the second subset of a two-subset partition.</summary>
    public static readonly byte[] Anchor2 =
    {
        15, 15, 15, 15, 15, 15, 15, 15,
        15, 15, 15, 15, 15, 15, 15, 15,
        15,  2,  8,  2,  2,  8,  8, 15,
         2,  8,  2,  2,  8,  8,  2,  2,
        15, 15,  6,  8,  2,  8, 15, 15,
         2,  8,  2,  2,  2, 15, 15,  6,
         6,  2,  6,  8, 15, 15,  2,  2,
        15, 15, 15, 15, 15,  2,  2, 15,
    };

    /// <summary>Table.A3a: anchor for the second subset of a three-subset partition.</summary>
    public static readonly byte[] Anchor3Second =
    {
         3,  3, 15, 15,  8,  3, 15, 15,
         8,  8,  6,  6,  6,  5,  3,  3,
         3,  3,  8, 15,  3,  3,  6, 10,
         5,  8,  8,  6,  8,  5, 15, 15,
         8, 15,  3,  5,  6, 10,  8, 15,
        15,  3, 15,  5, 15, 15, 15, 15,
         3, 15,  5,  5,  5,  8,  5, 10,
         5, 10,  8, 13, 15, 12,  3,  3,
    };

    /// <summary>Table.A3b: anchor for the third subset of a three-subset partition.</summary>
    public static readonly byte[] Anchor3Third =
    {
        15,  8,  8,  3, 15, 15,  3,  8,
        15, 15, 15, 15, 15, 15, 15,  8,
        15,  8, 15,  3, 15,  8, 15,  8,
         3, 15,  6, 10, 15, 15, 10,  8,
        15,  3, 15, 10, 10,  8,  9, 10,
         6, 15,  8, 15,  3,  6,  6,  8,
        15,  3, 15, 15, 15, 15, 15, 15,
        15, 15, 15, 15,  3, 15, 15,  8,
    };

    /// <summary>Flattened Table.P2, indexed as [partition * 16 + texel].</summary>
    public static readonly byte[] Partition2 = Flatten(Partition2Rows, 2);

    /// <summary>Flattened Table.P3, indexed as [partition * 16 + texel].</summary>
    public static readonly byte[] Partition3 = Flatten(Partition3Rows, 3);

    /// <summary>
    /// Expands the digit rows and verifies the shape while doing so: 64 rows of 16
    /// texels, every subset index inside range. A transcription slip would show up
    /// here rather than as a few quietly wrong blocks in an output image.
    /// </summary>
    private static byte[] Flatten(string[] rows, int subsets)
    {
        if (rows.Length != 64)
            throw new InvalidOperationException(
                $"Таблица разбиений BC7 должна содержать 64 строки, найдено {rows.Length}.");

        var flat = new byte[64 * 16];
        for (int p = 0; p < 64; p++)
        {
            string row = rows[p];
            if (row.Length != 16)
                throw new InvalidOperationException(
                    $"Строка {p} таблицы разбиений BC7 имеет длину {row.Length}, ожидалось 16.");
            for (int t = 0; t < 16; t++)
            {
                int value = row[t] - '0';
                if (value < 0 || value >= subsets)
                    throw new InvalidOperationException(
                        $"Строка {p}, элемент {t}: индекс подмножества {value} вне диапазона " +
                        $"0..{subsets - 1}.");
                flat[p * 16 + t] = (byte)value;
            }
        }
        return flat;
    }
}
