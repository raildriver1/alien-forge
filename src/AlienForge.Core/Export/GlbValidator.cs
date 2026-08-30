using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace AlienForge.Core.Export;

/// <summary>
/// Reads a written .glb back and checks it hangs together.
/// </summary>
/// <remarks>
/// Exists so that "file written" is not mistaken for "file valid". Catches the
/// mistakes this exporter could plausibly make: wrong chunk lengths, missing
/// padding, and accessors that point past the end of their buffer view.
/// </remarks>
public static class GlbValidator
{
    public sealed class Report
    {
        public bool Ok => Problems.Count == 0;
        public List<string> Problems { get; } = new();
        public int Meshes { get; set; }
        public int Primitives { get; set; }
        public int Accessors { get; set; }
        public int BufferViews { get; set; }
        public int Images { get; set; }
        public int Materials { get; set; }
        public int BinaryBytes { get; set; }
        public int Nodes { get; set; }
        public int Joints { get; set; }
        public int Animations { get; set; }
        public int Channels { get; set; }

        public override string ToString()
            => Ok
                ? $"валиден: мешей {Meshes}, примитивов {Primitives}, аксессоров {Accessors}, " +
                  $"буферных представлений {BufferViews}, изображений {Images}, " +
                  $"материалов {Materials}, узлов {Nodes}, костей {Joints}, " +
                  $"анимаций {Animations}, каналов {Channels}, " +
                  $"бинарных данных {BinaryBytes / 1048576.0:F2} МБ"
                : $"проблем {Problems.Count}: {string.Join("; ", Problems.Take(6))}";
    }

    private static readonly int[] ComponentSizes =
    {
        // indexed by componentType - 5120
        1, // 5120 BYTE
        1, // 5121 UNSIGNED_BYTE
        2, // 5122 SHORT
        2, // 5123 UNSIGNED_SHORT
        0, // 5124 unused
        4, // 5125 UNSIGNED_INT
        4, // 5126 FLOAT
    };

    private static int ComponentCount(string type) => type switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        "MAT2" => 4,
        "MAT3" => 9,
        "MAT4" => 16,
        _ => 0,
    };

    public static Report Validate(string path)
    {
        var report = new Report();
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            report.Problems.Add($"не читается: {ex.Message}");
            return report;
        }

        if (bytes.Length < 20)
        {
            report.Problems.Add("файл короче заголовка GLB");
            return report;
        }
        if (Encoding.ASCII.GetString(bytes, 0, 4) != "glTF")
            report.Problems.Add("нет сигнатуры glTF");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        if (version != 2)
            report.Problems.Add($"версия {version}, ожидалась 2");
        if (declared != bytes.Length)
            report.Problems.Add($"в заголовке размер {declared}, на диске {bytes.Length}");

        int at = 12;
        byte[]? json = null;
        byte[]? binary = null;

        while (at + 8 <= bytes.Length)
        {
            int chunkLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
            string kind = Encoding.ASCII.GetString(bytes, at + 4, 4);
            int dataAt = at + 8;
            if (chunkLength < 0 || dataAt + chunkLength > bytes.Length)
            {
                report.Problems.Add($"кусок '{kind.Trim()}' выходит за пределы файла");
                break;
            }
            if (chunkLength % 4 != 0)
                report.Problems.Add($"кусок '{kind.Trim()}' не выровнен по 4 байтам");

            if (kind == "JSON")
                json = bytes[dataAt..(dataAt + chunkLength)];
            else if (kind.StartsWith("BIN"))
                binary = bytes[dataAt..(dataAt + chunkLength)];

            at = dataAt + chunkLength;
        }

        if (json is null)
        {
            report.Problems.Add("нет куска JSON");
            return report;
        }
        report.BinaryBytes = binary?.Length ?? 0;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            report.Problems.Add($"JSON не разбирается: {ex.Message}");
            return report;
        }

        using (document)
        {
            var root = document.RootElement;

            int bufferLength = 0;
            if (root.TryGetProperty("buffers", out var buffers) && buffers.GetArrayLength() > 0)
                bufferLength = buffers[0].GetProperty("byteLength").GetInt32();
            if (binary is not null && bufferLength != binary.Length)
                report.Problems.Add(
                    $"buffer.byteLength {bufferLength} против куска BIN {binary.Length}");

            var viewRanges = new List<(int offset, int length)>();
            if (root.TryGetProperty("bufferViews", out var views))
            {
                report.BufferViews = views.GetArrayLength();
                foreach (var view in views.EnumerateArray())
                {
                    int offset = view.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
                    int length = view.GetProperty("byteLength").GetInt32();
                    viewRanges.Add((offset, length));
                    if (offset < 0 || length < 0 || offset + length > bufferLength)
                        report.Problems.Add(
                            $"буферное представление {offset}+{length} выходит за буфер {bufferLength}");
                }
            }

            var accessorInfo = new List<(int count, string type, bool hasBounds)>();
            if (root.TryGetProperty("accessors", out var accessors))
            {
                report.Accessors = accessors.GetArrayLength();
                int index = 0;
                foreach (var accessor in accessors.EnumerateArray())
                {
                    accessorInfo.Add((
                        accessor.GetProperty("count").GetInt32(),
                        accessor.GetProperty("type").GetString() ?? "",
                        accessor.TryGetProperty("min", out _) && accessor.TryGetProperty("max", out _)));
                    int viewIndex = accessor.TryGetProperty("bufferView", out var bv)
                        ? bv.GetInt32() : -1;
                    int count = accessor.GetProperty("count").GetInt32();
                    int componentType = accessor.GetProperty("componentType").GetInt32();
                    string type = accessor.GetProperty("type").GetString() ?? "";

                    int componentSize = componentType - 5120 >= 0
                                        && componentType - 5120 < ComponentSizes.Length
                        ? ComponentSizes[componentType - 5120]
                        : 0;
                    int elements = ComponentCount(type);
                    if (componentSize == 0 || elements == 0)
                    {
                        report.Problems.Add($"аксессор {index}: неизвестный тип {type}/{componentType}");
                    }
                    else if (viewIndex >= 0 && viewIndex < viewRanges.Count)
                    {
                        int needed = count * elements * componentSize;
                        if (needed > viewRanges[viewIndex].length)
                            report.Problems.Add(
                                $"аксессор {index}: нужно {needed} байт, в представлении " +
                                $"{viewRanges[viewIndex].length}");
                    }
                    else
                    {
                        report.Problems.Add($"аксессор {index}: неверный bufferView {viewIndex}");
                    }
                    index++;
                }
            }

            if (root.TryGetProperty("meshes", out var meshes))
            {
                report.Meshes = meshes.GetArrayLength();
                foreach (var mesh in meshes.EnumerateArray())
                    if (mesh.TryGetProperty("primitives", out var prims))
                        report.Primitives += prims.GetArrayLength();
            }
            if (root.TryGetProperty("images", out var images))
                report.Images = images.GetArrayLength();
            if (root.TryGetProperty("materials", out var materials))
                report.Materials = materials.GetArrayLength();

            if (!root.TryGetProperty("scenes", out var scenes) || scenes.GetArrayLength() == 0)
                report.Problems.Add("нет ни одной сцены");

            ValidateNodes(root, report);
            ValidateSkins(root, report, accessorInfo);
            ValidateAnimations(root, report, accessorInfo);
        }

        return report;
    }

    /// <summary>
    /// Node indices stay in range and the hierarchy is a forest. A bone claimed by two
    /// parents is the mistake a hand-built skeleton makes, and it turns into a mangled
    /// rig rather than a load error, so it is worth catching here.
    /// </summary>
    private static void ValidateNodes(JsonElement root, Report report)
    {
        if (!root.TryGetProperty("nodes", out var nodes))
            return;

        report.Nodes = nodes.GetArrayLength();
        var parentOf = new int[report.Nodes];
        Array.Fill(parentOf, -1);

        int index = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.TryGetProperty("children", out var children))
            {
                if (children.GetArrayLength() == 0)
                    report.Problems.Add($"узел {index}: пустой массив children");
                foreach (var child in children.EnumerateArray())
                {
                    int c = child.GetInt32();
                    if (c < 0 || c >= report.Nodes)
                    {
                        report.Problems.Add($"узел {index}: ребёнок {c} вне диапазона");
                        continue;
                    }
                    if (c == index)
                        report.Problems.Add($"узел {index} указан своим же ребёнком");
                    else if (parentOf[c] >= 0)
                        report.Problems.Add(
                            $"узел {c} есть у двух родителей: {parentOf[c]} и {index}");
                    else
                        parentOf[c] = index;
                }
            }
            index++;
        }
    }

    private static void ValidateSkins(JsonElement root, Report report,
        List<(int count, string type, bool hasBounds)> accessorInfo)
    {
        if (!root.TryGetProperty("skins", out var skins))
            return;

        int index = 0;
        foreach (var skin in skins.EnumerateArray())
        {
            if (!skin.TryGetProperty("joints", out var joints) || joints.GetArrayLength() == 0)
            {
                report.Problems.Add($"скин {index}: нет костей");
                index++;
                continue;
            }

            int jointCount = joints.GetArrayLength();
            report.Joints += jointCount;
            foreach (var joint in joints.EnumerateArray())
            {
                int j = joint.GetInt32();
                if (j < 0 || j >= report.Nodes)
                    report.Problems.Add($"скин {index}: кость ссылается на узел {j} вне диапазона");
            }

            if (skin.TryGetProperty("inverseBindMatrices", out var ibm))
            {
                int accessor = ibm.GetInt32();
                if (accessor < 0 || accessor >= accessorInfo.Count)
                {
                    report.Problems.Add($"скин {index}: неверный аксессор inverseBindMatrices");
                }
                else
                {
                    var info = accessorInfo[accessor];
                    if (info.count != jointCount)
                        report.Problems.Add(
                            $"скин {index}: матриц {info.count}, а костей {jointCount}");
                    if (info.type != "MAT4")
                        report.Problems.Add(
                            $"скин {index}: inverseBindMatrices типа {info.type}, нужен MAT4");
                }
            }
            index++;
        }
    }

    private static void ValidateAnimations(JsonElement root, Report report,
        List<(int count, string type, bool hasBounds)> accessorInfo)
    {
        if (!root.TryGetProperty("animations", out var animations))
            return;

        report.Animations = animations.GetArrayLength();
        int index = 0;
        foreach (var animation in animations.EnumerateArray())
        {
            bool hasSamplers = animation.TryGetProperty("samplers", out var samplers)
                               && samplers.ValueKind == JsonValueKind.Array;
            int samplerCount = hasSamplers ? samplers.GetArrayLength() : 0;

            if (samplerCount == 0)
                report.Problems.Add($"анимация {index}: нет сэмплеров");

            if (hasSamplers)
            {
                int samplerIndex = 0;
                foreach (var sampler in samplers.EnumerateArray())
                {
                    int input = sampler.GetProperty("input").GetInt32();
                    int output = sampler.GetProperty("output").GetInt32();

                    if (input < 0 || input >= accessorInfo.Count
                        || output < 0 || output >= accessorInfo.Count)
                    {
                        report.Problems.Add(
                            $"анимация {index}, сэмплер {samplerIndex}: аксессор вне диапазона");
                        samplerIndex++;
                        continue;
                    }

                    var inputInfo = accessorInfo[input];
                    var outputInfo = accessorInfo[output];

                    // The spec requires bounds on a sampler input; without them a player
                    // cannot tell how long the clip runs.
                    if (!inputInfo.hasBounds)
                        report.Problems.Add(
                            $"анимация {index}, сэмплер {samplerIndex}: у времени нет min/max");
                    if (inputInfo.type != "SCALAR")
                        report.Problems.Add(
                            $"анимация {index}, сэмплер {samplerIndex}: время типа {inputInfo.type}");
                    if (inputInfo.count != outputInfo.count)
                        report.Problems.Add(
                            $"анимация {index}, сэмплер {samplerIndex}: кадров {inputInfo.count}, " +
                            $"значений {outputInfo.count}");
                    samplerIndex++;
                }
            }

            if (animation.TryGetProperty("channels", out var channels))
            {
                report.Channels += channels.GetArrayLength();
                if (channels.GetArrayLength() == 0)
                    report.Problems.Add($"анимация {index}: нет каналов");

                foreach (var channel in channels.EnumerateArray())
                {
                    int sampler = channel.GetProperty("sampler").GetInt32();
                    if (sampler < 0 || sampler >= samplerCount)
                        report.Problems.Add(
                            $"анимация {index}: канал ссылается на сэмплер {sampler}");

                    var target = channel.GetProperty("target");
                    string path = target.TryGetProperty("path", out var p)
                        ? p.GetString() ?? "" : "";
                    if (path is not ("translation" or "rotation" or "scale" or "weights"))
                        report.Problems.Add($"анимация {index}: неизвестный путь '{path}'");

                    if (target.TryGetProperty("node", out var n))
                    {
                        int node = n.GetInt32();
                        if (node < 0 || node >= report.Nodes)
                            report.Problems.Add(
                                $"анимация {index}: канал указывает на узел {node} вне диапазона");
                    }
                }
            }
            index++;
        }
    }
}
