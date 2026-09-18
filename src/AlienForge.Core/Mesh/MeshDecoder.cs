using System.Buffers.Binary;
using System.Numerics;
using CATHODE;
using Usage = CATHODE.Models.VertexFormat.Usage;
using VType = CATHODE.Models.VertexFormat.Type;

namespace AlienForge.Core.Mesh;

/// <summary>
/// Turns a CATHODE submesh blob into plain vertex arrays.
/// </summary>
/// <remarks>
/// The scale factors here are not guesses. They were pinned down by decoding the
/// Alien and checking the result against facts the file itself states: positions
/// land exactly inside the stored bounding box, decoded normals come out unit
/// length for every vertex, UVs land inside 0..1, and blend weights sum to 255.
/// </remarks>
public static class MeshDecoder
{
    /// <summary>Fixed-point divisor for TexCoord stored as S16_2 (11 fractional bits).</summary>
    private const float TexCoordFixedPoint = 2048.0f;

    /// <summary>
    /// Decodes one submesh, or returns null when there is nothing usable in it.
    /// </summary>
    public static DecodedMesh? Decode(Models.CS2 model, int componentIndex, int lodIndex,
        int submeshIndex)
    {
        var lod = model.Components[componentIndex].LODs[lodIndex];
        var sm = lod.Submeshes[submeshIndex];

        var layout = VertexLayout.Plan(sm.VertexFormatFull, sm.VertexCount, sm.IndexCount);
        if (layout is null || sm.VertexCount == 0)
            return null;

        byte[] data = sm.Data ?? Array.Empty<byte>();
        var warnings = new List<string>();

        if (data.Length < layout.TotalSize)
        {
            warnings.Add($"блоб короче ожидаемого: {data.Length} < {layout.TotalSize} байт");
            if (!layout.TryGet(Usage.Position, 0, out _))
                return null;
        }

        foreach (var (usage, index, slot) in layout.Slots)
            if (!VertexLayout.IsKnown(slot.Type))
                warnings.Add($"неизвестный тип атрибута {usage}{index}: {slot.Type}");

        int vc = sm.VertexCount;
        float posScale = sm.VertexScale <= 0 ? 1.0f : sm.VertexScale;

        Vector3[] positions = ReadPositions(data, layout, vc, posScale, warnings);
        Vector3[]? normals = ReadDirections(data, layout, vc, Usage.Normal);
        Vector4[]? tangents = ReadTangents(data, layout, vc);
        Vector2[]? uv0 = ReadTexCoord(data, layout, vc, 0);
        Vector2[]? uv1 = ReadTexCoord(data, layout, vc, 1);
        Vector4[]? colors = ReadColors(data, layout, vc, 0);
        Vector4[]? colors1 = ReadColors(data, layout, vc, 1);
        ReadSkin(data, layout, vc, sm.Bones, warnings, out ushort[]? joints, out Vector4[]? weights);
        int[] indices = ReadIndices(data, layout, sm.IndexCount, vc, warnings);

        var mesh = new DecodedMesh
        {
            Name = BuildName(model, lod, componentIndex, lodIndex, submeshIndex),
            MaterialName = sm.Material?.Name,
            ComponentIndex = componentIndex,
            LodIndex = lodIndex,
            SubmeshIndex = submeshIndex,
            LodName = lod.Name ?? string.Empty,
            Positions = positions,
            Normals = normals,
            Tangents = tangents,
            Uv0 = uv0,
            Uv1 = uv1,
            Colors = colors,
            Colors1 = colors1,
            Joints = joints,
            Weights = weights,
            Indices = indices,
        };
        mesh.Warnings.AddRange(warnings);
        return mesh;
    }

    /// <summary>Every submesh of every component and LOD.</summary>
    public static List<DecodedMesh> DecodeAll(Models.CS2 model)
    {
        var result = new List<DecodedMesh>();
        for (int ci = 0; ci < model.Components.Count; ci++)
            for (int li = 0; li < model.Components[ci].LODs.Count; li++)
                for (int si = 0; si < model.Components[ci].LODs[li].Submeshes.Count; si++)
                {
                    var decoded = Decode(model, ci, li, si);
                    if (decoded is not null)
                        result.Add(decoded);
                }
        return result;
    }

    private static string BuildName(Models.CS2 model, Models.CS2.Component.LOD lod,
        int ci, int li, int si)
    {
        string lodName = string.IsNullOrEmpty(lod.Name) ? $"lod{li}" : lod.Name;
        return $"{lodName}_c{ci}l{li}s{si}";
    }

    // ------------------------------------------------------------- attributes
    private static Vector3[] ReadPositions(byte[] data, VertexLayout layout, int vc,
        float scale, List<string> warnings)
    {
        var result = new Vector3[vc];
        if (!layout.TryGet(Usage.Position, 0, out var slot))
        {
            warnings.Add("нет атрибута Position");
            return result;
        }

        for (int i = 0; i < vc; i++)
        {
            int at = layout.OffsetOf(slot, i);
            Vector4 raw = ReadRaw(data, at, slot.Type);
            result[i] = slot.Type switch
            {
                // normalised shorts carrying the model's own scale factor
                VType.S16_4N or VType.S16_2N => new Vector3(raw.X, raw.Y, raw.Z) * scale,
                // raw shorts: same fixed point, just not flagged normalised
                VType.S16_4 or VType.S16_2 => new Vector3(raw.X, raw.Y, raw.Z) / 32767.0f * scale,
                // floats are already in model units
                _ => new Vector3(raw.X, raw.Y, raw.Z),
            };
        }
        return result;
    }

    private static Vector3[]? ReadDirections(byte[] data, VertexLayout layout, int vc, Usage usage)
    {
        if (!layout.TryGet(usage, 0, out var slot))
            return null;

        var result = new Vector3[vc];
        for (int i = 0; i < vc; i++)
        {
            Vector4 raw = ReadRaw(data, layout.OffsetOf(slot, i), slot.Type);
            var v = new Vector3(raw.X, raw.Y, raw.Z);
            float len = v.Length();
            result[i] = len > 1e-6f ? v / len : new Vector3(0, 0, 1);
        }
        return result;
    }

    private static Vector4[]? ReadTangents(byte[] data, VertexLayout layout, int vc)
    {
        if (!layout.TryGet(Usage.Tangent, 0, out var slot))
            return null;

        // glTF wants a handedness sign in W. Derive it from the binormal when the
        // format carries one, otherwise leave it at +1.
        Vector3[]? normals = ReadDirections(data, layout, vc, Usage.Normal);
        Vector3[]? binormals = ReadDirections(data, layout, vc, Usage.Binormal);

        var result = new Vector4[vc];
        for (int i = 0; i < vc; i++)
        {
            Vector4 raw = ReadRaw(data, layout.OffsetOf(slot, i), slot.Type);
            var t = new Vector3(raw.X, raw.Y, raw.Z);
            float len = t.Length();
            t = len > 1e-6f ? t / len : new Vector3(1, 0, 0);

            float w = 1.0f;
            if (normals is not null && binormals is not null)
                w = Vector3.Dot(Vector3.Cross(normals[i], t), binormals[i]) < 0.0f ? -1.0f : 1.0f;
            result[i] = new Vector4(t, w);
        }
        return result;
    }

    /// <summary>Только цвет вершин сабмеша (для экспорта уровня, где геометрию читает CathodeLib).</summary>
    public static Vector4[]? ReadVertexColors(Models.CS2.Component.LOD.Submesh sm, int index = 0)
    {
        var layout = VertexLayout.Plan(sm.VertexFormatFull, sm.VertexCount, sm.IndexCount);
        if (layout is null || sm.VertexCount == 0 || sm.Data is null || sm.Data.Length < layout.TotalSize)
            return null;
        return ReadColors(sm.Data, layout, sm.VertexCount, index);
    }

    /// <summary>Цвет вершин. D3DCOLOR лежит в памяти как B,G,R,A — переставляем в RGBA.</summary>
    private static Vector4[]? ReadColors(byte[] data, VertexLayout layout, int vc, int index)
    {
        if (!layout.TryGet(Usage.Color, index, out var slot))
            return null;
        var result = new Vector4[vc];
        for (int i = 0; i < vc; i++)
        {
            int at = layout.OffsetOf(slot, i);
            var v = ReadRaw(data, at, slot.Type);
            if (slot.Type == VType.Color)
                v = new Vector4(v.Z, v.Y, v.X, v.W) / 255.0f;
            else if (slot.Type == VType.U8_4)
                v /= 255.0f;
            result[i] = Vector4.Clamp(v, Vector4.Zero, Vector4.One);
        }
        return result;
    }

    private static Vector2[]? ReadTexCoord(byte[] data, VertexLayout layout, int vc, int index)
    {
        if (!layout.TryGet(Usage.TexCoord, index, out var slot))
            return null;

        var result = new Vector2[vc];
        for (int i = 0; i < vc; i++)
        {
            int at = layout.OffsetOf(slot, i);
            switch (slot.Type)
            {
                case VType.S16_2:
                case VType.S16_4:
                case VType.S16_2N:
                case VType.S16_4N:
                case VType.U16_2N:
                case VType.U16_4N:
                    // fixed point with 11 fractional bits, not a normalised short — и для
                    // «нормализованных» типов тоже (UV Чужого лежат в S16_2N: /32767 давал
                    // развёртку в 1/16 текстуры, как и у OpenCAGE делим на 2048)
                    short u = ReadI16(data, at);
                    short v = ReadI16(data, at + 2);
                    result[i] = new Vector2(u / TexCoordFixedPoint, v / TexCoordFixedPoint);
                    break;
                default:
                    Vector4 raw = ReadRaw(data, at, slot.Type);
                    result[i] = new Vector2(raw.X, raw.Y);
                    break;
            }
        }
        return result;
    }

    private static void ReadSkin(byte[] data, VertexLayout layout, int vc, List<int> palette,
        List<string> warnings, out ushort[]? joints, out Vector4[]? weights)
    {
        joints = null;
        weights = null;
        if (!layout.TryGet(Usage.BlendIndices, 0, out var jointSlot))
            return;

        joints = new ushort[vc * 4];
        weights = new Vector4[vc];
        bool hasWeights = layout.TryGet(Usage.BlendWeight, 0, out var weightSlot);
        bool paletteOverflow = false;

        for (int i = 0; i < vc; i++)
        {
            int jointAt = layout.OffsetOf(jointSlot, i);
            for (int k = 0; k < 4; k++)
            {
                int raw = jointAt + k < data.Length ? data[jointAt + k] : 0;
                // The submesh carries its own bone palette; raw indices point into
                // it, not straight at the skeleton. Identity palettes exist (the
                // Alien has one), so this is a no-op there and required elsewhere.
                if (palette.Count > 0)
                {
                    if (raw < palette.Count)
                        raw = palette[raw];
                    else
                        paletteOverflow = true;
                }
                joints[i * 4 + k] = (ushort)raw;
            }

            if (!hasWeights)
            {
                weights[i] = new Vector4(1, 0, 0, 0);
                continue;
            }

            int wAt = layout.OffsetOf(weightSlot, i);
            int b0 = wAt + 0 < data.Length ? data[wAt + 0] : 0;
            int b1 = wAt + 1 < data.Length ? data[wAt + 1] : 0;
            int b2 = wAt + 2 < data.Length ? data[wAt + 2] : 0;
            int b3 = wAt + 3 < data.Length ? data[wAt + 3] : 0;
            int sum = b0 + b1 + b2 + b3;
            weights[i] = sum == 0
                ? new Vector4(1, 0, 0, 0)
                : new Vector4(b0, b1, b2, b3) / sum;
        }

        if (paletteOverflow)
            warnings.Add("индекс кости вышел за палитру сабмеша, лишние обнулены");
    }

    private static int[] ReadIndices(byte[] data, VertexLayout layout, int indexCount,
        int vertexCount, List<string> warnings)
    {
        if (indexCount <= 0)
            return Array.Empty<int>();

        int available = Math.Max(0, (data.Length - layout.IndexBase) / sizeof(ushort));
        if (available < indexCount)
        {
            warnings.Add($"индексов меньше заявленного: {available} из {indexCount}");
            indexCount = available;
        }
        indexCount -= indexCount % 3;

        var result = new int[indexCount];
        int outOfRange = 0;
        for (int i = 0; i < indexCount; i++)
        {
            int value = ReadU16(data, layout.IndexBase + i * sizeof(ushort));
            if (value >= vertexCount)
                outOfRange++;
            result[i] = value;
        }
        if (outOfRange > 0)
            warnings.Add($"индексов вне диапазона вершин: {outOfRange}");
        return result;
    }

    // ------------------------------------------------------------------- raw
    /// <summary>
    /// Reads one attribute as up to four floats, applying the format's own
    /// normalisation but no model-level scaling.
    /// </summary>
    private static Vector4 ReadRaw(byte[] d, int at, VType type)
    {
        switch (type)
        {
            case VType.FP32_1:
                return new Vector4(ReadF32(d, at), 0, 0, 0);
            case VType.FP32_2:
                return new Vector4(ReadF32(d, at), ReadF32(d, at + 4), 0, 0);
            case VType.FP32_3:
                return new Vector4(ReadF32(d, at), ReadF32(d, at + 4), ReadF32(d, at + 8), 0);
            case VType.FP32_4:
                return new Vector4(ReadF32(d, at), ReadF32(d, at + 4), ReadF32(d, at + 8),
                    ReadF32(d, at + 12));

            case VType.FP16_2:
                return new Vector4(ReadF16(d, at), ReadF16(d, at + 2), 0, 0);
            case VType.FP16_4:
                return new Vector4(ReadF16(d, at), ReadF16(d, at + 2), ReadF16(d, at + 4),
                    ReadF16(d, at + 6));

            case VType.Color:
            case VType.U8_4:
                return new Vector4(Byte(d, at), Byte(d, at + 1), Byte(d, at + 2), Byte(d, at + 3));
            case VType.U8_4N:
                return new Vector4(Byte(d, at), Byte(d, at + 1), Byte(d, at + 2), Byte(d, at + 3))
                       / 255.0f;
            case VType.S8_4N:
                return new Vector4(SByteN(d, at), SByteN(d, at + 1), SByteN(d, at + 2),
                    SByteN(d, at + 3));

            case VType.S16_2:
                return new Vector4(ReadI16(d, at), ReadI16(d, at + 2), 0, 0);
            case VType.S16_4:
                return new Vector4(ReadI16(d, at), ReadI16(d, at + 2), ReadI16(d, at + 4),
                    ReadI16(d, at + 6));
            case VType.S16_2N:
                return new Vector4(ReadI16(d, at) / 32767.0f, ReadI16(d, at + 2) / 32767.0f, 0, 0);
            case VType.S16_4N:
                return new Vector4(ReadI16(d, at) / 32767.0f, ReadI16(d, at + 2) / 32767.0f,
                    ReadI16(d, at + 4) / 32767.0f, ReadI16(d, at + 6) / 32767.0f);
            case VType.U16_2N:
                return new Vector4(ReadU16(d, at) / 65535.0f, ReadU16(d, at + 2) / 65535.0f, 0, 0);
            case VType.U16_4N:
                return new Vector4(ReadU16(d, at) / 65535.0f, ReadU16(d, at + 2) / 65535.0f,
                    ReadU16(d, at + 4) / 65535.0f, ReadU16(d, at + 6) / 65535.0f);

            case VType.Dec3N:
            case VType.UDec3:
                // Not the packed 10:10:10:2 the name suggests. In this game's data it
                // is three biased bytes: every decoded normal, tangent and binormal
                // of the Alien comes out unit length this way, which a wrong
                // interpretation would not.
                return new Vector4(
                    Byte(d, at + 0) / 127.5f - 1.0f,
                    Byte(d, at + 1) / 127.5f - 1.0f,
                    Byte(d, at + 2) / 127.5f - 1.0f,
                    0);

            default:
                return Vector4.Zero;
        }
    }

    private static float Byte(byte[] d, int at) => at >= 0 && at < d.Length ? d[at] : 0;

    private static float SByteN(byte[] d, int at)
        => at >= 0 && at < d.Length ? Math.Max(-1.0f, unchecked((sbyte)d[at]) / 127.0f) : 0;

    private static short ReadI16(byte[] d, int at)
        => at >= 0 && at + 1 < d.Length
            ? BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(at, 2))
            : (short)0;

    private static ushort ReadU16(byte[] d, int at)
        => at >= 0 && at + 1 < d.Length
            ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at, 2))
            : (ushort)0;

    private static float ReadF32(byte[] d, int at)
        => at >= 0 && at + 3 < d.Length
            ? BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at, 4))
            : 0f;

    private static float ReadF16(byte[] d, int at)
        => (float)BitConverter.UInt16BitsToHalf(ReadU16(d, at));
}
