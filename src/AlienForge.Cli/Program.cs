using System.Numerics;
using System.Text;
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
                "texfind" => CmdTexFind(Arg(args, 1), Arg(args, 2), Arg(args, 3)),
                "texraw" => CmdTexRaw(Arg(args, 1), Arg(args, 2), Arg(args, 3)),
                "wictest" => CmdWicTest(Arg(args, 1), Arg(args, 2), Arg(args, 3)),
                "tree" => CmdTree(Arg(args, 1), Arg(args, 2), Arg(args, 3), Arg(args, 4)),
                "deps" => CmdDeps(Arg(args, 1), Arg(args, 2), Arg(args, 3)),
                "export" => CmdExport(Arg(args, 1), Arg(args, 2), Arg(args, 3), Arg(args, 4),
                    Arg(args, 5)),
                "extract" => CmdExtract(Arg(args, 1), Arg(args, 2), Arg(args, 3), Arg(args, 4)),
                "anims" => CmdAnims(Arg(args, 1), Arg(args, 2), Arg(args, 3)),
                "pose" => CmdPose(Arg(args, 1), Arg(args, 2), Arg(args, 3), Arg(args, 4)),
                "pakls" => CmdPakList(Arg(args, 1), Arg(args, 2)),
                "pakget" => CmdPakGet(Arg(args, 1), Arg(args, 2), Arg(args, 3)),
                "bones" => CmdBones(Arg(args, 1), Arg(args, 2)),
                "names" => CmdNames(Arg(args, 1), Arg(args, 2)),
                "clips" => CmdClips(Arg(args, 1), Arg(args, 2), Arg(args, 3)),
                "animscan" => CmdAnimScan(Arg(args, 1), Arg(args, 2)),
                "guitest" => CmdGuiTest(Arg(args, 1), Arg(args, 2), Arg(args, 3), Arg(args, 4)),
                "rig" => CmdRig(Arg(args, 1), Arg(args, 2)),
                "rigs" => CmdRigs(Arg(args, 1)),
                "cliprows" => CmdClipRows(Arg(args, 1)),
                "texstats" => CmdTexStats(Arg(args, 1)),
                "shaderdump" => CmdShaderDump(Arg(args, 1), Arg(args, 2)),
                "material" => CmdMaterial(Arg(args, 1), Arg(args, 2), Arg(args, 3)),
                "level" => CmdLevel(args),
                "leveldiag" => CmdLevelDiag(Arg(args, 1)),
                "funcs" => CmdFuncs(Arg(args, 1), Arg(args, 2)),
                "enum" => CmdEnum(Arg(args, 1)),
                "paramstats" => CmdParamStats(Arg(args, 1), Arg(args, 2)),
                "comp" => CmdComp(Arg(args, 1), Arg(args, 2), args.Contains("--links")),
                "matfind" => CmdMatFind(Arg(args, 1), Arg(args, 2)),
                "ui" => CmdUi(args),
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
        Console.WriteLine("  pose <уровень> <путь> [корень] [номер клипа]");
        Console.WriteLine("                                      сверка систем координат меша и скелета");
        Console.WriteLine("  export <уровень> <путь> <файл.glb> [корень] [клипов]");
        Console.WriteLine("                                      [клипов]: 0 — только скелет и скин,");
        Console.WriteLine("                                      N — запечь ещё и N клипов");
        Console.WriteLine("                                      экспорт модели с текстурами в .glb");
        Console.WriteLine("  level <уровень> <файл.glb> [корень] [--no-textures] [--unlit] [--gray] [--no-decals]");
        Console.WriteLine("                                      экспорт всего уровня в glTF как в игре");
        Console.WriteLine("                                      (иерархия композитов, без окклюдеров/LOD)");
        Console.WriteLine("  ui list [фильтр] | ui extract <папка> [фильтр]");
        Console.WriteLine("  ui open <ролик.GFX> | ui sprites <ролик.GFX> <папка> [items]");
        Console.WriteLine("                                      интерфейс игры (UI.PAK): открыть ролик в JPEXS,");
        Console.WriteLine("                                      выгрузить спрайты PNG-последовательностями");
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
    /// <summary>texfind: текстуры уровня/глобальные по фрагменту имени → PNG (гобо, кукисы фонарика…).</summary>
    private static int CmdTexFind(string? levelName, string? filter, string? outDir)
    {
        if (levelName is null || filter is null) { Console.Error.WriteLine("Нужны уровень и фрагмент имени [папка вывода]."); return 2; }
        var install = Resolve(null);
        if (install is null) return 2;
        var level = ResolveLevel(install, levelName);
        if (level is null) return 2;
        var ws = AssetWorkspace.OpenLevel(install, level);
        int n = 0;
        foreach (var tex in ws.LevelTextures.Entries.Concat(ws.GlobalTextures.Entries))
        {
            string name = tex.Name ?? "";
            if (!name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var result = TextureDecoder.Decode(tex);
            Console.WriteLine($"  {name}  {(result.Ok ? $"{result.Format} {result.Image!.Width}x{result.Image.Height}" : "ПРОВАЛ " + result.Error)}");
            if (result.Ok && outDir is not null)
            {
                Directory.CreateDirectory(outDir);
                PngEncoder.Save(result.Image!, Path.Combine(outDir, Path.GetFileNameWithoutExtension(name) + ".png"));
            }
            n++;
        }
        Console.WriteLine($"найдено: {n}");
        return 0;
    }

    /// <summary>texraw: сырые байты текстур (объёмные LUT и т.п.) — имя, формат, размеры, длины в консоль, содержимое в файлы.</summary>
    private static int CmdTexRaw(string? levelName, string? filter, string? outDir)
    {
        if (levelName is null || filter is null || outDir is null) return 2;
        var install = Resolve(null);
        if (install is null) return 2;
        var level = ResolveLevel(install, levelName);
        if (level is null) return 2;
        var ws = AssetWorkspace.OpenLevel(install, level);
        Directory.CreateDirectory(outDir);
        foreach (var tex in ws.LevelTextures.Entries.Concat(ws.GlobalTextures.Entries))
        {
            string name = tex.Name ?? "";
            if (!name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var (tag, part) in new[] { ("persistent", tex.TexturePersistent), ("streamed", tex.TextureStreamed) })
            {
                if (part?.Content is null || part.Content.Length == 0) continue;
                Console.WriteLine($"  {name} [{tag}] {tex.Format} {part.Width}x{part.Height} bytes={part.Content.Length}");
                File.WriteAllBytes(Path.Combine(outDir, Path.GetFileNameWithoutExtension(name) + "." + tag + ".bin"), part.Content);
            }
        }
        return 0;
    }

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
    private static int CmdExport(string? levelName, string? modelPath, string? outPath, string? root,
        string? animSpec = null)
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

        AnimationBundle? bundle = null;
        if (animSpec is not null)
        {
            bundle = LoadBundleForExport(loaded, model.Name, animSpec, model);
            if (bundle is null)
                return 5;
        }

        var result = ModelExporter.ExportGlb(loaded, model, outPath, null, bundle);
        Console.WriteLine($"записан {result.Path}");
        Console.WriteLine($"  {result.Bytes / 1048576.0:F2} МБ");
        Console.WriteLine($"  частей={result.Parts} вершин={result.Vertices} " +
                          $"треугольников={result.Triangles}");
        Console.WriteLine($"  материалов={result.Materials} изображений={result.Images} " +
                          $"скинованных частей={result.SkinnedParts}");
        Console.WriteLine($"  костей={result.Bones} анимаций={result.Clips}");
        foreach (string w in result.Warnings)
            Console.WriteLine($"  ! {w}");

        var report = GlbValidator.Validate(result.Path);
        Console.WriteLine($"  проверка: {report}");
        return report.Ok ? 0 : 4;
    }

    /// <summary>
    /// Builds the skeleton, and optionally clips, to bake into an export.
    /// <paramref name="spec"/> is the number of clips to include; 0 writes the skin
    /// with no animation, which is the fast way to check the rig itself.
    /// </summary>
    private static AnimationBundle? LoadBundleForExport(AssetWorkspace ws, string? modelName,
        string spec, CATHODE.Models.CS2? model = null)
    {
        if (!int.TryParse(spec, out int wanted) || wanted < 0)
        {
            Console.Error.WriteLine(
                $"Не понял '{spec}': нужно число клипов для запекания (0 — только скелет).");
            return null;
        }

        var dumper = HavokDump.TryCreate();
        if (dumper is null)
        {
            Console.Error.WriteLine($"{HavokDump.ToolName} не найден, анимации распаковать нечем.");
            return null;
        }
        Console.WriteLine($"  распаковщик: {dumper.ToolPath}");

        var catalog = AnimationCatalog.Open(ws.Install);
        // риг подбирается как в GUI: по пути, по имени (RIPLEY_FP → FEMALEFP…) и по числу костей
        var guess = catalog.GuessSkeleton(modelName, model, dumper);
        var (autoId, _, autoEntry) = catalog.ResolveSkeleton(modelName);
        uint? force = guess.entry is not null && (autoEntry is null || guess.id != autoId) ? guess.id : null;
        if (force is not null) Console.WriteLine($"  риг: {guess.name} ({guess.bones} костей)");
        byte[]? skeletonHkx = catalog.ExtractSkeletonHavok(modelName, force);
        if (skeletonHkx is null)
        {
            Console.Error.WriteLine(
                $"Скелет для '{modelName}' не найден: {catalog.DescribeSkeleton(modelName)}");
            return null;
        }

        if (wanted == 0)
        {
            var skeletonOnly = dumper.LoadSkeletonOnly(skeletonHkx);
            Console.WriteLine($"  скелет: костей {skeletonOnly.Count}");
            return new AnimationBundle
            {
                Skeleton = skeletonOnly,
                Clips = new List<ClipData>(),
            };
        }

        var refs = catalog.ClipsFor(modelName, force);
        Console.WriteLine($"  контейнеров клипов: {refs.Count}, беру {Math.Min(wanted, refs.Count)}");

        SkeletonData? skeleton = null;
        var clips = new List<ClipData>();
        float fps = 30f;
        int failed = 0;

        foreach (var clipRef in refs.Take(wanted))
        {
            try
            {
                var loadedBundle = dumper.Load(catalog.ExtractHavok(clipRef), skeletonHkx);
                skeleton ??= loadedBundle.Skeleton;
                if (loadedBundle.Fps > 0f)
                    fps = loadedBundle.Fps;
                AnimationCatalog.ApplyNames(clipRef, loadedBundle); // настоящие имена из ANIM_STRING_DB
                clips.AddRange(loadedBundle.Clips);
                Console.WriteLine($"    {clipRef.ShortName}: клипов {loadedBundle.Clips.Count}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"    {clipRef.ShortName}: не распаковался — {ex.Message}");
            }
        }

        if (failed > 0)
            Console.WriteLine($"  не распаковалось контейнеров: {failed}");

        // Fall back to the skeleton on its own: a rig with no clips still exports a
        // usable skinned mesh.
        skeleton ??= dumper.LoadSkeletonOnly(skeletonHkx);
        Console.WriteLine($"  скелет: костей {skeleton.Count}, клипов собрано {clips.Count}");

        return new AnimationBundle { Skeleton = skeleton, Clips = clips, Fps = fps };
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

    // ------------------------------------------------------------------ pakls
    /// <summary>
    /// Entry names inside ANIMATION.PAK, grouped by prefix. Used to find what else the
    /// archive holds besides clip databases, such as a string table for real names.
    /// </summary>
    private static int CmdPakList(string? filter, string? root)
    {
        var install = Resolve(root);
        if (install is null)
            return 2;

        var catalog = AnimationCatalog.Open(install);
        var entries = catalog.Archive.Entries;
        Console.WriteLine($"записей: {entries.Count}");

        // Prefix up to the first digit run, so the thousands of numbered sections
        // collapse into one line each.
        var groups = new Dictionary<string, (int count, long bytes, string sample)>();
        foreach (var entry in entries)
        {
            string leaf = entry.Name.Replace('/', '\\');
            int slash = leaf.LastIndexOf('\\');
            if (slash >= 0)
                leaf = leaf[(slash + 1)..];

            var key = new StringBuilder();
            foreach (char c in leaf)
                key.Append(char.IsDigit(c) ? '#' : c);
            string prefix = key.ToString();

            if (groups.TryGetValue(prefix, out var existing))
                groups[prefix] = (existing.count + 1, existing.bytes + entry.Length, existing.sample);
            else
                groups[prefix] = (1, entry.Length, leaf);
        }

        Console.WriteLine($"различных шаблонов имён: {groups.Count}");
        foreach (var pair in groups.OrderByDescending(p => p.Value.count).Take(40))
            Console.WriteLine($"  {pair.Value.count,6} x {pair.Key,-48} " +
                              $"{pair.Value.bytes / 1048576.0,9:F2} МБ  напр. {pair.Value.sample}");

        if (!string.IsNullOrWhiteSpace(filter))
        {
            Console.WriteLine();
            Console.WriteLine($"совпадения с '{filter}':");
            int shown = 0;
            foreach (var entry in entries)
            {
                if (!entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                Console.WriteLine($"  {entry.Length,10} {entry.Name}");
                if (++shown >= 40)
                {
                    Console.WriteLine("  ...");
                    break;
                }
            }
            Console.WriteLine($"  всего совпало: {entries.Count(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))}");
        }
        return 0;
    }

    // ------------------------------------------------------------------ bones
    /// <summary>Reference skeleton for one character: bone names by body part.</summary>
    private static int CmdBones(string? character, string? root)
    {
        var install = Resolve(root);
        if (install is null)
            return 2;

        var catalog = AnimationCatalog.Open(install);
        if (string.IsNullOrWhiteSpace(character))
        {
            var all = ReferenceSkeletons.List(catalog.Archive);
            Console.WriteLine($"персонажей со референсным скелетом: {all.Count}");
            foreach (string name in all)
                Console.WriteLine($"  {name}");
            return 0;
        }

        var reference = ReferenceSkeletons.Load(catalog.Archive, character);
        if (reference is null)
        {
            Console.Error.WriteLine($"Референсный скелет '{character}' не найден.");
            return 3;
        }

        Console.WriteLine($"{reference.Character}: групп {reference.Groups.Count}, " +
                          $"уникальных костей {reference.TotalBones}, " +
                          $"зеркальных пар {Math.Min(reference.LeftBones.Count, reference.RightBones.Count)}");
        foreach (var group in reference.Groups)
        {
            Console.WriteLine($"  {group.Name} — костей {group.Bones.Count}, весов {group.Floats.Count}");
            foreach (string bone in group.Bones.Take(4))
                Console.WriteLine($"      {bone}");
            if (group.Bones.Count > 4)
                Console.WriteLine($"      ... ещё {group.Bones.Count - 4}");
        }
        return 0;
    }

    // --------------------------------------------------------------- animscan
    /// <summary>
    /// Reports, for every model in a level, whether a skeleton and clips were found.
    /// </summary>
    /// <remarks>
    /// Answers the question "why does only the Alien have animations" with counts
    /// instead of a guess: how many of the level's models resolve to a rig at all, how
    /// many of those rigs are in the archive, and which model paths fail. If most
    /// models are props then having no animation is correct; if characters are failing
    /// then the name guessing is at fault.
    /// </remarks>
    private static int CmdAnimScan(string? levelName, string? root)
    {
        if (levelName is null)
        {
            Console.Error.WriteLine("Нужно имя уровня.");
            return 2;
        }
        var ws = LoadLevel(levelName, root);
        if (ws is null)
            return 2;

        var catalog = AnimationCatalog.Open(ws.Install);
        Console.WriteLine($"скелетов в архиве: {catalog.SkeletonCount}");

        var models = ws.Models.Entries.ToList();
        Console.WriteLine($"моделей в уровне: {models.Count}");

        int resolved = 0, missing = 0, noCandidate = 0;
        var byRig = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (var model in models)
        {
            var (id, name, entry) = catalog.ResolveSkeleton(model.Name);
            if (name is null)
            {
                noCandidate++;
                continue;
            }
            if (entry is null)
            {
                missing++;
                if (failures.Count < 25)
                    failures.Add($"{name,-22} <- {model.Name}");
                continue;
            }
            resolved++;
            byRig[name] = byRig.GetValueOrDefault(name) + 1;
        }

        Console.WriteLine($"  скелет найден в архиве : {resolved}");
        Console.WriteLine($"  скелет назван, но его нет в архиве: {missing}");
        Console.WriteLine($"  ни одного кандидата имени: {noCandidate}");

        Console.WriteLine();
        Console.WriteLine($"скелеты, найденные по моделям уровня: {byRig.Count}");
        foreach (var pair in byRig.OrderByDescending(p => p.Value))
        {
            int clips = catalog.ClipsFor(
                models.First(m => string.Equals(
                    catalog.ResolveSkeleton(m.Name).name, pair.Key,
                    StringComparison.OrdinalIgnoreCase)).Name).Count;
            Console.WriteLine($"  {pair.Key,-22} моделей {pair.Value,4}  секций клипов {clips,4}");
        }

        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("имя предположено, но такого скелета в архиве нет:");
            foreach (string line in failures)
                Console.WriteLine($"  {line}");
        }

        // The other side of the question: rigs that do have clips, whether or not this
        // level uses them. If a character is missing here it is missing from the game
        // data, not from the guessing.
        var all = catalog.ListSkeletons();
        int withClips = all.Count(s => s.clips > 0);
        Console.WriteLine();
        Console.WriteLine($"всего скелетов: {all.Count}, из них с клипами: {withClips}, " +
                          $"с восстановленным именем: {catalog.SkeletonNames.Count}");
        Console.WriteLine("первые 30 по числу клипов:");
        foreach (var (id, name, clips) in all.Take(30))
            Console.WriteLine($"  {name,-28} id {id,12}  клипов {clips,5}");
        return 0;
    }

    // ------------------------------------------------------------------ clips
    /// <summary>
    /// Names every clip of one character and checks the naming against the clip data.
    /// </summary>
    private static int CmdClips(string? character, string? limitSpec, string? root)
    {
        var install = Resolve(root);
        if (install is null)
            return 2;

        string wanted = string.IsNullOrWhiteSpace(character) ? "ALIEN" : character.ToUpperInvariant();
        int limit = int.TryParse(limitSpec, out int parsed) && parsed > 0 ? parsed : 12;

        var catalog = AnimationCatalog.Open(install);
        var resolver = AnimNameResolver.Load(catalog.Archive);
        Console.WriteLine($"имён в таблицах строк: {resolver.NameCount}");

        uint skeletonId = AnimationCatalog.Fnv1a(wanted);
        var refs = catalog.ClipsFor($@"CHARACTERS\{wanted}\model0.cs2");
        if (refs.Count == 0)
        {
            Console.Error.WriteLine($"У скелета '{wanted}' (id {skeletonId}) нет секций.");
            return 3;
        }

        // Each section is stored twice, as a 32-bit and a 64-bit packfile: same name,
        // different offset, and the 64-bit copy is the larger of the two. Keeping one
        // copy per name is what turns 338 containers back into the 169 that exist.
        var unique = refs
            .GroupBy(r => r.ShortName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.Entry.Length).First())
            .ToList();
        Console.WriteLine($"секций: записей {refs.Count}, уникальных {unique.Count}");

        var sectionHashes = new HashSet<uint>();
        var byHash = new Dictionary<uint, AnimationClipRef>();
        foreach (var clipRef in unique)
        {
            string digits = clipRef.ShortName
                .Replace("ANIM_CLIP_DB_SEC_", "", StringComparison.OrdinalIgnoreCase)
                .Replace(".BIN", "", StringComparison.OrdinalIgnoreCase);
            if (uint.TryParse(digits, out uint h))
            {
                sectionHashes.Add(h);
                byHash[h] = clipRef;
            }
        }

        var index = AnimClipIndex.Load(catalog.Archive, resolver);
        Console.WriteLine($"клипов в игре: {index.ClipCount}, без имени {index.Unnamed}");

        var mine = new List<ClipNameEntry>();
        foreach (uint hash in sectionHashes)
            mine.AddRange(index.ForSection(hash));
        Console.WriteLine($"клипов у скелета {wanted}: {mine.Count}");

        var categories = mine
            .GroupBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();
        Console.WriteLine($"  папок: {categories.Count}, крупнейшие:");
        foreach (var group in categories.Take(8))
            Console.WriteLine($"    {group.Count(),4}  {group.Key}");

        var dumper = HavokDump.TryCreate();
        if (dumper is null)
        {
            Console.Error.WriteLine($"{HavokDump.ToolName} не найден.");
            return 5;
        }
        byte[]? skeletonHkx = catalog.ExtractSkeletonHavok($@"CHARACTERS\{wanted}\model0.cs2");
        if (skeletonHkx is null)
        {
            Console.Error.WriteLine("Скелет не найден.");
            return 5;
        }

        // Smallest sections first: they decode quickly, so more of the naming can be
        // checked in the same time. The structural test is the clip count: the index
        // says how many clips a section holds, and Havok says how many it really has.
        var toCheck = sectionHashes
            .Where(h => byHash.ContainsKey(h) && index.ForSection(h).Count > 0)
            .OrderBy(h => byHash[h].Entry.Length)
            .Take(limit)
            .ToList();

        Console.WriteLine();
        Console.WriteLine($"проверяю {toCheck.Count} секций (клипов по индексу против анимаций в данных):");

        var named = new List<(string name, ClipData clip)>();
        int matched = 0, mismatched = 0;
        foreach (uint hash in toCheck)
        {
            var clipRef = byHash[hash];
            var expected = index.ForSection(hash);
            List<ClipData> clips;
            try
            {
                clips = dumper.Load(catalog.ExtractHavok(clipRef), skeletonHkx).Clips;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {hash,12}: не распаковалась — {ex.Message}");
                continue;
            }

            bool ok = clips.Count == expected.Count;
            if (ok) matched++; else mismatched++;
            Console.WriteLine($"  {hash,12}: по индексу {expected.Count,3}, " +
                              $"в данных {clips.Count,3}, {clipRef.Entry.Length,8} байт  " +
                              $"{(ok ? "совпало" : "РАСХОДИТСЯ")}");

            AnimationCatalog.ApplyNames(clipRef, new AnimationBundle
            {
                Skeleton = new SkeletonData { Bones = new List<BoneData>() },
                Clips = clips,
            });

            for (int i = 0; i < Math.Min(clips.Count, expected.Count); i++)
            {
                named.Add((clips[i].Name, clips[i]));
                if (named.Count <= 24)
                    Console.WriteLine($"      {clips[i]}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"секций с совпавшим числом: {matched}, с расхождением: {mismatched}");
        if (named.Count > 0)
        {
            var check = ClipNameCheck.Evaluate(named);
            Console.WriteLine("семантическая проверка:");
            Console.WriteLine($"  {check}");
        }
        return 0;
    }

    // ------------------------------------------------------------------ names
    /// <summary>
    /// Recovers names for the hashes in the animation archive and reports which keying
    /// answered, so a table that follows a new convention is visible rather than silent.
    /// </summary>
    private static int CmdNames(string? character, string? root)
    {
        var install = Resolve(root);
        if (install is null)
            return 2;

        var catalog = AnimationCatalog.Open(install);
        var resolver = AnimNameResolver.Load(catalog.Archive);

        Console.WriteLine("таблицы строк:");
        foreach (string source in resolver.Sources)
            Console.WriteLine($"  {source}");
        Console.WriteLine($"имён всего: {resolver.NameCount}");

        // The simplest possibility first: the section file names carry the hash, so if
        // those resolve there is nothing else to join.
        var sections = catalog.Archive.Entries
            .Where(e => e.Name.Contains("ANIM_CLIP_DB_SEC_", StringComparison.OrdinalIgnoreCase))
            .Select(e =>
            {
                string leaf = e.Name[(e.Name.LastIndexOf('\\') + 1)..];
                string digits = leaf.Replace("ANIM_CLIP_DB_SEC_", "", StringComparison.OrdinalIgnoreCase)
                    .Replace(".BIN", "", StringComparison.OrdinalIgnoreCase);
                return uint.TryParse(digits, out uint h) ? h : 0u;
            })
            .Where(h => h != 0)
            .Distinct()
            .ToList();

        var byRoute = new Dictionary<AnimNameResolver.Route, int>();
        var examples = new List<string>();
        foreach (uint hash in sections)
        {
            var (name, route) = resolver.Resolve(hash);
            byRoute[route] = byRoute.GetValueOrDefault(route) + 1;
            if (name is not null && examples.Count < 10)
                examples.Add($"{hash} -> {name}");
        }
        Console.WriteLine();
        Console.WriteLine($"секций клипов (уникальных хешей): {sections.Count}");
        foreach (var pair in byRoute.OrderByDescending(p => p.Value))
            Console.WriteLine($"  {pair.Key,-16} {pair.Value}");
        foreach (string example in examples)
            Console.WriteLine($"    {example}");

        // Then the per-skeleton clip databases, which is where a clip's local index
        // should meet its name.
        string wanted = string.IsNullOrWhiteSpace(character) ? "ALIEN" : character.ToUpperInvariant();
        uint skeletonId = AnimationCatalog.Fnv1a(wanted);
        Console.WriteLine();
        Console.WriteLine($"скелет '{wanted}' -> id {skeletonId}");

        var clipDbEntry = catalog.Archive.Entries.FirstOrDefault(e =>
            e.Name.Contains($"{skeletonId}_ANIM_CLIP_DB.BIN", StringComparison.OrdinalIgnoreCase));
        if (clipDbEntry is null)
        {
            Console.WriteLine("  базы клипов для этого скелета нет");
            return 0;
        }

        byte[] clipDb = catalog.Archive.Read(clipDbEntry);
        Console.WriteLine($"  {clipDbEntry.Name}: {clipDb.Length} байт");

        var table = HashTableFile.TryParse(clipDb);
        if (table is null)
        {
            Console.WriteLine("  заголовок не распознан");
            return 0;
        }
        Console.WriteLine($"  заголовок на +0x{table.HeaderAt:X2}, записей {table.Count}, " +
                          $"пары с +0x{table.PairsAt:X2}, хвост {table.TailLength} байт " +
                          $"({(table.Count > 0 ? (table.TailLength / (double)table.Count).ToString("F2") : "?")} на запись)");

        var keyRoutes = new Dictionary<AnimNameResolver.Route, int>();
        var valueRoutes = new Dictionary<AnimNameResolver.Route, int>();
        var resolved = new List<string>();
        uint maxValue = 0;
        foreach (var (key, value) in table.Pairs())
        {
            var (keyName, keyRoute) = resolver.Resolve(key);
            keyRoutes[keyRoute] = keyRoutes.GetValueOrDefault(keyRoute) + 1;
            if (keyName is not null && resolved.Count < 12)
                resolved.Add($"    ключ {key,12} -> {keyName}   (значение {value})");

            var (_, valueRoute) = resolver.Resolve(value);
            valueRoutes[valueRoute] = valueRoutes.GetValueOrDefault(valueRoute) + 1;
            if (value > maxValue)
                maxValue = value;
        }

        Console.WriteLine("  по первому столбцу:");
        foreach (var pair in keyRoutes.OrderByDescending(p => p.Value))
            Console.WriteLine($"    {pair.Key,-16} {pair.Value}");
        Console.WriteLine("  по второму столбцу:");
        foreach (var pair in valueRoutes.OrderByDescending(p => p.Value))
            Console.WriteLine($"    {pair.Key,-16} {pair.Value}");
        Console.WriteLine($"  максимум во втором столбце: {maxValue}" +
                          $" ({(maxValue < table.Count ? "похоже на локальный индекс" : "не индекс")})");
        foreach (string line in resolved)
            Console.WriteLine(line);

        // The tail is what ties a local clip index to the Havok data. Its layout is
        // found rather than assumed: try plausible record strides and field positions,
        // and score each by how many records land on a section hash that actually
        // exists in the archive. A wrong arrangement scores near zero, because hitting
        // one of a few thousand specific 32-bit values by accident does not happen.
        // Ground truth: the sections whose container header declares this skeleton.
        // Matching against those rather than against every section in the archive turns
        // the search from suggestive into decisive.
        var mine = catalog.ClipsFor(@"CHARACTERS\" + wanted + @"\model0.cs2");
        var mineHashes = new HashSet<uint>();
        foreach (var clipRef in mine)
        {
            string leaf = clipRef.ShortName;
            string digits = leaf.Replace("ANIM_CLIP_DB_SEC_", "", StringComparison.OrdinalIgnoreCase)
                .Replace(".BIN", "", StringComparison.OrdinalIgnoreCase);
            if (uint.TryParse(digits, out uint h))
                mineHashes.Add(h);
        }
        Console.WriteLine();
        Console.WriteLine($"контейнеров у скелета: {mine.Count}, уникальных секций: {mineHashes.Count}");

        // The container header states how many bytes of Havok data it holds. If that is
        // far smaller than the archive record, the extraction is reading past the end of
        // one section into the next, which would explain a section appearing to hold
        // hundreds of animations when the name table only knows of a few hundred in total.
        Console.WriteLine("  запись архива против заявленного размера Havok:");
        long sumEntry = 0, sumHavok = 0;
        foreach (var clipRef in mine.Take(8))
            Console.WriteLine($"    {clipRef.ShortName,-40} запись {clipRef.Entry.Length,9}  " +
                              $"havok {clipRef.HavokSize,9}  " +
                              $"{(clipRef.HavokSize > 0 ? $"{clipRef.Entry.Length / (double)clipRef.HavokSize:F2}x" : "?")}");
        foreach (var clipRef in mine)
        {
            sumEntry += clipRef.Entry.Length;
            sumHavok += clipRef.HavokSize;
        }
        Console.WriteLine($"    итого записей {sumEntry / 1048576.0:F1} МБ, " +
                          $"заявлено havok {sumHavok / 1048576.0:F1} МБ");

        var duplicates = mine.GroupBy(c => c.ShortName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        Console.WriteLine($"    имён, встречающихся более одного раза: {duplicates.Count}");
        foreach (var group in duplicates.Take(3))
        {
            Console.WriteLine($"      {group.Key}");
            foreach (var clipRef in group)
                Console.WriteLine($"        смещение {clipRef.Entry.Offset,12} длина {clipRef.Entry.Length,9} " +
                                  $"havok {clipRef.HavokSize,9}");
        }

        // Every byte position, not just aligned ones: the stride follows from where the
        // hits actually land instead of being chosen up front.
        var hitsAt = new List<int>();
        for (int at = table.TailAt; at + 4 <= clipDb.Length; at++)
        {
            uint value = BitConverter.ToUInt32(clipDb, at);
            if (mineHashes.Contains(value))
                hitsAt.Add(at);
        }
        Console.WriteLine($"позиций в хвосте со ссылкой на свою секцию: {hitsAt.Count}");

        if (hitsAt.Count > 0)
        {
            Console.WriteLine($"  первая на +{hitsAt[0] - table.TailAt}, " +
                              $"последняя на +{hitsAt[^1] - table.TailAt}");
            var deltas = new Dictionary<int, int>();
            for (int i = 1; i < hitsAt.Count; i++)
            {
                int delta = hitsAt[i] - hitsAt[i - 1];
                deltas[delta] = deltas.GetValueOrDefault(delta) + 1;
            }
            Console.WriteLine("  расстояния между попаданиями:");
            foreach (var pair in deltas.OrderByDescending(p => p.Value).Take(6))
                Console.WriteLine($"    {pair.Key,4} байт: {pair.Value} раз");

            int stride = deltas.OrderByDescending(p => p.Value).First().Key;
            int start = hitsAt[0];
            Console.WriteLine($"  предполагаю: записи с +{start - table.TailAt}, шаг {stride}");
            Console.WriteLine("  разбор первых записей:");
            for (int i = 0; i < 12; i++)
            {
                int at = start + i * stride;
                if (at + stride > clipDb.Length)
                    break;
                var fields = new List<string>();
                for (int f = 0; f + 4 <= stride; f += 4)
                    fields.Add(BitConverter.ToUInt32(clipDb, at + f).ToString());
                uint section = BitConverter.ToUInt32(clipDb, at);
                Console.WriteLine($"    [{i,3}] {(mineHashes.Contains(section) ? "своя" : "чужая")}  " +
                                  string.Join("  ", fields));
            }
        }

        // Names that mention this character at all: the reachable vocabulary, which
        // bounds what any join could possibly produce.
        var mentioning = resolver.AllNames
            .Where(n => n.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Console.WriteLine();
        Console.WriteLine($"имён, начинающихся с '{wanted}': {mentioning.Count}");
        foreach (string name in mentioning.Take(25))
            Console.WriteLine($"    {name}");
        return 0;
    }

    // ----------------------------------------------------------------- pakget
    /// <summary>
    /// Pulls one entry out of ANIMATION.PAK, writes it, and shows enough of the head
    /// to recognise the format.
    /// </summary>
    private static int CmdPakGet(string? name, string? outPath, string? root)
    {
        if (name is null)
        {
            Console.Error.WriteLine("Нужно имя записи (подстрока).");
            return 2;
        }
        var install = Resolve(root);
        if (install is null)
            return 2;

        var catalog = AnimationCatalog.Open(install);
        var matches = catalog.Archive.Entries
            .Where(e => e.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"Ничего не найдено по '{name}'.");
            return 3;
        }
        if (matches.Count > 1)
        {
            Console.WriteLine($"совпадений {matches.Count}, беру первое:");
            foreach (var m in matches.Take(6))
                Console.WriteLine($"  {m.Length,10} {m.Name}");
        }

        var entry = matches[0];
        byte[] data = catalog.Archive.Read(entry);
        Console.WriteLine($"{entry.Name}: {data.Length} байт");

        if (!string.IsNullOrWhiteSpace(outPath))
        {
            string? dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(outPath, data);
            Console.WriteLine($"записано в {outPath}");
        }

        // Printable run of the head tells a text file from a binary one immediately.
        int printable = 0;
        int sample = Math.Min(512, data.Length);
        for (int i = 0; i < sample; i++)
            if (data[i] == 9 || data[i] == 10 || data[i] == 13 || (data[i] >= 32 && data[i] < 127))
                printable++;
        bool looksText = sample > 0 && printable * 100 / sample > 85;
        Console.WriteLine($"печатных символов в первых {sample}: {printable * 100 / Math.Max(1, sample)}% " +
                          $"({(looksText ? "похоже на текст" : "двоичный")})");

        if (looksText)
        {
            string text = Encoding.ASCII.GetString(data, 0, Math.Min(2000, data.Length));
            Console.WriteLine("--- начало ---");
            Console.WriteLine(text);
            Console.WriteLine("--- конец фрагмента ---");
        }
        else
        {
            Console.WriteLine("шестнадцатеричный дамп первых 256 байт:");
            for (int at = 0; at < Math.Min(256, data.Length); at += 16)
            {
                var hex = new StringBuilder();
                var chars = new StringBuilder();
                for (int k = 0; k < 16 && at + k < data.Length; k++)
                {
                    hex.Append(data[at + k].ToString("X2")).Append(' ');
                    byte b = data[at + k];
                    chars.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                Console.WriteLine($"  {at:X4}  {hex,-48} {chars}");
            }

            // Null-terminated runs, which is how these tables usually store names.
            var strings = new List<string>();
            var current = new StringBuilder();
            foreach (byte b in data)
            {
                if (b >= 32 && b < 127)
                {
                    current.Append((char)b);
                }
                else
                {
                    if (current.Length >= 4)
                        strings.Add(current.ToString());
                    current.Clear();
                }
            }
            if (current.Length >= 4)
                strings.Add(current.ToString());

            Console.WriteLine($"строк длиной 4+: {strings.Count}");
            foreach (string s in strings.Take(30))
                Console.WriteLine($"  {s}");
        }
        return 0;
    }

    // ------------------------------------------------------------------- pose
    /// <summary>
    /// Measures whether a skeleton and a mesh actually live in the same space.
    /// </summary>
    /// <remarks>
    /// The risk this answers: the mesh bind pose and the Havok rig could disagree on
    /// units or axis order, and the result would be a mangled pose rather than an
    /// error. Comparing the joint cloud against the mesh bounds catches that, and
    /// posing a real clip shows whether vertices stay where a body would keep them.
    /// Numbers instead of a screenshot, so it can be checked without the window.
    /// </remarks>
    private static int CmdPose(string? levelName, string? modelPath, string? root, string? clipSpec)
    {
        if (levelName is null || modelPath is null)
        {
            Console.Error.WriteLine("Нужны уровень и путь модели.");
            return 2;
        }
        var ws = LoadLevel(levelName, root);
        if (ws is null)
            return 2;

        var model = ws.FindModelByPath(modelPath) ?? ws.FindModels(modelPath).FirstOrDefault();
        if (model is null)
        {
            Console.Error.WriteLine($"Модель '{modelPath}' не найдена.");
            return 3;
        }

        var parts = new List<DecodedMesh>();
        for (int ci = 0; ci < model.Components.Count; ci++)
        {
            if (model.Components[ci].LODs.Count == 0)
                continue;
            var lod = model.Components[ci].LODs[0];
            if (lod.Name is not null && lod.Name.StartsWith("COL_", StringComparison.OrdinalIgnoreCase))
                continue;
            for (int si = 0; si < lod.Submeshes.Count; si++)
            {
                var decoded = MeshDecoder.Decode(model, ci, 0, si);
                if (decoded is { VertexCount: > 0 })
                    parts.Add(decoded);
            }
        }
        if (parts.Count == 0)
        {
            Console.Error.WriteLine("Геометрия не декодировалась.");
            return 3;
        }

        var meshMin = new Vector3(float.MaxValue);
        var meshMax = new Vector3(float.MinValue);
        foreach (var part in parts)
        {
            var (lo, hi) = part.Bounds();
            meshMin = Vector3.Min(meshMin, lo);
            meshMax = Vector3.Max(meshMax, hi);
        }
        Console.WriteLine($"частей {parts.Count}, вершин {parts.Sum(p => p.VertexCount)}, " +
                          $"скинованных {parts.Count(p => p.IsSkinned)}");
        Console.WriteLine($"меш   : {Box(meshMin, meshMax)}");

        var dumper = HavokDump.TryCreate();
        if (dumper is null)
        {
            Console.Error.WriteLine($"{HavokDump.ToolName} не найден.");
            return 5;
        }
        var catalog = AnimationCatalog.Open(ws.Install);
        byte[]? skeletonHkx = catalog.ExtractSkeletonHavok(model.Name);
        if (skeletonHkx is null)
        {
            Console.Error.WriteLine($"Скелет не найден: {catalog.DescribeSkeleton(model.Name)}");
            return 5;
        }

        var skeleton = dumper.LoadSkeletonOnly(skeletonHkx);
        var bind = skeleton.BindWorld();
        var boneMin = new Vector3(float.MaxValue);
        var boneMax = new Vector3(float.MinValue);
        foreach (var m in bind)
        {
            boneMin = Vector3.Min(boneMin, m.Translation);
            boneMax = Vector3.Max(boneMax, m.Translation);
        }
        Console.WriteLine($"скелет: костей {skeleton.Count}, {Box(boneMin, boneMax)}");

        // Same space means the joint cloud sits inside the mesh, give or take. A ratio
        // far from 1 on any axis is the signature of a unit or axis mismatch.
        Vector3 meshSize = meshMax - meshMin;
        Vector3 boneSize = boneMax - boneMin;
        Console.WriteLine($"отношение размеров кости/меш: " +
                          $"X={Ratio(boneSize.X, meshSize.X)} " +
                          $"Y={Ratio(boneSize.Y, meshSize.Y)} " +
                          $"Z={Ratio(boneSize.Z, meshSize.Z)}");

        int highest = -1;
        foreach (var part in parts)
            if (part.Joints is not null)
                foreach (ushort j in part.Joints)
                    if (j > highest)
                        highest = j;
        Console.WriteLine($"максимальный индекс кости в меше: {highest} " +
                          $"({(highest < skeleton.Count ? "в пределах скелета" : "ВЫХОДИТ ЗА СКЕЛЕТ")})");

        int named = skeleton.Bones.Count(b => !string.IsNullOrWhiteSpace(b.Name)
                                              && !b.Name.StartsWith("bone", StringComparison.Ordinal));
        Console.WriteLine($"костей с именами: {named} из {skeleton.Count}");

        Console.WriteLine();
        Console.WriteLine("первые кости:");
        for (int i = 0; i < Math.Min(8, skeleton.Count); i++)
        {
            var b = skeleton.Bones[i];
            Console.WriteLine($"  [{i,3}] parent={b.Parent,3}  '{b.Name}'");
        }

        // Written out so the dumper's own field names can be checked when something
        // like an empty bone name shows up.
        try
        {
            string jsonPath = Path.Combine(Path.GetTempPath(), "alienforge_skeleton.json");
            string json = dumper.SkeletonJson(skeletonHkx);
            File.WriteAllText(jsonPath, json);
            Console.WriteLine($"JSON скелета: {jsonPath} ({json.Length} символов)");
            Console.WriteLine("  начало: " + json[..Math.Min(400, json.Length)].Replace("\n", " "));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"JSON скелета не сохранён: {ex.Message}");
        }

        // Now a real clip: does the posed mesh stay the size of a body?
        var refs = catalog.ClipsFor(model.Name);
        if (refs.Count == 0)
        {
            Console.WriteLine("клипов нет, поза не проверяется");
            return 0;
        }

        int wantedClip = int.TryParse(clipSpec, out int parsed) && parsed >= 0 ? parsed : 0;
        var bundle = dumper.Load(catalog.ExtractHavok(refs[0]), skeletonHkx);
        if (bundle.Clips.Count == 0)
        {
            Console.WriteLine("контейнер не дал ни одного клипа");
            return 0;
        }
        var clip = bundle.Clips[Math.Min(wantedClip, bundle.Clips.Count - 1)];
        Console.WriteLine();
        Console.WriteLine($"клип: {clip.Name}, кадров {clip.Frames}, " +
                          $"{clip.Duration:F2} с, дорожек {clip.Tracks.Count}" +
                          $"{(clip.IsAdditive ? ", аддитивный" : "")}");

        // Where a scattered pose comes from: either the rig sits in the wrong space, or
        // the clip's reference pose is not the skeleton's bind pose. The first frame
        // tells them apart. If the clip is built against the same reference, its
        // skinning matrices at frame 0 are close to identity, and the mesh barely moves.
        var frame0 = PoseEvaluator.WorldPose(bundle.Skeleton, clip, 0);
        var skin0 = PoseEvaluator.SkinMatrices(bundle.Skeleton, frame0);
        float worstOffset = 0f;
        double sumOffset = 0;
        for (int i = 0; i < skin0.Length; i++)
        {
            float d = MatrixDistance(skin0[i], Matrix4x4.Identity);
            sumOffset += d;
            if (d > worstOffset) worstOffset = d;
        }
        Console.WriteLine($"матрицы скиннинга на кадре 0 против единичной: " +
                          $"средне {sumOffset / Math.Max(1, skin0.Length):F3}, максимум {worstOffset:F3}");

        var byBone = new TrackData?[bundle.Skeleton.Count];
        foreach (var t in clip.Tracks)
            if (t.Bone >= 0 && t.Bone < byBone.Length)
                byBone[t.Bone] = t;

        // How safe it is to read a one-key channel as "not animated": count the static
        // channels whose single value disagrees with the reference pose. If only a
        // couple do, the rule costs nothing and fixes the root.
        int constant = 0, constantDiffers = 0;
        for (int i = 0; i < bundle.Skeleton.Count; i++)
        {
            var track = byBone[i];
            if (track is null)
                continue;
            var b = bundle.Skeleton.Bones[i];
            if (track.Translation.Length == 1)
            {
                constant++;
                if (Vector3.Distance(track.Translation[0], b.Translation) > 1e-4f)
                    constantDiffers++;
            }
            if (track.Rotation.Length == 1)
            {
                constant++;
                if (!QuaternionNear(track.Rotation[0], b.Rotation))
                    constantDiffers++;
            }
            if (track.Scale.Length == 1)
            {
                constant++;
                if (Vector3.Distance(track.Scale[0], b.Scale) > 1e-4f)
                    constantDiffers++;
            }
        }
        Console.WriteLine($"статичных каналов {constant}, из них расходятся с bind: {constantDiffers}");

        Console.WriteLine("локальные преобразования, bind против кадра 0:");
        for (int i = 0; i < Math.Min(6, bundle.Skeleton.Count); i++)
        {
            var b = bundle.Skeleton.Bones[i];
            var track = byBone[i];
            Vector3 t0 = track?.TranslationAt(0, b.Translation) ?? b.Translation;
            Quaternion r0 = track?.RotationAt(0, b.Rotation) ?? b.Rotation;
            Console.WriteLine($"  [{i,3}] bind t=({b.Translation.X,7:F3},{b.Translation.Y,7:F3},{b.Translation.Z,7:F3}) " +
                              $"r=({b.Rotation.X,6:F3},{b.Rotation.Y,6:F3},{b.Rotation.Z,6:F3},{b.Rotation.W,6:F3})");
            Console.WriteLine($"        клип t=({t0.X,7:F3},{t0.Y,7:F3},{t0.Z,7:F3}) " +
                              $"r=({r0.X,6:F3},{r0.Y,6:F3},{r0.Z,6:F3},{r0.W,6:F3})" +
                              $"  дорожек t/r/s={track?.Translation.Length ?? 0}/" +
                              $"{track?.Rotation.Length ?? 0}/{track?.Scale.Length ?? 0}");
        }
        Console.WriteLine();

        var skinners = parts.Where(p => p.IsSkinned).Select(p => new MeshSkinner(p)).ToList();
        int[] frames = clip.Frames <= 1
            ? new[] { 0 }
            : new[] { 0, clip.Frames / 4, clip.Frames / 2, clip.Frames - 1 };

        foreach (int frame in frames)
        {
            var world = PoseEvaluator.WorldPose(bundle.Skeleton, clip, frame);
            var skin = PoseEvaluator.SkinMatrices(bundle.Skeleton, world);

            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            double sum = 0;
            float worst = 0;
            int total = 0, far = 0;

            foreach (var skinner in skinners)
            {
                skinner.Apply(skin);
                var posed = skinner.Positions;
                for (int v = 0; v < posed.Length; v++)
                {
                    lo = Vector3.Min(lo, posed[v]);
                    hi = Vector3.Max(hi, posed[v]);
                    float moved = Vector3.Distance(posed[v], skinner.Mesh.Positions[v]);
                    sum += moved;
                    if (moved > worst) worst = moved;
                    if (moved > 0.5f) far++;
                    total++;
                }
            }

            Console.WriteLine($"  кадр {frame,4}: {Box(lo, hi)}");
            Console.WriteLine($"             сдвиг: средний {sum / Math.Max(1, total):F3} м, " +
                              $"максимум {worst:F3} м, дальше 0,5 м {far * 100.0 / Math.Max(1, total):F1}%");
        }

        Console.WriteLine();
        Console.WriteLine("Ориентир: у персонажа ростом около 2 м размеры позы должны остаться");
        Console.WriteLine("того же порядка, а средний сдвиг — заметно меньше метра.");

        // A single clip cannot tell a broken pipeline from a pose that simply differs
        // from the bind pose. Scanning the whole container can: if some clip lands
        // almost on top of the bind pose, the skinning path is sound and the rest of
        // the displacement is the animation doing its job.
        Console.WriteLine();
        Console.WriteLine($"скан {bundle.Clips.Count} клипов по кадру 0 (каждая 20-я вершина):");

        var sampled = skinners
            .Select(s => (skinner: s, indices: Enumerable
                .Range(0, s.Mesh.VertexCount)
                .Where(v => v % 20 == 0)
                .ToArray()))
            .ToList();

        var scored = new List<(double mean, ClipData clip)>();
        foreach (var candidate in bundle.Clips)
        {
            var world = PoseEvaluator.WorldPose(bundle.Skeleton, candidate, 0);
            var skin = PoseEvaluator.SkinMatrices(bundle.Skeleton, world);

            double sum = 0;
            int count = 0;
            foreach (var (skinner, indices) in sampled)
            {
                skinner.Apply(skin);
                foreach (int v in indices)
                {
                    sum += Vector3.Distance(skinner.Positions[v], skinner.Mesh.Positions[v]);
                    count++;
                }
            }
            scored.Add((sum / Math.Max(1, count), candidate));
        }

        scored.Sort((a, b) => a.mean.CompareTo(b.mean));
        Console.WriteLine("  ближе всего к bind:");
        foreach (var (mean, candidate) in scored.Take(8))
            Console.WriteLine($"    {mean,6:F3} м  кадров {candidate.Frames,4}  " +
                              $"blendHint {candidate.BlendHint}  '{candidate.Name}'");
        Console.WriteLine("  дальше всего:");
        foreach (var (mean, candidate) in scored.TakeLast(3))
            Console.WriteLine($"    {mean,6:F3} м  кадров {candidate.Frames,4}  " +
                              $"blendHint {candidate.BlendHint}  '{candidate.Name}'");

        double median = scored[scored.Count / 2].mean;
        Console.WriteLine($"  медиана сдвига по клипам: {median:F3} м");
        Console.WriteLine($"  минимум: {scored[0].mean:F3} м " +
                          $"({(scored[0].mean < 0.15 ? "конвейер сходится с bind" : "остаётся расхождение")})");
        return 0;
    }

    private static string Box(Vector3 min, Vector3 max)
    {
        Vector3 size = max - min;
        return $"размер {size.X:F2} x {size.Y:F2} x {size.Z:F2} м, " +
               $"центр ({(min.X + max.X) / 2:F2}, {(min.Y + max.Y) / 2:F2}, {(min.Z + max.Z) / 2:F2})";
    }

    private static string Ratio(float a, float b)
        => MathF.Abs(b) < 1e-6f ? "н/д" : $"{a / b:F2}";

    /// <summary>Quaternions compared allowing for the sign flip that means the same rotation.</summary>
    private static bool QuaternionNear(Quaternion a, Quaternion b)
    {
        float dot = MathF.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
        return dot > 0.9999f;
    }

    /// <summary>Sum of absolute differences between two matrices, element by element.</summary>
    private static float MatrixDistance(Matrix4x4 a, Matrix4x4 b)
        => MathF.Abs(a.M11 - b.M11) + MathF.Abs(a.M12 - b.M12) + MathF.Abs(a.M13 - b.M13)
           + MathF.Abs(a.M14 - b.M14) + MathF.Abs(a.M21 - b.M21) + MathF.Abs(a.M22 - b.M22)
           + MathF.Abs(a.M23 - b.M23) + MathF.Abs(a.M24 - b.M24) + MathF.Abs(a.M31 - b.M31)
           + MathF.Abs(a.M32 - b.M32) + MathF.Abs(a.M33 - b.M33) + MathF.Abs(a.M34 - b.M34)
           + MathF.Abs(a.M41 - b.M41) + MathF.Abs(a.M42 - b.M42) + MathF.Abs(a.M43 - b.M43)
           + MathF.Abs(a.M44 - b.M44);

    /// <summary>Resolves the install and level, loads the archives, prints the log.</summary>
    /// <summary>
    /// Прогон ровно того пути, что идёт из окна (OnLoadAnimations + ExportWithAnimations),
    /// но с выводом каждой ошибки, которые окно глотает (failed++).
    /// </summary>
    private static int CmdGuiTest(string? levelName, string? modelPath, string? outPath, string? maxSpec)
    {
        if (levelName is null || modelPath is null || outPath is null) return 2;
        var ws = LoadLevel(levelName, null);
        if (ws is null) return 2;
        var model = ws.FindModelByPath(modelPath) ?? ws.FindModels(modelPath).FirstOrDefault();
        if (model is null) { Console.Error.WriteLine("модель не найдена"); return 3; }
        string modelName = model.Name ?? string.Empty;
        var catalog = AnimationCatalog.Open(ws.Install);
        var clips = catalog.ClipsFor(modelName);
        Console.WriteLine($"ClipsFor('{modelName}'): {clips.Count} секций; без имён: {clips.Count(c => c.Names.Count == 0)}");
        foreach (var c in clips.Take(12)) Console.WriteLine($"   {c.DisplayName,-60} names={c.Names.Count} file={c.ShortName}");
        int max = int.TryParse(maxSpec, out int m) ? m : clips.Count;
        var dumper = HavokDump.TryCreate();
        if (dumper is null) { Console.Error.WriteLine("нет hkdump"); return 4; }
        byte[]? skeletonHkx = catalog.ExtractSkeletonHavok(modelName, null);
        if (skeletonHkx is null) { Console.Error.WriteLine("нет скелета: " + catalog.DescribeSkeleton(modelName)); return 5; }
        SkeletonData? skeleton = null; var all = new List<ClipData>(); float fps = 30f; int failed = 0;
        var errors = new Dictionary<string, int>();
        foreach (var section in clips.Take(max))
        {
            try
            {
                var bundle = dumper.Load(catalog.ExtractHavok(section), skeletonHkx);
                AnimationCatalog.ApplyNames(section, bundle);
                skeleton ??= bundle.Skeleton;
                if (bundle.Fps > 0f) fps = bundle.Fps;
                all.AddRange(bundle.Clips);
            }
            catch (Exception ex)
            {
                failed++;
                string k = $"{ex.GetType().Name}: {ex.Message}";
                errors[k] = errors.GetValueOrDefault(k) + 1;
                if (errors[k] <= 2) Console.WriteLine($"   ! {section.ShortName}: {k}");
            }
        }
        Console.WriteLine($"секций обработано {Math.Min(max, clips.Count)}, ошибок {failed}, клипов {all.Count}");
        foreach (var kv in errors) Console.WriteLine($"   x{kv.Value}  {kv.Key}");
        skeleton ??= dumper.LoadSkeletonOnly(skeletonHkx);
        var animation = new AnimationBundle { Skeleton = skeleton, Clips = all, Fps = fps };
        try
        {
            var export = ModelExporter.ExportGlb(ws, model, outPath, null, animation);
            Console.WriteLine($"экспорт: клипов {export.Clips}, костей {export.Bones}, {export.Bytes / 1048576.0:F1} МБ; предупреждений {export.Warnings.Count}");
            foreach (var w in export.Warnings.Take(12)) Console.WriteLine($"   ! {w}");
            foreach (var c in all.Take(6)) Console.WriteLine($"   клип '{c.Name}' кадров {c.Frames} длит {c.Duration:F2}");
            if (all.Count > 0)
            {
                var c0 = all[0];
                int t1 = 0, tN = 0, r1 = 0, rN = 0, r1id = 0, t0 = 0;
                foreach (var tr in c0.Tracks)
                {
                    if (tr.Translation.Length == 1) t1++; else if (tr.Translation.Length > 1) tN++; else t0++;
                    if (tr.Rotation.Length == 1) { r1++; var q = tr.Rotation[0]; if (Math.Abs(q.W) > 0.9999f) r1id++; } else if (tr.Rotation.Length > 1) rN++;
                }
                Console.WriteLine($"   треков {c0.Tracks.Count}: transl 1key={t1} Nkey={tN} none={t0}; rot 1key={r1} (из них identity {r1id}) Nkey={rN}");
                int diffRot = 0, diffTr = 0;
                foreach (var tr in c0.Tracks)
                {
                    if (tr.Bone < 0 || tr.Bone >= skeleton!.Count) continue;
                    var b = skeleton.Bones[tr.Bone];
                    if (tr.Rotation.Length == 1) { var q = tr.Rotation[0]; var d = Math.Abs(q.X-b.Rotation.X)+Math.Abs(q.Y-b.Rotation.Y)+Math.Abs(q.Z-b.Rotation.Z)+Math.Abs(q.W-b.Rotation.W); var d2 = Math.Abs(q.X+b.Rotation.X)+Math.Abs(q.Y+b.Rotation.Y)+Math.Abs(q.Z+b.Rotation.Z)+Math.Abs(q.W+b.Rotation.W); if (Math.Min(d, d2) > 1e-3) diffRot++; }
                    if (tr.Translation.Length == 1 && (tr.Translation[0] - b.Translation).Length() > 1e-3f) diffTr++;
                }
                Console.WriteLine($"   одноключевых, отличающихся от bind: rot {diffRot}, transl {diffTr}");
                var sample = c0.Tracks.Where(tr => tr.Rotation.Length == 1).Take(4).ToList();
                foreach (var tr in sample) Console.WriteLine($"     bone {tr.Bone} rot1={tr.Rotation[0]} transl={(tr.Translation.Length > 0 ? tr.Translation[0].ToString() : "-")}");
            }
            var report = GlbValidator.Validate(outPath);
            Console.WriteLine($"проверка: {report}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ЭКСПОРТ УПАЛ: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 6;
        }
        return 0;
    }

    // UI.PAK: Scaleform-ролики интерфейса и JPEXS
    private static int CmdUi(string[] args)
    {
        string? sub = Arg(args, 1);
        var install = Resolve(null);
        if (install is null) return 2;
        AlienForge.Core.Ui.UiArchive ui;
        try { ui = AlienForge.Core.Ui.UiArchive.Open(install); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }

        switch (sub)
        {
            case "list":
            {
                foreach (var e in ui.Find(Arg(args, 2)))
                    Console.WriteLine($"{e.Name,-60} {e.Length,10} B");
                Console.WriteLine($"JPEXS: {AlienForge.Core.Ui.Jpexs.FindGui() ?? "не найден"}");
                return 0;
            }
            case "extract":
            {
                string? outDir = Arg(args, 2);
                if (outDir is null) { Console.Error.WriteLine("ui extract <папка> [фильтр]"); return 2; }
                int n = 0;
                foreach (var e in ui.Find(Arg(args, 3)))
                {
                    ui.Extract(e, outDir, keepFolders: true);
                    n++;
                }
                Console.WriteLine($"извлечено {n} -> {outDir}");
                return 0;
            }
            case "open":
            {
                var e = Arg(args, 2) is string name ? ui.Resolve(name) : null;
                if (e is null) { Console.Error.WriteLine("ui open <имя .GFX>"); return 2; }
                string path = ui.EnsureCached(e);
                var proc = AlienForge.Core.Ui.Jpexs.Open(path);
                Console.WriteLine(proc is null ? "JPEXS (ffdec.exe) не найден: поставьте в Program Files/FFDec или задайте FFDEC_HOME" : $"открыт в JPEXS: {path}");
                return proc is null ? 1 : 0;
            }
            case "sprites":
            {
                var e = Arg(args, 2) is string name ? ui.Resolve(name) : null;
                string? outDir = Arg(args, 3);
                if (e is null || outDir is null) { Console.Error.WriteLine("ui sprites <имя .GFX> <папка> [items]"); return 2; }
                string path = ui.EnsureCached(e);
                var (code, output) = AlienForge.Core.Ui.Jpexs.ExportItems(path, outDir, Arg(args, 4), line => Console.WriteLine("  " + line));
                if (code != 0) Console.Error.WriteLine(output);
                else Console.WriteLine($"готово -> {outDir}");
                return code == 0 ? 0 : 1;
            }
            default:
                Console.Error.WriteLine("ui list [фильтр] | ui extract <папка> [фильтр] | ui open <ролик> | ui sprites <ролик> <папка> [sprite,frame,image,shape,script,all]");
                return 2;
        }
    }

    private static int CmdMatFind(string? levelName, string? filter)
    {
        if (levelName is null || filter is null) return 2;
        var ws = LoadLevel(levelName, null);
        if (ws is null) return 2;
        int n = 0;
        foreach (var mat in ws.Materials.Entries)
        {
            if (!(mat.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (n++ >= 3) break;
            Console.WriteLine("=== " + mat.Name);
            Console.WriteLine(AlienForge.Core.Export.ShaderLayout.Read(mat).Describe());
        }
        return 0;
    }

    // Перепись функций COMMANDS уровня: тип функции -> число сущностей (+ параметры примера)
    private static int CmdFuncs(string? levelName, string? filter)
    {
        if (levelName is null) return 2;
        var install = Resolve(null);
        if (install is null) return 2;
        var level = ResolveLevel(install, levelName);
        if (level is null) return 2;
        var lvl = AlienForge.Core.Export.LevelExporter.OpenLevel(install.Root, level.Directory);
        var counts = new Dictionary<string, int>();
        var example = new Dictionary<string, CATHODE.Scripting.FunctionEntity>();
        foreach (var comp in lvl.Commands.Entries)
            foreach (var fe in comp.functions)
            {
                string fn = fe.function.IsFunctionType ? fe.function.AsFunctionType.ToString() : ("(" + (lvl.Commands.GetComposite(fe.function)?.name ?? fe.function.ToString()) + ")");
                if (filter is not null && !fn.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                counts[fn] = counts.GetValueOrDefault(fn) + 1;
                if (!example.ContainsKey(fn) || example[fn].parameters.Count < fe.parameters.Count) example[fn] = fe;
            }
        foreach (var kv in counts.OrderByDescending(k => k.Value).Take(filter is null ? 60 : 200))
        {
            var fe = example[kv.Key];
            var ps = string.Join(", ", fe.parameters.Take(filter is null ? 14 : 200).Select(p => $"{p.name}={Short(p.content)}"));
            Console.WriteLine($"{kv.Value,6}  {kv.Key}   [{ps}]");
        }
        return 0;
    }

    // Частоты параметров (имя=значение) у сущностей заданного типа функции
    private static int CmdParamStats(string? levelName, string? type)
    {
        if (levelName is null || type is null) return 2;
        var install = Resolve(null);
        if (install is null) return 2;
        var level = ResolveLevel(install, levelName);
        if (level is null) return 2;
        var lvl = AlienForge.Core.Export.LevelExporter.OpenLevel(install.Root, level.Directory);
        var counts = new Dictionary<string, int>();
        int total = 0;
        foreach (var comp in lvl.Commands.Entries)
            foreach (var fe in comp.functions)
            {
                string fn = fe.function.IsFunctionType ? fe.function.AsFunctionType.ToString() : (lvl.Commands.GetComposite(fe.function)?.name ?? "");
                if (!fn.Equals(type, StringComparison.OrdinalIgnoreCase)) continue;
                total++;
                foreach (var p in fe.parameters)
                {
                    string k = $"{p.name}={Short(p.content)}";
                    counts[k] = counts.GetValueOrDefault(k) + 1;
                }
                foreach (var l in fe.childLinks)
                {
                    string k = $"{l.thisParamID} -> link";
                    counts[k] = counts.GetValueOrDefault(k) + 1;
                }
            }
        Console.WriteLine($"{type}: {total} сущностей");
        foreach (var kv in counts.OrderByDescending(k => k.Value).Take(60)) Console.WriteLine($"{kv.Value,6}  {kv.Key}");
        return 0;
    }

    private static int CmdEnum(string? filter)
    {
        foreach (var e in CathodeLib.CustomTable.Vanilla.CathodeEnums.enums)
        {
            if (filter is not null && !e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine(e.Name + ": " + string.Join(", ", e.Entries.Select(x => $"{x.Index}={x.Name}")));
        }
        return 0;
    }

    // Дамп композитов по фильтру имени: сущности, параметры, связи (для разбора видимости)
    private static int CmdComp(string? levelName, string? filter, bool links)
    {
        if (levelName is null || filter is null) return 2;
        var install = Resolve(null);
        if (install is null) return 2;
        var level = ResolveLevel(install, levelName);
        if (level is null) return 2;
        var lvl = AlienForge.Core.Export.LevelExporter.OpenLevel(install.Root, level.Directory);
        var cmds = lvl.Commands;
        int shown = 0;
        foreach (var comp in cmds.Entries)
        {
            if (!(comp.name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (shown++ >= 8) break;
            Console.WriteLine($"=== {comp.name}  (функций {comp.functions.Count}, переменных {comp.variables.Count}, алиасов {comp.aliases.Count}, прокси {comp.proxies.Count})");
            foreach (var parent in cmds.Entries)
                foreach (var pf in parent.functions)
                    if (!pf.function.IsFunctionType && pf.function == comp.shortGUID)
                        Console.WriteLine($"  << {parent.name} :: {cmds.Utils.GetEntityName(parent, pf)}  [{string.Join(", ", pf.parameters.Select(p => $"{p.name}={Short(p.content)}"))}]");
            foreach (var v in comp.variables)
                Console.WriteLine($"  VAR {cmds.Utils.GetEntityName(comp, v)} : {v.type}  [{string.Join(", ", v.parameters.Select(p => $"{p.name}={Short(p.content)}"))}]");
            foreach (var fe in comp.functions)
            {
                string fn = fe.function.IsFunctionType ? fe.function.AsFunctionType.ToString() : ("(" + (cmds.GetComposite(fe.function)?.name ?? fe.function.ToString()) + ")");
                var ps = string.Join(", ", fe.parameters.Select(p => $"{p.name}={Short(p.content)}"));
                Console.WriteLine($"  {cmds.Utils.GetEntityName(comp, fe)} : {fn}  [{ps}]");
                if (links)
                    foreach (var l in fe.childLinks)
                    {
                        var target = comp.GetEntityByID(l.linkedEntityID);
                        string tn = target is null ? l.linkedEntityID.ToString() : cmds.Utils.GetEntityName(comp, target);
                        Console.WriteLine($"      {l.thisParamID.ToString()} -> {tn}.{l.linkedParamID.ToString()}");
                    }
            }
        }
        return 0;
    }

    private static string Short(CATHODE.Scripting.Internal.ParameterData? d)
    {
        if (d is null) return "?";
        string t = d.ToString() ?? d.GetType().Name;
        return t.Length > 40 ? t[..40] : t;
    }

    private static int CmdLevelDiag(string? levelName)
    {
        if (levelName is null) return 2;
        var install = Resolve(null);
        if (install is null) return 2;
        var level = ResolveLevel(install, levelName);
        if (level is null) return 2;
        var lvl = AlienForge.Core.Export.LevelExporter.OpenLevel(install.Root, level.Directory);
        AlienForge.Core.Export.LevelExporter.DiagnoseMaterials(lvl);
        return 0;
    }

    // Экспорт целого уровня в glTF как в просмотрщике OpenCAGE (см. LevelExporter)
    private static int CmdLevel(string[] args)
    {
        var positional = args.Skip(1).Where(a => !a.StartsWith("-")).ToList();
        var flags = args.Skip(1).Where(a => a.StartsWith("-")).Select(a => a.ToLowerInvariant()).ToHashSet();
        if (positional.Count < 2)
        {
            Console.Error.WriteLine("level <уровень> <out.glb|out.gltf> [корень] [--no-textures] [--unlit] [--gray] [--no-decals] [--diffuse-only] [--no-collision] [--collision-min=0.3] [--no-occluders] [--no-lights] [--no-particles] [--no-fog] [--no-rigid] [--no-zones] [--no-merge] [-v]");
            return 2;
        }
        var install = Resolve(positional.Count > 2 ? positional[2] : null);
        if (install is null) return 2;
        var level = ResolveLevel(install, positional[0]);
        if (level is null) return 2;
        var opt = new AlienForge.Core.Export.LevelExporter.Options
        {
            Textures = !flags.Contains("--no-textures"),
            Unlit = flags.Contains("--unlit"),
            Gltf = flags.Contains("--gltf") || positional[1].EndsWith(".gltf", StringComparison.OrdinalIgnoreCase),
            Verbose = flags.Contains("-v") || flags.Contains("--verbose"),
            IncludeUnsupported = flags.Contains("--gray"),
            Decals = !flags.Contains("--no-decals"),
            AllMaps = !flags.Contains("--diffuse-only"),
            GodotCollision = !flags.Contains("--no-collision"),
            CollisionMinSize = args.Skip(1).Where(a => a.StartsWith("--collision-min=")).Select(a => float.TryParse(a[16..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0.3f).DefaultIfEmpty(0.3f).First(),
            Occluders = !flags.Contains("--no-occluders"),
            Lights = !flags.Contains("--no-lights"),
            Particles = !flags.Contains("--no-particles"),
            Fog = !flags.Contains("--no-fog"),
            RigidBodies = !flags.Contains("--no-rigid"),
            Zones = !flags.Contains("--no-zones"),
            MergeStatic = !flags.Contains("--no-merge"),
        };
        try
        {
            var lvl = AlienForge.Core.Export.LevelExporter.OpenLevel(install.Root, level.Directory);
            return AlienForge.Core.Export.LevelExporter.Run(lvl, level.Name, positional[1], opt);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ошибка: " + ex.Message);
            return 1;
        }
    }

    // Материалы одной модели: полный разбор (слоты, каналы, фичи, параметры) + байткод её шейдеров
    private static int CmdMaterial(string? levelName, string? modelPath, string? outDir)
    {
        if (levelName is null || modelPath is null) return 2;
        var ws = LoadLevel(levelName, null);
        if (ws is null) return 2;
        var model = ws.FindModelByPath(modelPath) ?? ws.FindModels(modelPath).FirstOrDefault();
        if (model is null) { Console.Error.WriteLine("модель не найдена"); return 2; }
        if (outDir is not null) Directory.CreateDirectory(outDir);
        var seen = new HashSet<CATHODE.Materials.Material>();
        int ci = 0;
        foreach (var component in model.Components)
        {
            int li = 0;
            foreach (var lod in component.LODs)
            {
                int si = 0;
                foreach (var sm in lod.Submeshes)
                {
                    var mat = sm.Material;
                    string part = $"{lod.Name ?? "lod"} c{ci}l{li}s{si}";
                    si++;
                    if (mat is null || !seen.Add(mat)) continue;
                    var layout = AlienForge.Core.Export.ShaderLayout.Read(mat);
                    Console.WriteLine($"=== {part}: {mat.Name}");
                    Console.WriteLine(layout.Describe());
                    if (outDir is not null && mat.Shader?.PixelShader is { Length: > 0 })
                    {
                        string safe = string.Concat((mat.Name ?? "mat").Select(c => char.IsLetterOrDigit(c) ? c : '_'));
                        File.WriteAllBytes(Path.Combine(outDir, $"{part.Replace(' ', '_')}_{safe}.ps.dxbc"), mat.Shader.PixelShader);
                    }
                }
                li++;
            }
            ci++;
        }
        return 0;
    }

    // Байткод шейдеров уровня по убершейдерам: <папка>\<UBERSHADER>_<n>.ps.dxbc (+ vs)
    private static int CmdShaderDump(string? levelName, string? outDir)
    {
        if (levelName is null || outDir is null) return 2;
        var ws = LoadLevel(levelName, null);
        if (ws is null) return 2;
        Directory.CreateDirectory(outDir);
        var seen = new Dictionary<CATHODE.Shaders.Shader, string>();
        var perUber = new Dictionary<string, int>();
        var index = new List<string>();
        foreach (var mat in ws.Materials.Entries)
        {
            var sh = mat.Shader;
            if (sh is null || sh.PixelShader is null || sh.PixelShader.Length == 0) continue;
            if (seen.TryGetValue(sh, out var name)) { index.Add($"{name}	{mat.Name}"); continue; }
            string uber = sh.Ubershader.ToString();
            perUber.TryGetValue(uber, out int n);
            perUber[uber] = n + 1;
            name = $"{uber}_{n}";
            seen[sh] = name;
            File.WriteAllBytes(Path.Combine(outDir, name + ".ps.dxbc"), sh.PixelShader);
            if (sh.VertexShader is { Length: > 0 }) File.WriteAllBytes(Path.Combine(outDir, name + ".vs.dxbc"), sh.VertexShader);
            var samplers = CATHODE.ShaderTypes.ShaderUtility.GetSamplers(sh.Ubershader);
            var features = CATHODE.ShaderTypes.ShaderUtility.GetFeatures(sh.Ubershader);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"ubershader {uber}  features 0x{sh.UbershaderFeatureFlags:X}  req 0x{sh.UbershaderRequirementFlags:X}");
            foreach (var f in features)
            {
                int? bit = CATHODE.ShaderTypes.ShaderUtility.GetShaderFunctionalityIndex(sh.Ubershader, CATHODE.ShaderTypes.ShaderIndexType.FEATURES, f);
                if (bit is not null && (sh.UbershaderFeatureFlags & (1L << bit.Value)) != 0) sb.AppendLine($"  feature {f}");
            }
            for (int i = 0; i < sh.SamplerRemaps.Count; i++)
                if (sh.SamplerRemaps[i] != 255)
                    sb.AppendLine($"  sampler {i} {(i < samplers.Count ? samplers[i] : "?")} -> texture slot {sh.SamplerRemaps[i]}  stage {(i < sh.SamplerStageBindings.Count ? sh.SamplerStageBindings[i] : -1)}");
            var pars = CATHODE.ShaderTypes.ShaderUtility.GetParameters(sh.Ubershader);
            for (int i = 0; i < sh.PixelShaderParameterRemaps.Count; i++)
                if (sh.PixelShaderParameterRemaps[i] != 255)
                    sb.AppendLine($"  ps param {i} {(i < pars.Count ? pars[i] : "?")} -> const {sh.PixelShaderParameterRemaps[i]}");
            sb.AppendLine($"  material example: {mat.Name}");
            File.WriteAllText(Path.Combine(outDir, name + ".txt"), sb.ToString());
            index.Add($"{name}	{mat.Name}");
        }
        File.WriteAllLines(Path.Combine(outDir, "index.txt"), index);
        Console.WriteLine($"уникальных шейдеров: {seen.Count}");
        foreach (var kv in perUber.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Key}: {kv.Value}");
        return 0;
    }

    private static int CmdTexStats(string? levelName)
    {
        if (levelName is null) return 2;
        var ws = LoadLevel(levelName, null);
        if (ws is null) return 2;
        int total = 0, noTex = 0, noDiffuse = 0, unknownSlots = 0, shown = 0;
        var byShader = new Dictionary<int, (int mats, int noDiff)>();
        foreach (var mat in ws.Materials.Entries)
        {
            total++;
            var layout = AlienForge.Core.Export.ShaderLayout.Read(mat);
            if (layout.Slots.Count == 0) { noTex++; continue; }
            unknownSlots += layout.UnknownSlots;
            bool nd = layout.First(AlienForge.Core.Export.TextureRole.Diffuse) is null
                      && layout.First(AlienForge.Core.Export.TextureRole.Colour) is null;
            byShader.TryGetValue((mat.Shader is null ? -1 : (int)mat.Shader.Ubershader), out var c);
            byShader[(mat.Shader is null ? -1 : (int)mat.Shader.Ubershader)] = (c.mats + 1, c.noDiff + (nd ? 1 : 0));
            if (nd)
            {
                noDiffuse++;
                if (shown++ < 25)
                    Console.WriteLine($"  {mat.Name} shader={(mat.Shader is null ? -1 : (int)mat.Shader.Ubershader)}: " + string.Join(" | ", layout.Slots.Select(x => $"[{x.Index}] {x.TextureName}")));
            }
        }
        Console.WriteLine($"материалов {total}, без текстур {noTex}, с текстурами но без diffuse {noDiffuse}, слотов с неясной ролью {unknownSlots}");
        foreach (var u in ws.Materials.Entries.Where(m => m.Shader is not null).Select(m => m.Shader.Ubershader).Distinct())
            Console.WriteLine($"  {u}: " + string.Join(", ", CATHODE.ShaderTypes.ShaderUtility.GetSamplers(u).Select((n, i) => $"{i}:{n}")));
        foreach (var kv in byShader.OrderByDescending(k => k.Value.noDiff).Take(15))
            Console.WriteLine($"  shader {kv.Key}: материалов {kv.Value.mats}, без diffuse {kv.Value.noDiff}");
        return 0;
    }

    // Отладка ANIM_CLIP_DB.BIN: имя, секция, строка и второе слово строки
    private static int CmdClipRows(string? filter)
    {
        var install = Resolve(null);
        if (install is null) return 2;
        var pak = Pak2Index.Open(install.AnimationPak);
        var master = pak.Entries.First(e => e.Name.EndsWith(@"ANIM_SYS\ANIM_CLIP_DB.BIN", StringComparison.OrdinalIgnoreCase));
        byte[] data = pak.Read(master);
        var table = HashTableFile.TryParse(data)!;
        var resolver = AnimNameResolver.Load(pak);
        Console.WriteLine($"count={table.Count} tailAt={table.TailAt} tailLen={table.TailLength} ({table.TailLength / 8.0} строк по 8)");
        int shown = 0;
        foreach (var (key, row) in table.Pairs())
        {
            string? name = resolver.Name(key);
            if (name is null) continue;
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            int at = table.TailAt + (int)row * 8;
            uint a = BitConverter.ToUInt32(data, at), b = BitConverter.ToUInt32(data, at + 4);
            Console.WriteLine($"row {row,6}  sec {a,10}  w2 {b,10} (0x{b:X8})  {name}");
            if (++shown >= 40) break;
        }
        return 0;
    }

    private static int CmdRigs(string? filter)
    {
        var install = Resolve(null);
        if (install is null) return 2;
        var catalog = AnimationCatalog.Open(install);
        foreach (var (id, name, clips) in catalog.ListSkeletons())
            if (filter is null || name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"{name,-40} id {id,-12} клипов {clips}");
        return 0;
    }

    private static int CmdRig(string? levelName, string? filter)
    {
        if (levelName is null) return 2;
        var ws = LoadLevel(levelName, null);
        if (ws is null) return 2;
        var catalog = AnimationCatalog.Open(ws.Install);
        var dumper = HavokDump.TryCreate();
        foreach (var model in ws.FindModels(filter ?? "CHARACTERS"))
        {
            var g = catalog.GuessSkeleton(model.Name, model, dumper);
            Console.WriteLine($"{model.Name,-50} нужно костей {AnimationCatalog.RequiredBoneCount(model),3}  ->  {g.name ?? "?"} (id {g.id}, костей {g.bones}) {(g.entry is null ? "НЕ НАЙДЕН" : "")}");
        }
        return 0;
    }

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
