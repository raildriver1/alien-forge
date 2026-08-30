using System.Diagnostics;
using System.Numerics;
using System.Text.Json;

namespace AlienForge.Core.Animation;

/// <summary>
/// Runs the Havok dumper and turns its JSON into skeletons and clips.
/// </summary>
/// <remarks>
/// Clip data in this game is spline-compressed Havok, which CathodeLib does not
/// decode: its Skeleton class only reads the blob length. Rather than reimplement
/// the decompressor, this drives hkdump.exe, a small tool built against HavokLib
/// during the earlier reverse engineering of the Alien's animations. Its interface:
///   hkdump &lt;in.hkx&gt; &lt;out.json&gt; [--skeleton skel.hkx] [--fps N] [--skeleton-only]
/// A clip needs the skeleton passed alongside it, because track indices are mapped
/// onto bone indices through the skeleton's binding.
/// </remarks>
public sealed class HavokDump
{
    public const string ToolName = "hkdump.exe";

    private HavokDump(string toolPath) => ToolPath = toolPath;

    public string ToolPath { get; }

    /// <summary>Explicit path chosen by the user; tried before the known locations.</summary>
    public static string? PreferredToolPath { get; set; }

    /// <summary>
    /// Looks for the tool in the places it is likely to be: a user-chosen path, next
    /// to this application, then the build output from the reverse engineering work.
    /// </summary>
    public static string? FindTool()
    {
        foreach (string? candidate in Candidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static IEnumerable<string?> Candidates()
    {
        yield return PreferredToolPath;

        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, ToolName);
        yield return Path.Combine(baseDir, "tools", ToolName);

        string? desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (!string.IsNullOrEmpty(desktop))
        {
            yield return Path.Combine(desktop, "_havoklib", "build", "hkdump_bin", ToolName);
            yield return Path.Combine(desktop, ToolName);
        }
    }

    public static HavokDump? TryCreate()
    {
        string? tool = FindTool();
        return tool is null ? null : new HavokDump(tool);
    }

    // ------------------------------------------------------------------- runs
    /// <summary>Decodes a clip together with its skeleton.</summary>
    public AnimationBundle Load(byte[] clipHkx, byte[]? skeletonHkx, float fps = 30f)
    {
        string work = Path.Combine(Path.GetTempPath(),
            "alienforge_hk_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            string clipPath = Path.Combine(work, "clip.hkx");
            string jsonPath = Path.Combine(work, "out.json");
            File.WriteAllBytes(clipPath, clipHkx);

            var args = new List<string> { Quote(clipPath), Quote(jsonPath) };
            if (skeletonHkx is { Length: > 0 })
            {
                string skeletonPath = Path.Combine(work, "skeleton.hkx");
                File.WriteAllBytes(skeletonPath, skeletonHkx);
                args.Add("--skeleton");
                args.Add(Quote(skeletonPath));
            }
            args.Add("--fps");
            args.Add(fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

            Run(string.Join(' ', args), work);
            if (!File.Exists(jsonPath))
                throw new InvalidOperationException(
                    $"{ToolName} не создал JSON. Проверьте, что клип действительно Havok.");

            return Parse(File.ReadAllText(jsonPath));
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>
    /// The dumper's raw JSON for a skeleton, for inspecting what it actually reports.
    /// </summary>
    public string SkeletonJson(byte[] skeletonHkx)
    {
        string work = Path.Combine(Path.GetTempPath(),
            "alienforge_hk_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            string skeletonPath = Path.Combine(work, "skeleton.hkx");
            string jsonPath = Path.Combine(work, "out.json");
            File.WriteAllBytes(skeletonPath, skeletonHkx);
            Run($"{Quote(skeletonPath)} {Quote(jsonPath)} --skeleton-only", work);
            return File.Exists(jsonPath) ? File.ReadAllText(jsonPath) : string.Empty;
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>Decodes just a skeleton, which is much faster than sampling a clip.</summary>
    public SkeletonData LoadSkeletonOnly(byte[] skeletonHkx)
    {
        string work = Path.Combine(Path.GetTempPath(),
            "alienforge_hk_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            string skeletonPath = Path.Combine(work, "skeleton.hkx");
            string jsonPath = Path.Combine(work, "out.json");
            File.WriteAllBytes(skeletonPath, skeletonHkx);

            Run($"{Quote(skeletonPath)} {Quote(jsonPath)} --skeleton-only", work);
            if (!File.Exists(jsonPath))
                throw new InvalidOperationException($"{ToolName} не создал JSON для скелета.");

            var bundle = Parse(File.ReadAllText(jsonPath));
            return bundle.Skeleton;
        }
        finally
        {
            TryDelete(work);
        }
    }

    private void Run(string arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(ToolPath, arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Не удалось запустить {ToolPath}");
        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(true); } catch (Exception) { }
            throw new TimeoutException($"{ToolName} не ответил за 120 с.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{ToolName} завершился с кодом {process.ExitCode}. {error.Trim()}");
    }

    private static string Quote(string path) => $"\"{path}\"";

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing the operation over.
        }
    }

    // ------------------------------------------------------------------ parse
    public static AnimationBundle Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        float fps = root.TryGetProperty("fps", out var fpsElement)
            ? (float)fpsElement.GetDouble()
            : 30f;

        var bones = new List<BoneData>();
        if (root.TryGetProperty("skeletons", out var skeletons)
            && skeletons.ValueKind == JsonValueKind.Array)
        {
            // Several skeletons can be present; the richest one is the real rig.
            JsonElement best = default;
            int bestCount = -1;
            foreach (var candidate in skeletons.EnumerateArray())
            {
                int count = candidate.TryGetProperty("bones", out var b)
                            && b.ValueKind == JsonValueKind.Array
                    ? b.GetArrayLength()
                    : 0;
                if (count > bestCount)
                {
                    bestCount = count;
                    best = candidate;
                }
            }
            if (bestCount > 0 && best.TryGetProperty("bones", out var boneArray))
            {
                foreach (var bone in boneArray.EnumerateArray())
                {
                    // The shipped skeletons carry empty bone names: the release build
                    // strips them, so the file really does say "name": "". A generated
                    // name keeps the rig usable, and glTF needs non-empty node names
                    // or importers invent their own.
                    string? reported = bone.TryGetProperty("name", out var n)
                        ? n.GetString()
                        : null;
                    bones.Add(new BoneData
                    {
                        Name = string.IsNullOrWhiteSpace(reported)
                            ? $"bone{bones.Count:D3}"
                            : reported,
                        Parent = bone.TryGetProperty("parent", out var p) ? p.GetInt32() : -1,
                        Translation = ReadVector3(bone, "t"),
                        Rotation = ReadQuaternion(bone, "r"),
                        Scale = ReadVector3(bone, "s", Vector3.One),
                    });
                }
            }
        }

        var clips = new List<ClipData>();
        if (root.TryGetProperty("animations", out var animations)
            && animations.ValueKind == JsonValueKind.Array)
        {
            foreach (var animation in animations.EnumerateArray())
                clips.Add(ReadClip(animation, fps));
        }

        return new AnimationBundle
        {
            // The rig comes out of Havok in its own space; this lines it up with the
            // CATHODE mesh so bind poses and skinning agree.
            Skeleton = new SkeletonData
            {
                Bones = bones,
                Correction = SkeletonData.HavokToCathode,
            },
            Clips = clips,
            Fps = fps,
        };
    }

    private static ClipData ReadClip(JsonElement animation, float defaultFps)
    {
        int frames = animation.TryGetProperty("numFrames", out var f) ? f.GetInt32() : 1;
        var tracks = new List<TrackData>();

        if (animation.TryGetProperty("tracks", out var trackArray)
            && trackArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var track in trackArray.EnumerateArray())
            {
                int bone = track.TryGetProperty("bone", out var b) ? b.GetInt32() : -1;
                if (bone < 0)
                    continue;
                tracks.Add(new TrackData
                {
                    Bone = bone,
                    Translation = ReadVector3Array(track, "t"),
                    Rotation = ReadQuaternionArray(track, "r"),
                    Scale = ReadVector3Array(track, "s"),
                });
            }
        }

        // Clip names are stripped too. The index inside the container is the only
        // stable handle, so it becomes the name when there is nothing better.
        string? reportedName = animation.TryGetProperty("name", out var n)
            ? n.GetString()
            : null;
        int index = animation.TryGetProperty("index", out var idx) ? idx.GetInt32() : -1;

        return new ClipData
        {
            Name = !string.IsNullOrWhiteSpace(reportedName)
                ? reportedName
                : index >= 0 ? $"clip{index:D3}" : "clip",
            Index = index,
            Duration = animation.TryGetProperty("duration", out var d)
                ? (float)d.GetDouble()
                : frames / Math.Max(1f, defaultFps),
            Frames = Math.Max(1, frames),
            Fps = defaultFps,
            BlendHint = animation.TryGetProperty("blendHint", out var h) ? h.GetInt32() : 0,
            Tracks = tracks,
        };
    }

    private static Vector3 ReadVector3(JsonElement owner, string name, Vector3 fallback = default)
    {
        if (!owner.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() < 3)
            return fallback;
        return new Vector3(
            (float)element[0].GetDouble(),
            (float)element[1].GetDouble(),
            (float)element[2].GetDouble());
    }

    private static Quaternion ReadQuaternion(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() < 4)
            return Quaternion.Identity;
        return new Quaternion(
            (float)element[0].GetDouble(),
            (float)element[1].GetDouble(),
            (float)element[2].GetDouble(),
            (float)element[3].GetDouble());
    }

    private static Vector3[] ReadVector3Array(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.Array)
            return Array.Empty<Vector3>();

        var result = new List<Vector3>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 3)
                result.Add(new Vector3(
                    (float)item[0].GetDouble(),
                    (float)item[1].GetDouble(),
                    (float)item[2].GetDouble()));
        }
        return result.ToArray();
    }

    private static Quaternion[] ReadQuaternionArray(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.Array)
            return Array.Empty<Quaternion>();

        var result = new List<Quaternion>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 4)
                result.Add(new Quaternion(
                    (float)item[0].GetDouble(),
                    (float)item[1].GetDouble(),
                    (float)item[2].GetDouble(),
                    (float)item[3].GetDouble()));
        }
        return result.ToArray();
    }
}
