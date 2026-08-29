using System.Numerics;

namespace AlienForge.Core.Mesh;

/// <summary>
/// A submesh decoded into plain arrays, ready to hand to an exporter or a viewport.
/// Optional channels are null when the source format does not carry them.
/// </summary>
public sealed class DecodedMesh
{
    public required string Name { get; init; }
    public required string? MaterialName { get; init; }

    /// <summary>Which component / LOD / submesh of the CS2 this came from.</summary>
    public required int ComponentIndex { get; init; }
    public required int LodIndex { get; init; }
    public required int SubmeshIndex { get; init; }
    public required string LodName { get; init; }

    public required Vector3[] Positions { get; init; }
    public Vector3[]? Normals { get; init; }
    public Vector4[]? Tangents { get; init; }
    public Vector2[]? Uv0 { get; init; }
    public Vector2[]? Uv1 { get; init; }

    /// <summary>Global bone indices, already run through the submesh bone palette.</summary>
    public ushort[]? Joints { get; init; }

    /// <summary>Weights normalised to sum to 1.</summary>
    public Vector4[]? Weights { get; init; }

    public required int[] Indices { get; init; }

    public int VertexCount => Positions.Length;
    public int TriangleCount => Indices.Length / 3;
    public bool IsSkinned => Joints is not null && Weights is not null;

    /// <summary>Anything the decoder had to work around, for reporting.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Share of triangles with an edge longer than <paramref name="limit"/> metres.
    /// A healthy character mesh sits at 0%; a nonzero figure is the signature of a
    /// misread vertex stream, where single vertices land far from their neighbours.
    /// </summary>
    public double LongEdgeRatio(float limit = 0.15f)
    {
        if (Indices.Length < 3)
            return 0.0;

        int bad = 0, total = 0;
        for (int t = 0; t + 2 < Indices.Length; t += 3)
        {
            int a = Indices[t], b = Indices[t + 1], c = Indices[t + 2];
            total++;
            if (a >= Positions.Length || b >= Positions.Length || c >= Positions.Length)
            {
                bad++;
                continue;
            }
            float longest = MathF.Max(
                Vector3.Distance(Positions[a], Positions[b]),
                MathF.Max(Vector3.Distance(Positions[b], Positions[c]),
                          Vector3.Distance(Positions[c], Positions[a])));
            if (longest > limit)
                bad++;
        }
        return total == 0 ? 0.0 : bad * 100.0 / total;
    }

    public (Vector3 min, Vector3 max) Bounds()
    {
        if (Positions.Length == 0)
            return (Vector3.Zero, Vector3.Zero);
        Vector3 min = Positions[0], max = Positions[0];
        foreach (var p in Positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }
}
