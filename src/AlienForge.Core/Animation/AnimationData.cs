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
    /// <summary>
    /// Puts a Havok rig into the space CATHODE geometry lives in.
    /// </summary>
    /// <remarks>
    /// Measured on the Alien: the mesh bounding box is 1.48 x 2.71 x 4.39 m while the
    /// skeleton's joint cloud is 1.41 x 4.00 x 2.59 m. Y and Z are swapped, and after
    /// swapping them back every axis of the rig comes out slightly smaller than the
    /// mesh (0.95, 0.96, 0.91), which is what a skeleton sitting inside its skin looks
    /// like. The transform that does it is a -90 degree turn about X, taking
    /// (x, y, z) to (x, z, -y). Without it the posed mesh scatters: mean vertex
    /// displacement was 3.06 m and 98% of vertices moved over half a metre.
    /// </remarks>
    public static readonly Matrix4x4 HavokToCathode = Matrix4x4.CreateRotationX(-MathF.PI / 2f);

    public required List<BoneData> Bones { get; init; }

    /// <summary>
    /// Applied to the root bones, which carries it to the whole rig through the
    /// hierarchy. Identity leaves the skeleton in its source space.
    /// </summary>
    public Matrix4x4 Correction { get; init; } = Matrix4x4.Identity;

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
                : local * Correction;
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
    /// <summary>
    /// Settable because the real name does not come from the Havok data. The shipped
    /// packfiles have their names stripped, so the dumper reports a placeholder and the
    /// clip index fills in the name afterwards.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>Position inside its container, or -1 when the dumper did not say.</summary>
    public int Index { get; init; } = -1;

    /// <summary>Container the clip came from, for telling identically named clips apart.</summary>
    public string? Container { get; set; }

    /// <summary>Last path element of <see cref="Name"/>, which is what a person reads.</summary>
    public string ShortName
    {
        get
        {
            int cut = Name.LastIndexOfAny(new[] { '\\', '/' });
            return cut >= 0 && cut + 1 < Name.Length ? Name[(cut + 1)..] : Name;
        }
    }

    public float Duration { get; init; }
    public int Frames { get; init; }
    public float Fps { get; init; } = 30f;
    public int BlendHint { get; init; }
    public required List<TrackData> Tracks { get; init; }

    /// <summary>Additive clips are meant to be layered, not played on their own.</summary>
    public bool IsAdditive => BlendHint != 0;

    public override string ToString()
        => $"{ShortName}  {Frames} кадр., {Duration:F2} с{(IsAdditive ? ", аддитивный" : "")}";
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
    /// <remarks>
    /// A channel holding a single key carries no motion, and this game's clips leave
    /// such channels at a placeholder rather than at the reference value. Measured on
    /// the Alien: the root bone's reference rotation is the Y/Z swap that puts the rig
    /// into CATHODE space, while the clip supplies one identity key for it. Bone 1
    /// cancels that swap for its children, so honouring the placeholder swings
    /// everything below the root by 90 degrees and scatters the mesh. Every other
    /// static channel in the clip matched the reference exactly, so treating a
    /// one-key channel as "not animated" both keeps those values and repairs the root.
    /// </remarks>
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

            // Bones the clip does not touch keep their bind pose. Одноключевой канал
            // берём, если это не заглушка (см. IsPlaceholder): статические позы
            // (ALIEN_COVERMAG_POSES и т.п.) состоят целиком из одноключевых каналов
            // с реальными значениями — 98 из 127 костей отличаются от bind.
            Vector3 t = UseTranslation(track) ? track!.TranslationAt(frame, bone.Translation) : bone.Translation;
            Quaternion r = UseRotation(track) ? track!.RotationAt(frame, bone.Rotation) : bone.Rotation;
            Vector3 s = UseScale(track) ? track!.ScaleAt(frame, bone.Scale) : bone.Scale;

            if (r.LengthSquared() > 1e-8f)
                r = Quaternion.Normalize(r);
            else
                r = bone.Rotation;

            Matrix4x4 local = SkeletonData.Compose(t, r, s);
            world[i] = bone.Parent >= 0 && bone.Parent < i
                ? local * world[bone.Parent]
                : local * skeleton.Correction;
        }
        return world;
    }

    /// <summary>
    /// Заглушка одноключевого канала: identity-поворот / нулевой сдвиг / единичный
    /// масштаб. Так дампер помечает "кость не трогалась" (у корня Чужого клип даёт
    /// identity, хотя reference-поворот корня — разворот в CATHODE-пространство).
    /// Любое другое одноключевое значение — реальная статическая поза.
    /// </summary>
    public static bool IsPlaceholder(Quaternion q)
        => MathF.Abs(q.X) < 1e-6f && MathF.Abs(q.Y) < 1e-6f && MathF.Abs(q.Z) < 1e-6f && MathF.Abs(MathF.Abs(q.W) - 1f) < 1e-6f;
    public static bool IsPlaceholder(Vector3 v) => v.LengthSquared() < 1e-12f;
    public static bool IsPlaceholderScale(Vector3 v) => (v - Vector3.One).LengthSquared() < 1e-12f;

    public static bool UseTranslation(TrackData? track)
        => track is not null && track.Translation.Length > 0
           && (track.Translation.Length > 1 || !IsPlaceholder(track.Translation[0]));
    public static bool UseRotation(TrackData? track)
        => track is not null && track.Rotation.Length > 0
           && (track.Rotation.Length > 1 || !IsPlaceholder(track.Rotation[0]));
    public static bool UseScale(TrackData? track)
        => track is not null && track.Scale.Length > 0
           && (track.Scale.Length > 1 || !IsPlaceholderScale(track.Scale[0]));

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
