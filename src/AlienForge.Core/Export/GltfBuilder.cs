using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlienForge.Core.Export;

/// <summary>
/// Assembles a glTF 2.0 binary (.glb) with a single embedded buffer.
/// </summary>
/// <remarks>
/// A .glb keeps geometry and PNG textures in one file, which is what makes the
/// export drag-and-droppable into Blender or Godot without hunting for side files.
/// The layout rules that matter: buffer views are 4-byte aligned, and both the JSON
/// and BIN chunks are padded to a 4-byte boundary (JSON with spaces, BIN with zeros).
/// </remarks>
public sealed class GltfBuilder
{
    public const int ComponentByte = 5120;
    public const int ComponentUnsignedByte = 5121;
    public const int ComponentShort = 5122;
    public const int ComponentUnsignedShort = 5123;
    public const int ComponentUnsignedInt = 5125;
    public const int ComponentFloat = 5126;

    public const int TargetArrayBuffer = 34962;
    public const int TargetElementArrayBuffer = 34963;

    private readonly MemoryStream _bin = new();
    private readonly JsonArray _bufferViews = new();
    private readonly JsonArray _accessors = new();
    private readonly JsonArray _meshes = new();
    private readonly JsonArray _materials = new();
    private readonly JsonArray _images = new();
    private readonly JsonArray _textures = new();
    private readonly JsonArray _samplers = new();
    private readonly JsonArray _nodes = new();
    private readonly JsonArray _skins = new();
    private readonly JsonArray _animations = new();

    public string Generator { get; set; } = "AlienForge";

    // ------------------------------------------------------------------ buffer
    private void PadTo(int alignment)
    {
        while (_bin.Length % alignment != 0)
            _bin.WriteByte(0);
    }

    public int AddBufferView(ReadOnlySpan<byte> data, int? target = null)
    {
        PadTo(4);
        int offset = (int)_bin.Length;
        _bin.Write(data);

        var view = new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = offset,
            ["byteLength"] = data.Length,
        };
        if (target is not null)
            view["target"] = target.Value;
        _bufferViews.Add(view);
        return _bufferViews.Count - 1;
    }

    public int AddAccessor(int bufferView, int componentType, int count, string type,
        IReadOnlyList<float>? min = null, IReadOnlyList<float>? max = null,
        bool normalized = false)
    {
        var accessor = new JsonObject
        {
            ["bufferView"] = bufferView,
            ["componentType"] = componentType,
            ["count"] = count,
            ["type"] = type,
        };
        if (normalized)
            accessor["normalized"] = true;
        if (min is not null)
            accessor["min"] = ToArray(min);
        if (max is not null)
            accessor["max"] = ToArray(max);
        _accessors.Add(accessor);
        return _accessors.Count - 1;
    }

    private static JsonArray ToArray(IReadOnlyList<float> values)
    {
        var array = new JsonArray();
        foreach (float v in values)
            array.Add(v);
        return array;
    }

    // ------------------------------------------------------------- attributes
    /// <summary>Positions, with the min/max the spec requires for POSITION.</summary>
    public int AddPositions(IReadOnlyList<Vector3> values)
    {
        var bytes = new byte[values.Count * 12];
        var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
        var max = new[] { float.MinValue, float.MinValue, float.MinValue };
        for (int i = 0; i < values.Count; i++)
        {
            Vector3 v = values[i];
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 12 + 0, 4), v.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 12 + 4, 4), v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 12 + 8, 4), v.Z);
            min[0] = MathF.Min(min[0], v.X); max[0] = MathF.Max(max[0], v.X);
            min[1] = MathF.Min(min[1], v.Y); max[1] = MathF.Max(max[1], v.Y);
            min[2] = MathF.Min(min[2], v.Z); max[2] = MathF.Max(max[2], v.Z);
        }
        if (values.Count == 0)
        {
            min = new[] { 0f, 0f, 0f };
            max = new[] { 0f, 0f, 0f };
        }
        int view = AddBufferView(bytes, TargetArrayBuffer);
        return AddAccessor(view, ComponentFloat, values.Count, "VEC3", min, max);
    }

    public int AddVec3(IReadOnlyList<Vector3> values)
    {
        var bytes = new byte[values.Count * 12];
        for (int i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 12 + 0, 4), values[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 12 + 4, 4), values[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 12 + 8, 4), values[i].Z);
        }
        int view = AddBufferView(bytes, TargetArrayBuffer);
        return AddAccessor(view, ComponentFloat, values.Count, "VEC3");
    }

    public int AddVec4(IReadOnlyList<Vector4> values)
    {
        var bytes = new byte[values.Count * 16];
        for (int i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 16 + 0, 4), values[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 16 + 4, 4), values[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 16 + 8, 4), values[i].Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 16 + 12, 4), values[i].W);
        }
        int view = AddBufferView(bytes, TargetArrayBuffer);
        return AddAccessor(view, ComponentFloat, values.Count, "VEC4");
    }

    public int AddVec2(IReadOnlyList<Vector2> values)
    {
        var bytes = new byte[values.Count * 8];
        for (int i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 8 + 0, 4), values[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 8 + 4, 4), values[i].Y);
        }
        int view = AddBufferView(bytes, TargetArrayBuffer);
        return AddAccessor(view, ComponentFloat, values.Count, "VEC2");
    }

    /// <summary>16-bit indices. Submesh vertex counts are stored as UInt16, so these fit.</summary>
    public int AddIndices(IReadOnlyList<int> indices)
    {
        var bytes = new byte[indices.Count * 2];
        for (int i = 0; i < indices.Count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2),
                (ushort)Math.Clamp(indices[i], 0, ushort.MaxValue));
        int view = AddBufferView(bytes, TargetElementArrayBuffer);
        return AddAccessor(view, ComponentUnsignedShort, indices.Count, "SCALAR");
    }

    // -------------------------------------------------------------- skinning
    /// <summary>JOINTS_0: four bone indices per vertex, as unsigned shorts.</summary>
    public int AddJoints(ushort[] joints)
    {
        int vertices = joints.Length / 4;
        var bytes = new byte[vertices * 8];
        for (int i = 0; i < vertices * 4; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), joints[i]);
        int view = AddBufferView(bytes, TargetArrayBuffer);
        return AddAccessor(view, ComponentUnsignedShort, vertices, "VEC4");
    }

    /// <summary>
    /// Matrices for skins and animation output. glTF wants 16 floats in column-major
    /// order; System.Numerics uses the row-vector convention, so writing its fields in
    /// row order (M11..M44) already produces the layout the spec asks for, translation
    /// included. No transpose needed.
    /// </summary>
    public int AddMatrices(IReadOnlyList<Matrix4x4> matrices)
    {
        var bytes = new byte[matrices.Count * 64];
        // Allocated once: a stackalloc inside the loop grows the frame per matrix.
        Span<float> f = stackalloc float[16];
        for (int i = 0; i < matrices.Count; i++)
        {
            Matrix4x4 m = matrices[i];
            f[0] = m.M11; f[1] = m.M12; f[2] = m.M13; f[3] = m.M14;
            f[4] = m.M21; f[5] = m.M22; f[6] = m.M23; f[7] = m.M24;
            f[8] = m.M31; f[9] = m.M32; f[10] = m.M33; f[11] = m.M34;
            f[12] = m.M41; f[13] = m.M42; f[14] = m.M43; f[15] = m.M44;
            for (int k = 0; k < 16; k++)
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 64 + k * 4, 4), f[k]);
        }
        // No bufferView target: targets describe vertex and index data only.
        int view = AddBufferView(bytes);
        return AddAccessor(view, ComponentFloat, matrices.Count, "MAT4");
    }

    public int AddSkin(string name, IReadOnlyList<int> jointNodes, int inverseBindMatrices,
        int? skeletonRoot = null)
    {
        var joints = new JsonArray();
        foreach (int j in jointNodes)
            joints.Add(j);

        var skin = new JsonObject
        {
            ["name"] = name,
            ["joints"] = joints,
            ["inverseBindMatrices"] = inverseBindMatrices,
        };
        if (skeletonRoot is not null)
            skin["skeleton"] = skeletonRoot.Value;
        _skins.Add(skin);
        return _skins.Count - 1;
    }

    // ------------------------------------------------------------- animation
    /// <summary>
    /// Keyframe times. The spec requires min/max on an animation sampler's input, so
    /// players know the clip's extent without scanning the buffer.
    /// </summary>
    public int AddTimes(IReadOnlyList<float> times)
    {
        var bytes = new byte[times.Count * 4];
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < times.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), times[i]);
            min = MathF.Min(min, times[i]);
            max = MathF.Max(max, times[i]);
        }
        if (times.Count == 0) { min = 0f; max = 0f; }

        int view = AddBufferView(bytes);
        return AddAccessor(view, ComponentFloat, times.Count, "SCALAR",
            new[] { min }, new[] { max });
    }

    /// <summary>VEC3 animation output. Untargeted, unlike the vertex-attribute variant.</summary>
    public int AddVec3Output(IReadOnlyList<Vector3> values)
    {
        var bytes = new byte[values.Count * 12];
        for (int i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 12 + 0, 4), values[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 12 + 4, 4), values[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 12 + 8, 4), values[i].Z);
        }
        int view = AddBufferView(bytes);
        return AddAccessor(view, ComponentFloat, values.Count, "VEC3");
    }

    /// <summary>Rotation output, written as (x, y, z, w) to match glTF.</summary>
    public int AddQuaternionOutput(IReadOnlyList<Quaternion> values)
    {
        var bytes = new byte[values.Count * 16];
        for (int i = 0; i < values.Count; i++)
        {
            Quaternion q = values[i];
            if (q.LengthSquared() > 1e-8f)
                q = Quaternion.Normalize(q);
            else
                q = Quaternion.Identity;
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 16 + 0, 4), q.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 16 + 4, 4), q.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 16 + 8, 4), q.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 16 + 12, 4), q.W);
        }
        int view = AddBufferView(bytes);
        return AddAccessor(view, ComponentFloat, values.Count, "VEC4");
    }

    public int AddAnimation(string name, JsonArray channels, JsonArray samplers, JsonObject? extras = null)
    {
        var anim = new JsonObject
        {
            ["name"] = name,
            ["channels"] = channels,
            ["samplers"] = samplers,
        };
        if (extras is not null) anim["extras"] = extras;
        _animations.Add(anim);
        return _animations.Count - 1;
    }

    public int SkinCount => _skins.Count;
    public int AnimationCount => _animations.Count;

    // ---------------------------------------------------------------- images
    public int AddPngImage(byte[] png, string name)
    {
        int view = AddBufferView(png);
        _images.Add(new JsonObject
        {
            ["bufferView"] = view,
            ["mimeType"] = "image/png",
            ["name"] = name,
        });
        return _images.Count - 1;
    }

    /// <summary>Adds the one repeating sampler this exporter uses, once.</summary>
    public int EnsureSampler()
    {
        if (_samplers.Count == 0)
            _samplers.Add(new JsonObject
            {
                ["magFilter"] = 9729,  // LINEAR
                ["minFilter"] = 9987,  // LINEAR_MIPMAP_LINEAR
                ["wrapS"] = 10497,     // REPEAT
                ["wrapT"] = 10497,
            });
        return 0;
    }

    public int AddTexture(int imageIndex)
    {
        _textures.Add(new JsonObject
        {
            ["sampler"] = EnsureSampler(),
            ["source"] = imageIndex,
        });
        return _textures.Count - 1;
    }

    private readonly HashSet<string> _extensionsUsed = new();

    /// <summary>Объявляет расширение glTF в extensionsUsed (например KHR_texture_transform).</summary>
    public void UseExtension(string name) => _extensionsUsed.Add(name);

    public int TextureCount => _textures.Count;

    public int AddMaterial(JsonObject material)
    {
        _materials.Add(material);
        if (material.ToJsonString().Contains("KHR_texture_transform"))
            UseExtension("KHR_texture_transform");
        return _materials.Count - 1;
    }

    public int AddMesh(string name, JsonArray primitives, JsonObject? extras = null)
    {
        var mesh = new JsonObject
        {
            ["name"] = name,
            ["primitives"] = primitives,
        };
        if (extras is not null)
            mesh["extras"] = extras;
        _meshes.Add(mesh);
        return _meshes.Count - 1;
    }

    public int AddNode(JsonObject node)
    {
        _nodes.Add(node);
        return _nodes.Count - 1;
    }

    public int MaterialCount => _materials.Count;
    public int ImageCount => _images.Count;
    public int MeshCount => _meshes.Count;

    // ------------------------------------------------------------------- save
    public long Save(string path, IReadOnlyList<int> sceneRootNodes, string sceneName = "Scene",
        JsonObject? extras = null)
    {
        var root = new JsonObject
        {
            ["asset"] = new JsonObject
            {
                ["version"] = "2.0",
                ["generator"] = Generator,
            },
        };
        if (extras is not null)
            root["extras"] = extras;
        if (_extensionsUsed.Count > 0)
        {
            var used = new JsonArray();
            foreach (var ext in _extensionsUsed) used.Add(ext);
            root["extensionsUsed"] = used;
        }

        var sceneNodes = new JsonArray();
        foreach (int n in sceneRootNodes)
            sceneNodes.Add(n);
        root["scene"] = 0;
        root["scenes"] = new JsonArray
        {
            new JsonObject { ["name"] = sceneName, ["nodes"] = sceneNodes },
        };

        root["nodes"] = _nodes;
        if (_meshes.Count > 0) root["meshes"] = _meshes;
        if (_materials.Count > 0) root["materials"] = _materials;
        if (_images.Count > 0) root["images"] = _images;
        if (_samplers.Count > 0) root["samplers"] = _samplers;
        if (_textures.Count > 0) root["textures"] = _textures;
        if (_skins.Count > 0) root["skins"] = _skins;
        if (_animations.Count > 0) root["animations"] = _animations;
        if (_accessors.Count > 0) root["accessors"] = _accessors;
        if (_bufferViews.Count > 0) root["bufferViews"] = _bufferViews;

        PadTo(4);
        byte[] binary = _bin.ToArray();
        root["buffers"] = new JsonArray
        {
            new JsonObject { ["byteLength"] = binary.Length },
        };

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(root,
            new JsonSerializerOptions { WriteIndented = false });
        int jsonPadded = (json.Length + 3) / 4 * 4;

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);

        int total = 12 + 8 + jsonPadded + 8 + binary.Length;
        w.Write(Encoding.ASCII.GetBytes("glTF"));
        w.Write(2u);
        w.Write((uint)total);

        w.Write((uint)jsonPadded);
        w.Write(Encoding.ASCII.GetBytes("JSON"));
        w.Write(json);
        for (int i = json.Length; i < jsonPadded; i++)
            w.Write((byte)0x20); // JSON chunk pads with spaces

        w.Write((uint)binary.Length);
        w.Write(new byte[] { (byte)'B', (byte)'I', (byte)'N', 0 });
        w.Write(binary);

        return total;
    }
}
