using CATHODE;

namespace AlienForge.Core.Mesh;

/// <summary>
/// Where every vertex attribute sits inside a submesh's raw blob.
/// </summary>
/// <remarks>
/// The one thing that is not obvious from the declared format: each vertex stream
/// is padded to a 16 byte boundary, and so is the index block that follows them.
/// Without that padding every stream after the first is read at the wrong offset,
/// which shows up as vertices flung across the model rather than as an obvious
/// failure.
/// </remarks>
public sealed class VertexLayout
{
    public const int StreamAlignment = 16;

    private readonly int[] _strides;
    private readonly int[] _bases;
    private readonly Dictionary<(Models.VertexFormat.Usage usage, int index), Slot> _slots;

    private VertexLayout(int[] strides, int[] bases, int indexBase, int totalSize,
        Dictionary<(Models.VertexFormat.Usage, int), Slot> slots)
    {
        _strides = strides;
        _bases = bases;
        IndexBase = indexBase;
        TotalSize = totalSize;
        _slots = slots;
    }

    /// <summary>Byte offset of the 16-bit index block.</summary>
    public int IndexBase { get; }

    /// <summary>Expected total blob size: vertex streams plus indices.</summary>
    public int TotalSize { get; }

    public int StreamCount => _strides.Length;

    public int StrideOf(int stream) => _strides[stream];

    /// <summary>One attribute's stream, in-vertex offset and storage type.</summary>
    public readonly record struct Slot(int Stream, int Offset, Models.VertexFormat.Type Type);

    public bool TryGet(Models.VertexFormat.Usage usage, int index, out Slot slot)
        => _slots.TryGetValue((usage, index), out slot);

    public bool Has(Models.VertexFormat.Usage usage, int index = 0)
        => _slots.ContainsKey((usage, index));

    public IEnumerable<(Models.VertexFormat.Usage usage, int index, Slot slot)> Slots
        => _slots.Select(kv => (kv.Key.usage, kv.Key.index, kv.Value));

    /// <summary>Byte offset of vertex <paramref name="vertex"/> for that slot.</summary>
    public int OffsetOf(in Slot slot, int vertex)
        => _bases[slot.Stream] + vertex * _strides[slot.Stream] + slot.Offset;

    public static int Align(int value, int alignment = StreamAlignment)
        => (value + alignment - 1) / alignment * alignment;

    /// <summary>
    /// Plans the layout for a submesh. Returns null when the format is missing.
    /// </summary>
    public static VertexLayout? Plan(Models.VertexFormat? format, int vertexCount, int indexCount)
    {
        if (format?.Attributes is null || format.Attributes.Count == 0)
            return null;

        int streams = format.Attributes.Count;
        var strides = new int[streams];
        var bases = new int[streams];
        var slots = new Dictionary<(Models.VertexFormat.Usage, int), Slot>();

        for (int s = 0; s < streams; s++)
        {
            int offset = 0;
            foreach (var attr in format.Attributes[s])
            {
                int size = SizeOf(attr.Type);
                if (size > 0)
                {
                    // First declaration wins: a few formats repeat a usage across
                    // streams, and the earlier one is the populated copy.
                    var key = (attr.Usage, attr.Index);
                    if (!slots.ContainsKey(key))
                        slots[key] = new Slot(s, offset, attr.Type);
                }
                offset += size;
            }
            strides[s] = offset;
        }

        int running = 0;
        for (int s = 0; s < streams; s++)
        {
            bases[s] = running;
            if (strides[s] > 0)
                running = Align(running + strides[s] * vertexCount);
        }

        int indexBase = Align(running);
        int total = indexBase + indexCount * sizeof(ushort);
        return new VertexLayout(strides, bases, indexBase, total, slots);
    }

    /// <summary>
    /// Storage size in bytes. Anything unrecognised reports 0, which keeps the
    /// running offset honest only if it truly occupies nothing, so unknown types
    /// are reported by <see cref="IsKnown"/> instead of being silently skipped.
    /// </summary>
    public static int SizeOf(Models.VertexFormat.Type type) => type switch
    {
        Models.VertexFormat.Type.FP32_1 => 4,
        Models.VertexFormat.Type.FP32_2 => 8,
        Models.VertexFormat.Type.FP32_3 => 12,
        Models.VertexFormat.Type.FP32_4 => 16,
        Models.VertexFormat.Type.Color => 4,
        Models.VertexFormat.Type.U8_4 => 4,
        Models.VertexFormat.Type.U8_4N => 4,
        Models.VertexFormat.Type.S8_4N => 4,
        Models.VertexFormat.Type.S16_2 => 4,
        Models.VertexFormat.Type.S16_2N => 4,
        Models.VertexFormat.Type.S16_4 => 8,
        Models.VertexFormat.Type.S16_4N => 8,
        Models.VertexFormat.Type.U16_2N => 4,
        Models.VertexFormat.Type.U16_4N => 8,
        Models.VertexFormat.Type.UDec3 => 4,
        Models.VertexFormat.Type.Dec3N => 4,
        Models.VertexFormat.Type.FP16_2 => 4,
        Models.VertexFormat.Type.FP16_4 => 8,
        _ => 0,
    };

    public static bool IsKnown(Models.VertexFormat.Type type)
        => type is not (Models.VertexFormat.Type.Unknown or Models.VertexFormat.Type.Unused)
           && SizeOf(type) > 0;
}
