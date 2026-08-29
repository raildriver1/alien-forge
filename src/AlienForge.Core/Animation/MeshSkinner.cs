using System.Numerics;
using AlienForge.Core.Mesh;

namespace AlienForge.Core.Animation;

/// <summary>
/// Moves a mesh from its bind pose into an animated pose on the CPU.
/// </summary>
/// <remarks>
/// Linear blend skinning: each vertex is transformed by up to four bone matrices and
/// mixed by its weights. Done on the CPU because the preview draws through WPF's
/// retained 3D graph, which has no vertex shader to hand this to. The buffers are
/// reused between frames so playback does not allocate per frame.
/// </remarks>
public sealed class MeshSkinner
{
    private readonly Vector3[] _positions;
    private readonly Vector3[]? _normals;

    public MeshSkinner(DecodedMesh mesh)
    {
        Mesh = mesh;
        _positions = new Vector3[mesh.VertexCount];
        _normals = mesh.Normals is { Length: > 0 } ? new Vector3[mesh.VertexCount] : null;
    }

    public DecodedMesh Mesh { get; }

    /// <summary>Skinned positions, valid until the next call.</summary>
    public Vector3[] Positions => _positions;

    /// <summary>Skinned normals, or null when the source mesh had none.</summary>
    public Vector3[]? Normals => _normals;

    public bool CanSkin => Mesh.Joints is not null && Mesh.Weights is not null;

    /// <summary>
    /// Applies the pose. Bones outside the matrix array are ignored rather than
    /// throwing, because a submesh palette can name a bone the clip's skeleton does
    /// not have.
    /// </summary>
    public void Apply(Matrix4x4[] skinMatrices)
    {
        var joints = Mesh.Joints;
        var weights = Mesh.Weights;
        if (joints is null || weights is null)
        {
            Array.Copy(Mesh.Positions, _positions, _positions.Length);
            if (_normals is not null && Mesh.Normals is not null)
                Array.Copy(Mesh.Normals, _normals, _normals.Length);
            return;
        }

        // Allocated once: a stackalloc inside the loop would grow the frame per vertex.
        Span<float> weightSpan = stackalloc float[4];

        for (int v = 0; v < _positions.Length; v++)
        {
            Vector4 w = weights[v];
            weightSpan[0] = w.X;
            weightSpan[1] = w.Y;
            weightSpan[2] = w.Z;
            weightSpan[3] = w.W;

            Vector3 position = Vector3.Zero;
            Vector3 normal = Vector3.Zero;
            float total = 0f;

            for (int k = 0; k < 4; k++)
            {
                float weight = weightSpan[k];
                if (weight <= 0f)
                    continue;
                int bone = joints[v * 4 + k];
                if (bone < 0 || bone >= skinMatrices.Length)
                    continue;

                Matrix4x4 m = skinMatrices[bone];
                position += Vector3.Transform(Mesh.Positions[v], m) * weight;
                if (_normals is not null && Mesh.Normals is not null)
                    normal += Vector3.TransformNormal(Mesh.Normals[v], m) * weight;
                total += weight;
            }

            // A vertex whose bones are all missing keeps its bind position instead of
            // collapsing to the origin.
            _positions[v] = total > 1e-5f ? position / total : Mesh.Positions[v];
            if (_normals is not null && Mesh.Normals is not null)
            {
                Vector3 n = total > 1e-5f ? normal : Mesh.Normals[v];
                float length = n.Length();
                _normals[v] = length > 1e-6f ? n / length : Mesh.Normals[v];
            }
        }
    }
}
