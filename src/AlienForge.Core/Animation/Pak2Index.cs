using System.Buffers.Binary;
using System.Text;

namespace AlienForge.Core.Animation;

/// <summary>One record inside a PAK2 archive.</summary>
public sealed record Pak2Entry(int Index, string Name, long Offset, int Length)
{
    public override string ToString() => $"{Name} ({Length} B)";
}

/// <summary>
/// Index-only reader for PAK2 archives, with on-demand extraction.
/// </summary>
/// <remarks>
/// ANIMATION.PAK is 451 MB. CathodeLib's loader pulls a whole archive into memory,
/// which is fine for a level's model pack but not for this one, so the header and
/// name table are read up front and record bodies are seeked to as needed.
///
/// Layout, confirmed against the shipped file:
///   +0x00  "PAK2"
///   +0x04  int32  size of the name table
///   +0x08  int32  number of records
///   +0x0C  int32  alignment of every record's start (4)
///   +0x10         null-terminated names, one per record
///   then          int32 per record: the ABSOLUTE end offset of its data
///   then          record bodies, back to back
/// So record i spans ends[i-1]..ends[i], and record 0 starts where the table ends.
/// </remarks>
public sealed class Pak2Index
{
    private readonly string _path;

    private Pak2Index(string path, List<Pak2Entry> entries)
    {
        _path = path;
        Entries = entries;
    }

    public IReadOnlyList<Pak2Entry> Entries { get; }

    public string Path => _path;

    public static Pak2Index Open(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        Span<byte> magic = stackalloc byte[4];
        if (reader.Read(magic) != 4 || Encoding.ASCII.GetString(magic) != "PAK2")
            throw new InvalidDataException($"{path}: не PAK2.");

        int nameTableSize = reader.ReadInt32();
        int count = reader.ReadInt32();
        // +0x0C — выравнивание начала каждой записи (в UI.PAK и ANIMATION.PAK — 4):
        // ends[] хранит настоящий конец данных, а следующая запись начинается с
        // ближайшей кратной границы. Без этого файлы читались со сдвигом 1–3 байта
        // (JPEXS: "Invalid SWF file, wrong signature").
        int alignment = reader.ReadInt32();
        if (alignment <= 0 || alignment > 4096) alignment = 1;
        if (count < 0 || count > 5_000_000 || nameTableSize < 0)
            throw new InvalidDataException($"{path}: неправдоподобный заголовок PAK2.");

        stream.Position = 0x10;
        byte[] nameBlob = reader.ReadBytes(nameTableSize);
        var names = new List<string>(count);
        int at = 0;
        for (int i = 0; i < count; i++)
        {
            int start = at;
            while (at < nameBlob.Length && nameBlob[at] != 0)
                at++;
            names.Add(Encoding.Latin1.GetString(nameBlob, start, at - start));
            at++; // skip the terminator
            if (at > nameBlob.Length)
                break;
        }

        long endsAt = 0x10 + (long)nameTableSize;
        stream.Position = endsAt;
        byte[] endBlob = reader.ReadBytes(count * 4);
        long dataBase = endsAt + count * 4L;

        var entries = new List<Pak2Entry>(count);
        long previous = dataBase;
        for (int i = 0; i < count && i < names.Count; i++)
        {
            if ((i + 1) * 4 > endBlob.Length)
                break;
            long end = BinaryPrimitives.ReadUInt32LittleEndian(endBlob.AsSpan(i * 4, 4));
            long start = (previous + alignment - 1) / alignment * alignment;
            int length = (int)Math.Max(0, end - start);
            entries.Add(new Pak2Entry(i, names[i], start, length));
            previous = end;
        }

        return new Pak2Index(path, entries);
    }

    /// <summary>Pulls one record's bytes off disk.</summary>
    public byte[] Read(Pak2Entry entry)
    {
        using var stream = File.OpenRead(_path);
        stream.Position = entry.Offset;
        var buffer = new byte[entry.Length];
        int read = 0;
        while (read < buffer.Length)
        {
            int got = stream.Read(buffer, read, buffer.Length - read);
            if (got <= 0)
                break;
            read += got;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    public Pak2Entry? Find(string fragment)
        => Entries.FirstOrDefault(e =>
            e.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<Pak2Entry> FindAll(string fragment)
        => Entries.Where(e => e.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
