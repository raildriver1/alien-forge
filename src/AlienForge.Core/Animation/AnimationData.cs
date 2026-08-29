using System.Numerics;

namespace AlienForge.Core.Animation;

/// <summary>One bone of a Havok skeleton, with its bind pose in parent space.</summary>
public sealed class BoneData
{
    public required string Name { get; init; }
    public required int Parent { get; init; }
    public Vector3 Translation { get; init; }
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public Vector3 Scale { get; init; } = Vector3.One;
}

public sealed class SkeletonData
{
    public required List<BoneData> Bones { get; init; }

    public int Count => Bones.Count;

    /// <summary>Bind pose of every bone in model space, parents applied.</summary>
    public Matrix4x4[] BindWorld()
    {
        var world = new Matrix4x4[Bones.Count];
        for (int i = 0; i < Bones.Count; i++)
        {
            var bone = Bones[i];
            Matrix4x4 local = Compose(bone.Translation, bone.Rotation, bone.Scale);
            // Bones always come after their parent in a Havok skeleton, so a single
            // forward pass is enough.
            world[i] = bone.Parent >= 0 && bone.Parent < i
                ? local * world[bone.Parent]
                : local;
        }
        return world;
    }

    public static Matrix4x4 Compose(Vector3 t, Quaternion r, Vector3 s)
        => Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r)
           * Matrix4x4.CreateTranslation(t);

    public int IndexOf(string name)
        => Bones.FindIndex(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Animation for one bone. A channel with a single entry is constant for the whole
/// clip, which is how the dumper collapses bones that do not move.
/// </summary>
public sealed class TrackData
{
    public required int Bone { get; init; }
    public Vector3[] Translation { get; init; } = Array.Empty<Vector3>();
    public Quaternion[] Rotation { get; init; } = Array.Empty<Quaternion>();
    public Vector3[] Scale { get; init; } = Array.Empty<Vector3>();

    private static T Sample<T>(T[] values, int frame, T fallback)
        => values.Length == 0 ? fallback
            : values[Math.Clamp(frame, 0, values.Length - 1)];

    public Vector3 TranslationAt(int frame, Vector3 fallback)
        => Sample(Translation, frame, fallback);

    public Quaternion RotationAt(int frame, Quaternion fallback)
        => Sample(Rotation, frame, fallback);

    public Vector3 ScaleAt(int frame, Vector3 fallback)
        => Sample(Scale, frame, fallback);
}

public sealed class ClipData
{
    public required string Name { get; init; }
    public float Duration { get; init; }
    public int Frames { get; init; }
    public float Fps { get; init; } = 30f;
    public int BlendHint { get; init; }
    public required List<TrackData> Tracks { get; init; }

    /// <summary>Additive clips are meant to be layered, not played on their own.</summary>
    public bool IsAdditive => BlendHint != 0;

    public override string ToString()
        => $"{Name}  {Frames} кадр., {Duration:F2} с";
}

/// <summary>A decoded skeleton plus every clip that came out of one dump.</summary>
public sealed class AnimationBundle
{
    public required SkeletonData Skeleton { get; init; }
    public required List<ClipData> Clips { get; init; }
    public float Fps { get; init; } = 30f;
}

/// <summary>
/// Builds per-frame bone matrices, and the matrices that move mesh vertices from
/// bind pose into the animated pose.
/// </summary>
public static class PoseEvaluator
{
    /// <summary>Bone matrices in model space for one frame.</summary>
    public static Matrix4x4[] WorldPose(SkeletonData skeleton, ClipData? clip, int frame)
    {
        var byBone = new TrackData?[skeleton.Count];
        if (clip is not null)
            foreach (var track in clip.Tracks)
                if (track.Bone >= 0 && track.Bone < byBone.Length)
                    byBone[track.Bone] = track;

        var world = new Matrix4x4[skeleton.Count];
        for (int i = 0; i < skeleton.Count; i++)
        {
            var bone = skeleton.Bones[i];
            var track = byBone[i];

            // Bones the clip does not touch keep their bind pose.
            Vector3 t = track?.TranslationAt(frame, bone.Translation) ?? bone.Translation;
            Quaternion r = track?.RotationAt(frame, bone.Rotation) ?? bone.Rotation;
            Vector3 s = track?.ScaleAt(frame, bone.Scale) ?? bone.Scale;

            if (r.LengthSquared() > 1e-8f)
                r = Quaternion.Normalize(r);
            else
                r = bone.Rotation;

            Matrix4x4 local = SkeletonData.Compose(t, r, s);
            world[i] = bone.Parent >= 0 && bone.Parent < i
                ? local * world[bone.Parent]
                : local;
        }
        return world;
    }

    /// <summary>
    /// Skinning matrices: inverse bind followed by the animated pose. Multiplying a
    /// bind-pose vertex by these puts it where the animation wants it.
    /// </summary>
    public static Matrix4x4[] SkinMatrices(SkeletonData skeleton, Matrix4x4[] worldPose)
    {
        var bind = skeleton.BindWorld();
        var result = new Matrix4x4[skeleton.Count];
        for (int i = 0; i < skeleton.Count; i++)
        {
            if (!Matrix4x4.Invert(bind[i], out Matrix4x4 inverseBind))
                inverseBind = Matrix4x4.Identity;
            result[i] = inverseBind * worldPose[i];
        }
        return result;
    }
}
