using AlienForge.Core;
using AlienForge.Core.Animation;
using AlienForge.Core.Export;
using AlienForge.Core.Mesh;
using AlienForge.Core.Imaging;

namespace AlienForge.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        try
        {
            return command switch
            {
                "detect" => CmdDetect(),
                "levels" => CmdLevels(Arg(args, 1)),
                "models" => CmdModels(Arg(args, 1), Arg(args, 2), Arg(args, 3)),
                "inspect" => CmdInspect(Arg(args, 1), Arg(args, 2), Arg(args, 3)),
                "textures" => CmdTextures(Arg(args, 1), Arg(args, 2), Arg(args, 3), Arg(args, 4),
                    Arg(args, 5)),
                "wictest" => CmdWicTest(Arg(args, 1), Arg(args, 2), Arg(args, 3)),
                "tree" => CmdTree(Arg(args, 1), Arg(args, 2), Arg(args, 3), Arg(args, 4)),
                "deps" => CmdDeps(Arg(args, 1), Arg(args, 2), Arg(args, 3)),
                "export" => CmdExport(Arg(args, 1), Arg(args, 2), Arg(args, 3), Arg(args, 4)),
                "extract" => CmdExtract(Arg(args, 1), Arg(args, 2), Arg(args, 3), Arg(args, 4)),
                "anims" => CmdAnims(Arg(args, 1), Arg(args, 2), Arg(args, 3)),
                _ => CmdHelp(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ОШИБКА: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static string? Arg(string[] args, int index)
        => args.Length > index && args[index].Length > 0 ? args[index] : null;

    private static int CmdHelp()
    {
        Console.WriteLine("alienforge — извлечение ассетов Alien: Isolation");
        Console.WriteLine();
        Console.WriteLine("  detect                              найти установленную игру");
        Console.WriteLine("  levels [корень]                     перечислить уровни");
        Console.WriteLine("  models <уровень> [фильтр] [корень]  перечислить модели уровня");
        Console.WriteLine("  inspect <уровень> <путь> [корень]   разобрать модель и проверить геометрию");
        Console.WriteLine("  textures <уровень> <путь> <вывод> [эталон] [корень]");
        Console.WriteLine("                                      извлечь текстуры модели в PNG");
        Console.WriteLine("  tree <уровень> [фильтр] [глубина] [корень]");
        Console.WriteLine("                                      дерево содержимого архива");
        Console.WriteLine("  deps <уровень> <путь> [корень]      дерево зависимостей модели");
        Console.WriteLine("  export <уровень> <путь> <файл.glb> [корень]");
        Console.WriteLine("                                      экспорт модели с текстурами в .glb");
        Console.WriteLine("  extract <уровень> <папка> [фильтр] [корень]");
        Console.WriteLine("                                      пакетный экспорт моделей уровня");
        Console.WriteLine();
        Console.WriteLine("Пример:");
        Console.WriteLine("  alienforge models ENG_ALIEN_NEST alien");
        Console.WriteLine("  alienforge deps ENG_ALIEN_NEST CHARACTERS\\ALIEN\\model0.cs2");
        Console.WriteLine("  alienforge export ENG_ALIEN_NEST CHARACTERS\\ALIEN\\model0.cs2 out\\alien.glb");
        Console.WriteLine("  alienforge extract ENG_ALIEN_NEST out\\nest characters");
        return 0;
    }

    // ------------------------------------------------------------- resolution
    private static GameInstall? Resolve(string? root)
    {
        GameInstall? install = root is null ? GameInstall.AutoDetect() : GameInstall.Open(root);
        if (install is null)
        {
            Console.Error.WriteLine("Игра не найдена. Укажите путь к папке с игрой последним аргументом.");
            return null;
        }
        string? problem = install.Validate();
        if (problem is not null)
        {
            Console.Error.WriteLine(problem);
            return null;
        }
        return install;
    }

    private static LevelRef? ResolveLevel(GameInstall install, string wanted)
    {
        var levels = install.EnumerateLevels();
        var exact = levels.FirstOrDefault(l =>
            string.Equals(l.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var partial = levels.Where(l =>
            l.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1)
            return partial[0];

        if (partial.Count > 1)
        {
            Console.Error.WriteLine($"Неоднозначно '{wanted}', подходят:");
            foreach (var l in partial)
                Console.Error.WriteLine($"   {l.Name}");
            return null;
        }

        Console.Error.WriteLine($"Уровень '{wanted}' не найден. Доступные:");
        foreach (var l in levels)
            Console.Error.WriteLine($"   {l.Name}");
        return null;
    }

    // ---------------------------------------------------------------- detect
    private static int CmdDetect()
    {
        var candidates = GameInstall.FindCandidates();
        Console.WriteLine($"найдено путей-кандидатов: {candidates.Count}");
        foreach (string c in candidates)
        {
            var g = GameInstall.Open(c);
            string? problem = g.Validate();
            Console.WriteLine($"  [{(problem is null ? "ok" : "нет")}] {c}");
            if (problem is not null)
                Console.WriteLine($"        {problem}");
        }

        var install = GameInstall.AutoDetect();
        if (install is null)
        {
            Console.WriteLine("Игра не найдена. Укажите путь вручную.");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine($"выбрано: {install.Root}");
        Console.WriteLine($"  GLOBAL_TEXTURES.ALL.PAK : {FileNote(install.GlobalTexturesPak)}");
        Console.WriteLine($"  ANIMATION.PAK           : {FileNote(install.AnimationPak)}");
        Console.WriteLine($"  уровней                 : {install.EnumerateLevels().Count}");
        return 0;
    }

    private static int CmdLevels(string? root)
    {
        var install = Resolve(root);
        if (install is null)
            return 2;

        var levels = install.EnumerateLevels();
        Console.WriteLine($"корень : {install.Root}");
        Console.WriteLine($"уровней: {levels.Count}");
        foreach (var lv in levels)
            Console.WriteLine($"  {lv.Name,-34} {(lv.Compressed ? "сжатый" : "обычный"),-8} " +
                              $"models={FileNote(lv.ModelsPak),-10} textures={FileNote(lv.TexturesPak)}");
        return 0;
    }

    // ---------------------------------------------------------------- models
    private static int CmdModels(string? levelName, string? filter, string? root)
    {
        if (levelName is null)
        {
            Console.Error.WriteLine("Укажите уровень: alienforge models <уровень> [фильтр]");
            return 2;
        }
        var install = Resolve(root);
        if (install is null)
            return 2;
        var level = ResolveLevel(install, levelName);
        if (level is null)
            return 2;

        var ws = AssetWorkspace.OpenLevel(install, level,
            onProgress: stage => Console.WriteLine($"  ... {stage}"));
        foreach (string line in ws.Log)
            Console.WriteLine("  " + line);
        Console.WriteLine($"загружено за {ws.LoadTime.TotalSeconds:F1} с");

        if (!ws.HasModels)
        {
            Console.Error.WriteLine("В уровне нет моделей (архив не открылся?).");
            return 3;
        }

        var matches = filter is null
            ? ws.Models.Entries.ToList()
            : ws.FindModels(filter).ToList();

        Console.WriteLine();
        Console.WriteLine($"моделей: {matches.Count} из {ws.ModelCount}" +
                          (filter is null ? "" : $" (фильтр '{filter}')"));
        foreach (var cs2 in matches.Take(400))
        {
            int subs = ws.SubmeshCount(cs2);
            Console.WriteLine($"  {cs2.Name,-64} компонентов={cs2.Components.Count,-3} сабмешей={subs}");
        }
        if (matches.Count > 400)
            Console.WriteLine($"  ... и ещё {matches.Count - 400}");
        return 0;
    }

    // --------------------------------------------------------------- inspect
    private static int CmdInspect(string? levelName, string? modelPath, string? root)
    {
        if (levelName is null || modelPath is null)
        {
            Console.Error.WriteLine("Нужны уровень и путь модели: alienforge inspect <уровень> <путь>");
            return 2;
        }
        var install = Resolve(root);
        if (install is null)
            return 2;
        var level = ResolveLevel(install, levelName);
        if (level is null)
            return 2;

        var ws = AssetWorkspace.OpenLevel(install, level);
        foreach (string line in ws.Log)
            Console.WriteLine("  " + line);

        var model = ws.FindModelByPath(modelPath) ?? ws.FindModels(modelPath).FirstOrDefault();
        if (model is null)
        {
            Console.Error.WriteLine($"Модель '{modelPath}' не найдена в {level.Name}.");
            return 3;
        }

        Console.WriteLine();
        Console.WriteLine($"модель: {model.Name}");
        Console.WriteLine($"компонентов: {model.Components.Count}");

        var meshes = MeshDecoder.DecodeAll(model);
        Console.WriteLine();
        Console.WriteLine($"{"часть",-22} {"материал",-26} {"верт",-7} {"трис",-7} {"скин",-5} длинРёбер");
        int totalVerts = 0, totalTris = 0;
        foreach (var m in meshes)
        {
            totalVerts += m.VertexCount;
            totalTris += m.TriangleCount;
            Console.WriteLine($"{Trim(m.Name, 22),-22} {Trim(m.MaterialName ?? "-", 26),-26} " +
                              $"{m.VertexCount,-7} {m.TriangleCount,-7} {(m.IsSkinned ? "да" : "нет"),-5} " +
                              $"{m.LongEdgeRatio():F2}%");
            foreach (string w in m.Warnings)
                Console.WriteLine($"    ! {w}");
        }

        Console.WriteLine();
        Console.WriteLine($"итого: частей={meshes.Count} вершин={totalVerts} треугольников={totalTris}");

        // LOD 0 only, which is what an export would take
        var lod0 = meshes.Where(m => m.LodIndex == 0).ToList();
        Console.WriteLine($"LOD 0: частей={lod0.Count} вершин={lod0.Sum(m => m.VertexCount)} " +
                          $"треугольников={lod0.Sum(m => m.TriangleCount)}");

        Console.WriteLine();
        var mats = ws.MaterialsOf(model);
        Console.WriteLine($"материалов: {mats.Count}");
        foreach (var mat in mats)
            Console.WriteLine($"  {mat.Name,-40} текстур={mat.TextureReferences.Count} " +
                              $"envMap={mat.EnvironmentMapIndex} шейдер={(mat.Shader is null ? "нет" : "есть")}");

        var textures = ws.TexturesOf(model);
        Console.WriteLine();
        Console.WriteLine($"текстур: {textures.Count}");
        foreach (var use in textures)
        {
            var pick = use.Texture.TextureStreamed?.Content is { Length: > 0 }
                ? use.Texture.TextureStreamed
                : use.Texture.TexturePersistent;
            string size = pick is null ? "-" : $"{pick.Width}x{pick.Height} mips={pick.MipLevels}";
            int bytes = pick?.Content?.Length ?? 0;
            Console.WriteLine($"  [{use.Source}] слот{use.Slot,-2} {Trim(use.Name, 46),-46} " +
                              $"{use.Texture.Format,-10} {size,-20} {bytes / 1024.0:F0} КБ");
        }
        return 0;
    }

    // -------------------------------------------------------------- textures
    private static int CmdTextures(string? levelName, string? modelPath, string? outDir,
        string? refDir, string? root)
    {
        if (levelName is null || modelPath is null || outDir is null)
        {
            Console.Error.WriteLine("Нужны уровень, путь модели и папка вывода.");
            return 2;
        }
        var install = Resolve(root);
        if (install is null)
            return 2;
        var level = ResolveLevel(install, levelName);
        if (level is null)
            return 2;

        var ws = AssetWorkspace.OpenLevel(install, level);
        var model = ws.FindModelByPath(modelPath) ?? ws.FindModels(modelPath).FirstOrDefault();
        if (model is null)
        {
            Console.Error.WriteLine($"Модель '{modelPath}' не найдена в {level.Name}.");
            return 3;
        }

        Directory.CreateDirectory(outDir);
        var uses = ws.TexturesOf(model);
        Console.WriteLine($"модель  : {model.Name}");
        Console.WriteLine($"текстур : {uses.Count}");
        if (refDir is not null)
            Console.WriteLine($"эталон  : {refDir}");
        Console.WriteLine();

        int ok = 0, failed = 0, compared = 0, identical = 0, identicalRgb = 0;
        foreach (var use in uses)
        {
            var result = TextureDecoder.Decode(use.Texture);
            if (!result.Ok)
            {
                failed++;
                Console.WriteLine($"  ПРОВАЛ {use.Name}");
                Console.WriteLine($"         {result.Error}");
                continue;
            }

            var image = result.Image!;
            string fileName = AssetNaming.TextureFileName(use.Name, image.Width, image.Height);
            string outPath = Path.Combine(outDir, fileName);
            PngEncoder.Save(image, outPath);
            ok++;

            string note = $"{result.Format,-10} {image.Width}x{image.Height} " +
                          $"{result.Source,-10} мип{result.Mip.Level}";

            if (refDir is not null)
            {
                string refPath = Path.Combine(refDir, fileName);
                if (File.Exists(refPath))
                {
                    var reference = WicDecoder.TryDecodeFile(refPath, out string? refError);
                    if (reference is null)
                    {
                        note += $"  эталон не читается: {refError}";
                    }
                    else
                    {
                        var diff = ImageDiff.Compare(image, reference);
                        compared++;
                        if (diff.Identical)
                            identical++;
                        if (diff.IdenticalIgnoringAlpha)
                            identicalRgb++;
                        note += $"  сверка: {diff}";
                        if (!diff.IdenticalIgnoringAlpha)
                            note += Environment.NewLine + "         "
                                 + TextureDecoder.DescribeBc7Mismatch(use.Texture, image, reference);
                    }
                }
                else
                {
                    note += "  эталона нет";
                }
            }

            Console.WriteLine($"  ok     {Trim(use.Name, 46),-46} {note}");
        }

        Console.WriteLine();
        Console.WriteLine($"записано: {ok}, провалов: {failed}");
        if (compared > 0)
            Console.WriteLine($"сверено с эталоном: {compared}, полностью идентичных: {identical}, " +
                              $"идентичных по RGB: {identicalRgb}");
        return failed == 0 ? 0 : 4;
    }

    /// <summary>
    /// Feeds every texture of a model to the platform DDS decoder, including the
    /// formats this tool decodes itself. If a format we know is fine also fails,
    /// the DDS header is at fault; if only one format fails, the platform lacks it.
    /// </summary>
    private static int CmdWicTest(string? levelName, string? modelPath, string? root)
    {
        if (levelName is null || modelPath is null)
        {
            Console.Error.WriteLine("Нужны уровень и путь модели.");
            return 2;
        }
        var install = Resolve(root);
        if (install is null)
            return 2;
        var level = ResolveLevel(install, levelName);
        if (level is null)
            return 2;

        var ws = AssetWorkspace.OpenLevel(install, level);
        var model = ws.FindModelByPath(modelPath) ?? ws.FindModels(modelPath).FirstOrDefault();
        if (model is null)
        {
            Console.Error.WriteLine($"Модель '{modelPath}' не найдена.");
            return 3;
        }

        string dumpDir = Path.Combine(Path.GetTempPath(), "alienforge_dds");
        Directory.CreateDirectory(dumpDir);
        Console.WriteLine($"дампы DDS: {dumpDir}");
        Console.WriteLine();

        var byFormat = new Dictionary<string, (int ok, int fail)>();
        foreach (var use in ws.TexturesOf(model))
        {
            var (ok, error, w, h, dds) = TextureDecoder.ProbeWic(use.Texture);
            string fmt = use.Texture.Format.ToString();
            var tally = byFormat.TryGetValue(fmt, out var t) ? t : (0, 0);
            byFormat[fmt] = ok ? (tally.Item1 + 1, tally.Item2) : (tally.Item1, tally.Item2 + 1);

            Console.WriteLine($"  {(ok ? "ok    " : "ПРОВАЛ")} {fmt,-10} {w}x{h,-6} " +
                              $"{Trim(use.Name, 44)}");
            if (!ok)
                Console.WriteLine($"         {error}");

            if (dds is not null)
            {
                string ddsPath = Path.Combine(dumpDir,
                    AssetNaming.Flatten(use.Name) + $"_{w}x{h}.dds");
                if (!File.Exists(ddsPath))
                    File.WriteAllBytes(ddsPath, dds);
            }
        }

        Console.WriteLine();
        Console.WriteLine("по форматам:");
        foreach (var kv in byFormat.OrderBy(k => k.Key))
            Console.WriteLine($"  {kv.Key,-10} успешно={kv.Value.ok} провал={kv.Value.fail}");
        return 0;
    }

    // ------------------------------------------------------------------ trees
    private static int CmdTree(string? levelName, string? filter, string? depthText, string? root)
    {
        if (levelName is null)
        {
            Console.Error.WriteLine("Укажите уровень.");
            return 2;
        }
        var loaded = LoadLevel(levelName, root);
        if (loaded is null)
            return 2;

        int depth = int.TryParse(depthText, out int d) ? d : 3;
        var tree = AssetTree.BuildArchiveTree(loaded, filter);
        AssetTree.Print(tree, Console.WriteLine, depth);
        return 0;
    }

    private static int CmdDeps(string? levelName, string? modelPath, string? root)
    {
        if (levelName is null || modelPath is null)
        {
            Console.Error.WriteLine("Нужны уровень и путь модели.");
            return 2;
        }
        var loaded = LoadLevel(levelName, root);
        if (loaded is null)
            return 2;

        var model = loaded.FindModelByPath(modelPath) ?? loaded.FindModels(modelPath).FirstOrDefault();
        if (model is null)
        {
            Console.Error.WriteLine($"Модель '{modelPath}' не найдена.");
            return 3;
        }

        var tree = AssetTree.BuildDependencyTree(loaded, model);
        AssetTree.Print(tree, Console.WriteLine, maxDepth: 5);
        return 0;
    }

    // ----------------------------------------------------------------- export
    private static int CmdExport(string? levelName, string? modelPath, string? outPath, string? root)
    {
        if (levelName is null || modelPath is null || outPath is null)
        {
            Console.Error.WriteLine("Нужны уровень, путь модели и файл вывода .glb");
            return 2;
        }
        var loaded = LoadLevel(levelName, root);
        if (loaded is null)
            return 2;

        var model = loaded.FindModelByPath(modelPath) ?? loaded.FindModels(modelPath).FirstOrDefault();
        if (model is null)
        {
            Console.Error.WriteLine($"Модель '{modelPath}' не найдена.");
            return 3;
        }

        var result = ModelExporter.ExportGlb(loaded, model, outPath);
        Console.WriteLine($"записан {result.Path}");
        Console.WriteLine($"  {result.Bytes / 1048576.0:F2} МБ");
        Console.WriteLine($"  частей={result.Parts} вершин={result.Vertices} " +
                          $"треугольников={result.Triangles}");
        Console.WriteLine($"  материалов={result.Materials} изображений={result.Images} " +
                          $"скинованных частей={result.SkinnedParts}");
        foreach (string w in result.Warnings)
            Console.WriteLine($"  ! {w}");

        var report = GlbValidator.Validate(result.Path);
        Console.WriteLine($"  проверка: {report}");
        return report.Ok ? 0 : 4;
    }

    private static int CmdExtract(string? levelName, string? outDir, string? filter, string? root)
    {
        if (levelName is null || outDir is null)
        {
            Console.Error.WriteLine("Нужны уровень и папка вывода.");
            return 2;
        }
        var loaded = LoadLevel(levelName, root);
        if (loaded is null)
            return 2;

        var models = (filter is null
            ? loaded.Models.Entries.AsEnumerable()
            : loaded.FindModels(filter)).ToList();

        Console.WriteLine($"к экспорту: {models.Count} моделей -> {outDir}");
        int ok = 0, failed = 0;
        long bytes = 0;
        var started = DateTime.UtcNow;

        foreach (var model in models)
        {
            string relative = AssetNaming.RelativePath(model.Name ?? "model", ".glb");
            string target = Path.Combine(outDir, relative);
            try
            {
                var result = ModelExporter.ExportGlb(loaded, model, target);
                ok++;
                bytes += result.Bytes;
                Console.WriteLine($"  ok  {relative}  частей={result.Parts} " +
                                  $"трис={result.Triangles} {result.Bytes / 1024.0:F0} КБ");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  --  {relative}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"готово за {(DateTime.UtcNow - started).TotalSeconds:F1} с: " +
                          $"успешно {ok}, пропущено {failed}, всего {bytes / 1048576.0:F1} МБ");
        return 0;
    }

    // ------------------------------------------------------------- animations
    private static int CmdAnims(string? levelName, string? modelPath, string? root)
    {
        var install = Resolve(root);
        if (install is null)
            return 2;

        Console.WriteLine($"архив: {install.AnimationPak}");
        var started = DateTime.UtcNow;
        var catalog = AnimationCatalog.Open(install);
        Console.WriteLine($"записей в PAK2: {catalog.Archive.Entries.Count}, " +
                          $"скелетов: {catalog.SkeletonCount}, " +
                          $"индексация {(DateTime.UtcNow - started).TotalSeconds:F1} с");

        Console.WriteLine($"fnv1a(\"ALIEN\") = {AnimationCatalog.Fnv1a("ALIEN")} " +
                          $"(ожидалось 4020659628)");

        if (modelPath is null)
            return 0;

        Console.WriteLine();
        Console.WriteLine($"скелет для '{modelPath}': {catalog.DescribeSkeleton(modelPath)}");
        var clips = catalog.ClipsFor(modelPath);
        Console.WriteLine($"клипов: {clips.Count}");
        foreach (var clip in clips.Take(40))
            Console.WriteLine($"  {clip.Display}");
        if (clips.Count > 40)
            Console.WriteLine($"  ... и ещё {clips.Count - 40}");
        return 0;
    }

    /// <summary>Resolves the install and level, loads the archives, prints the log.</summary>
    private static AssetWorkspace? LoadLevel(string levelName, string? root)
    {
        var install = Resolve(root);
        if (install is null)
            return null;
        var level = ResolveLevel(install, levelName);
        if (level is null)
            return null;

        var ws = AssetWorkspace.OpenLevel(install, level);
        foreach (string line in ws.Log)
            Console.WriteLine("  " + line);
        if (!ws.HasModels)
        {
            Console.Error.WriteLine("В уровне нет моделей.");
            return null;
        }
        return ws;
    }

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s.Substring(s.Length - max);

    private static string FileNote(string path)
    {
        var fi = new FileInfo(path);
        return fi.Exists ? $"{fi.Length / 1048576.0:F1} МБ" : "нет";
    }
}
