using System.Diagnostics;
using AlienForge.Core.Animation;

namespace AlienForge.Core.Ui;

/// <summary>
/// DATA/UI.PAK — интерфейс игры: Scaleform-ролики (.GFX, тот же SWF с заголовком
/// «GFX»), их DDS-атласы и пара HTML. Ролики читаются JPEXS Free Flash Decompiler,
/// в них анимации интерфейса лежат как спрайты (DefineSprite) — кадры выгружаются
/// PNG-последовательностями. Архив — обычный PAK2, читается тем же индексом, что
/// и ANIMATION.PAK.
/// </summary>
public sealed class UiArchive
{
    private readonly Pak2Index _pak;

    private UiArchive(Pak2Index pak) => _pak = pak;

    public IReadOnlyList<Pak2Entry> Entries => _pak.Entries;
    public string Path => _pak.Path;

    public static string PakPath(GameInstall install) => System.IO.Path.Combine(install.DataDir, "UI.PAK");

    public static UiArchive Open(GameInstall install)
    {
        string path = PakPath(install);
        if (!File.Exists(path))
            throw new FileNotFoundException("UI.PAK не найден", path);
        return new UiArchive(Pak2Index.Open(path));
    }

    /// <summary>Только Scaleform-ролики (*.GFX).</summary>
    public IEnumerable<Pak2Entry> Movies
        => Entries.Where(e => e.Name.EndsWith(".GFX", StringComparison.OrdinalIgnoreCase));

    public IEnumerable<Pak2Entry> Find(string? filter)
        => filter is null or "" ? Entries
           : Entries.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

    /// <summary>Запись по имени: полное имя, либо имя файла без папок.</summary>
    public Pak2Entry? Resolve(string name)
    {
        name = name.Replace('\\', '/');
        return Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? Entries.FirstOrDefault(e => Leaf(e.Name).Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? Entries.FirstOrDefault(e => Leaf(e.Name).Equals(name + ".GFX", StringComparison.OrdinalIgnoreCase))
               ?? Entries.FirstOrDefault(e => Leaf(e.Name).Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    public static string Leaf(string name)
    {
        int cut = name.LastIndexOfAny(new[] { '/', '\\' });
        return cut < 0 ? name : name[(cut + 1)..];
    }

    public byte[] Read(Pak2Entry entry)
    {
        using var stream = File.OpenRead(_pak.Path);
        stream.Position = entry.Offset;
        var buffer = new byte[entry.Length];
        int read = 0;
        while (read < buffer.Length)
        {
            int got = stream.Read(buffer, read, buffer.Length - read);
            if (got <= 0) break;
            read += got;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>Пишет запись в папку, возвращает путь к файлу.</summary>
    public string Extract(Pak2Entry entry, string outDir, bool keepFolders = false)
    {
        string rel = keepFolders ? entry.Name.Replace('/', System.IO.Path.DirectorySeparatorChar) : Leaf(entry.Name);
        string target = System.IO.Path.Combine(outDir, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, Read(entry));
        return target;
    }

    /// <summary>Папка для распакованных .GFX — JPEXS работает с файлами на диске.</summary>
    public static string CacheDir
        => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlienForge", "ui");

    private bool _ddsCached;

    /// <summary>
    /// Распаковывает запись в кэш (со структурой папок архива) и возвращает путь.
    /// Ролики .GFX ссылаются на внешние картинки (DefineExternalImage → *_I29.DDS
    /// из того же архива), JPEXS ищет их рядом с файлом — поэтому вместе с первым
    /// роликом в кэш выкладываются все DDS архива.
    /// </summary>
    public string EnsureCached(Pak2Entry entry)
    {
        string target = CachePathOf(entry);
        if (!File.Exists(target) || new FileInfo(target).Length != entry.Length)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, Read(entry));
        }
        if (!_ddsCached && entry.Name.EndsWith(".GFX", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var dds in Entries.Where(e => e.Name.EndsWith(".DDS", StringComparison.OrdinalIgnoreCase)))
            {
                string t = CachePathOf(dds);
                if (File.Exists(t) && new FileInfo(t).Length == dds.Length) continue;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(t)!);
                File.WriteAllBytes(t, Read(dds));
            }
            _ddsCached = true;
        }
        return target;
    }

    public static string CachePathOf(Pak2Entry entry)
        => System.IO.Path.Combine(CacheDir, entry.Name.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>Папка, куда JPEXS рендерит кадры и спрайты ролика (кэш предпросмотра).</summary>
    public static string RenderDirOf(Pak2Entry entry)
        => System.IO.Path.Combine(CacheDir, "render", System.IO.Path.GetFileNameWithoutExtension(Leaf(entry.Name)));

    /// <summary>Заголовок SWF/GFX: размер сцены, частота кадров, число кадров.</summary>
    public static (int width, int height, double fps, int frames)? ReadMovieHeader(byte[] data)
    {
        if (data.Length < 12) return null;
        string sig = System.Text.Encoding.ASCII.GetString(data, 0, 3);
        byte[] body;
        if (sig is "FWS" or "GFX") body = data;
        else if (sig is "CWS" or "CFX")
        {
            try
            {
                using var zin = new System.IO.Compression.ZLibStream(new MemoryStream(data, 8, data.Length - 8), System.IO.Compression.CompressionMode.Decompress);
                using var ms = new MemoryStream();
                ms.Write(data, 0, 8);
                zin.CopyTo(ms);
                body = ms.ToArray();
            }
            catch { return null; }
        }
        else return null;

        int pos = 8;
        int nbits = body[pos] >> 3;
        long bitPos = (long)pos * 8 + 5;
        long ReadBits(int n)
        {
            long v = 0;
            for (int i = 0; i < n; i++)
            {
                int b = (body[bitPos / 8] >> (7 - (int)(bitPos % 8))) & 1;
                v = (v << 1) | (uint)b;
                bitPos++;
            }
            if (n > 0 && (v & (1L << (n - 1))) != 0) v -= 1L << n; // знаковое
            return v;
        }
        long xmin = ReadBits(nbits), xmax = ReadBits(nbits), ymin = ReadBits(nbits), ymax = ReadBits(nbits);
        pos = (int)((bitPos + 7) / 8);
        if (pos + 4 > body.Length) return null;
        double fps = body[pos + 1] + body[pos] / 256.0;
        int frames = body[pos + 2] | (body[pos + 3] << 8);
        return ((int)((xmax - xmin) / 20), (int)((ymax - ymin) / 20), fps, frames);
    }
}

/// <summary>
/// JPEXS Free Flash Decompiler (ffdec): поиск установки, открытие ролика в окне
/// декомпилятора и пакетная выгрузка спрайтов/кадров через ffdec-cli.
/// </summary>
public static class Jpexs
{
    /// <summary>Путь к ffdec.exe (GUI) или null.</summary>
    public static string? FindGui() => Find("ffdec.exe", "ffdec.bat");

    /// <summary>Путь к ffdec-cli.exe (консоль) или null; годится и GUI-exe с аргументами.</summary>
    public static string? FindCli() => Find("ffdec-cli.exe", "ffdec-cli.bat", "ffdec.exe", "ffdec.bat");

    private static string? Find(params string[] names)
    {
        var dirs = new List<string>();
        void Add(string? d) { if (!string.IsNullOrEmpty(d)) dirs.Add(d); }
        Add(Environment.GetEnvironmentVariable("FFDEC_HOME"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "FFDec"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FFDec"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FFDec"));
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Add(desktop);
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(desktop))
                if (Path.GetFileName(sub).Contains("ffdec", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(sub).Contains("jpexs", StringComparison.OrdinalIgnoreCase))
                    Add(sub);
        }
        catch { }
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            Add(p);

        foreach (var dir in dirs)
            foreach (var name in names)
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>Открывает ролик в окне JPEXS.</summary>
    public static Process? Open(string swfPath)
    {
        string? exe = FindGui();
        if (exe is null) return null;
        return Process.Start(new ProcessStartInfo(exe, Quote(swfPath)) { UseShellExecute = true });
    }

    public static string DefaultItems = "sprite,image,shape,frame";

    /// <summary>
    /// ffdec-cli -format sprite:png,frame:png -export &lt;items&gt; &lt;outDir&gt; &lt;swf&gt;.
    /// items: sprite (спрайты — кадры PNG-последовательностями), frame (кадры
    /// главной сцены), image (растровые), shape (векторные, SVG), script, font,
    /// text, all. Возвращает (код, вывод).
    /// </summary>
    public static (int code, string output) ExportItems(string swfPath, string outDir, string? items = null,
        Action<string>? progress = null, int timeoutMs = 600_000)
    {
        string? exe = FindCli();
        if (exe is null) return (-1, "JPEXS (ffdec) не найден");
        Directory.CreateDirectory(outDir);
        string args = $"-format sprite:png,frame:png -export {items ?? DefaultItems} {Quote(outDir)} {Quote(swfPath)}";
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        var log = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { log.AppendLine(e.Data); progress?.Invoke(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(); } catch { }
            return (-2, log + "\nтайм-аут");
        }
        return (process.ExitCode, log.ToString());
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
