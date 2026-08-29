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

        public override string ToString()
            => Ok
                ? $"валиден: мешей {Meshes}, примитивов {Primitives}, аксессоров {Accessors}, " +
                  $"буферных представлений {BufferViews}, изображений {Images}, " +
                  $"материалов {Materials}, бинарных данных {BinaryBytes / 1048576.0:F2} МБ"
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

            if (root.TryGetProperty("accessors", out var accessors))
            {
                report.Accessors = accessors.GetArrayLength();
                int index = 0;
                foreach (var accessor in accessors.EnumerateArray())
                {
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
        }

        return report;
    }
}
