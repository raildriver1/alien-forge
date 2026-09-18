using System.Numerics;
using System.Runtime.InteropServices;
using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CATHODE.ShaderTypes;
using CathodeLib;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AlienForge.Core.Export;

/// <summary>
/// Экспорт уровня в glTF ровно так, как его показывает просмотрщик OpenCAGE
/// (CathodeEditorGodot). Алгоритм списан с декомпилированного просмотрщика:
///
///   1. Корневой композит уровня (Commands.EntryPoints[0]) обходится
///      рекурсивно. Каждая сущность становится узлом с трансформом из
///      параметра "position" (cTransform). FunctionEntity, чья функция —
///      другой композит, получает детей (инстанс композита).
///   2. ModelReference: параметр "resource" -> RENDERABLE_INSTANCE -> список
///      записей REDS (сабмеш + материал). Только базовые записи, LOD'ы не
///      входят в диапазон.
///   3. Алиасы и прокси с параметром "position" переставляют узел, на
///      который указывают (LevelViewer: WireCompositeAliasProxies).
///   4. Материал без diffuse-сэмплера для своего юбершейдера (окклюдеры,
///      коллизия, хелперы, CA_DECAL...) — меш НЕ экспортируется. Это и есть
///      "стены поперёк прохода" у наивных экспортёров.
///   5. Материал: диффуз по Shader.SamplerRemaps, DIFFUSE_TINT, DIFFUSE_UV_MULT,
///      SEPARATE_ALPHA, прозрачность/вырез по фичам и render-state,
///      double-sided по фиче; MaterialMappings по инстансу композита и
///      параметр "material" на самой ModelReference.
///   6. Оси: Cathode -> glTF как в просмотрщике: (x, y, -z) для позиций и
///      нормалей, Эйлер (-x, -y, z) в порядке YXZ; обмотка треугольников
///      переворачивается (a, c, b) — зеркало меняет ориентацию граней.
/// </summary>
public static class LevelExporter
{
    public sealed class Options
    {
        public bool Textures = true;
        public bool Unlit = false;
        public bool Gltf = false;          // .gltf + .bin + png рядом вместо .glb
        public bool Verbose = false;
        public bool IncludeUnsupported = false; // экспортировать и меши без диффуза (серым) — для отладки
        /// <summary>
        /// CA_DECAL (кровь, потёртости, "structural metal" поверх стен). Просмотрщик
        /// OpenCAGE их НЕ показывает (у него нет diffuse-слота для CA_DECAL), а в
        /// игре они есть. По умолчанию включены — цель "как в игре".
        /// </summary>
        public bool Decals = true;
        /// <summary>
        /// Класть в файл и остальные карты шейдера (normal, specular, AO, glow) плюс
        /// extras с ролями каналов/параметрами — для пересборки материалов 1:1 скриптом
        /// tools/alienforge_blender.py. Диффуз кладётся всегда.
        /// </summary>
        public bool AllMaps = true;
        /// <summary>
        /// Коллизия для Godot: узлы мешей получают суффикс «-col» — импортёр Godot
        /// сам создаёт StaticBody3D с тримеш-формой из визуальной геометрии.
        /// Своей статической коллизии у уровня в CathodeLib нет (COLLISION.HKX —
        /// Havok), поэтому коллизия строится по видимым мешам: декали, туман и
        /// мелочь короче collision_min_size пропускаются.
        /// </summary>
        public bool GodotCollision = true;
        public float CollisionMinSize = 0.3f;
        /// <summary>Окклюдеры игры (CA_Occlusion_Culling) → узлы «-occonly» (OccluderInstance3D в Godot).</summary>
        public bool Occluders = true;
        /// <summary>LightReference → свет glTF (KHR_lights_punctual): Omni/SpotLight3D в Godot.</summary>
        public bool Lights = true;
        /// <summary>Коэффициент: intensity_multiplier игры → energy Godot (1000 → 5).</summary>
        public float LightEnergyScale = 1f / 200f;
        /// <summary>ParticleEmitterReference → узлы PFX_n + параметры в sidecar-JSON (GPUParticles3D после импорта).</summary>
        public bool Particles = true;
        /// <summary>FogSphere/FogBox/FogSetting → sidecar-JSON (FogVolume + volumetric fog после импорта).</summary>
        public bool Fog = true;
        /// <summary>Пропсы из композитов с PhysicsSystem → «-rigid» (RigidBody3D).</summary>
        public bool RigidBodies = true;
        /// <summary>Инстансы композитов с сущностью Zone → узлы ZONE_&lt;имя&gt; (стриминг зон в Godot).</summary>
        public bool Zones = true;
        /// <summary>
        /// Слить статику по зонам прямо в файле: один меш на (зона, материал) в
        /// мировых координатах + один меш коллизии на зону (COLLISION+col). Файл
        /// растёт (инстансов больше нет), зато Godot импортирует тысячи узлов
        /// вместо десятков тысяч — минуты вместо часов. Rigid-пропсы, окклюдеры,
        /// свет, частицы и туман остаются отдельными узлами.
        /// </summary>
        public bool MergeStatic = true;
        public int MergeMaxVertices = 400_000;
    }

    /// <summary>Куда писать ход экспорта; по умолчанию — консоль.</summary>
    public static Action<string> Log { get; set; } = Console.WriteLine;

    /// <summary>
    /// Полная загрузка уровня через CathodeLib (GLOBAL + ANIMATION.PAK + сам
    /// уровень) — COMMANDS, REDS, MaterialMappings и текстуры нужны экспортёру целиком.
    /// </summary>
    public static Level OpenLevel(string gameRoot, string levelDir)
    {
        string animPakPath = Path.Combine(gameRoot, "DATA", "GLOBAL", "ANIMATION.PAK");
        string globalDir = Path.Combine(gameRoot, "DATA", "ENV", "GLOBAL");
        if (!File.Exists(animPakPath))
            throw new FileNotFoundException("Не найден ANIMATION.PAK", animPakPath);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var animPak = new PAK2(animPakPath);
        var global = new Global(globalDir, animPak);
        Log($"GLOBAL прочитан за {sw.Elapsed.TotalSeconds:0.0} с");
        sw.Restart();
        var lvl = new Level(levelDir, global);
        Log($"уровень прочитан за {sw.Elapsed.TotalSeconds:0.0} с");
        if (lvl.Commands is null || !lvl.Commands.Loaded || lvl.Commands.Entries.Count == 0)
            throw new InvalidDataException("COMMANDS.PAK уровня не загрузился");
        return lvl;
    }

    // ------------------------------------------------------------------ узлы

    private sealed class ExNode
    {
        public string Name = "";
        public Entity? Entity;
        public Composite? Owner;           // композит, в котором объявлена сущность
        public Composite? Instanced;       // композит, который эта сущность инстанцирует (если это инстанс)
        public Vector3 Position;           // уже в glTF-осях
        public Quaternion Rotation = Quaternion.Identity;
        public List<ExNode> Children = new();
        public ExNode? Parent;
        public Dictionary<uint, ExNode> ByEntityId = new(); // дети по id сущности
        public bool IsModelRef;
        public ExNode? MappingScope;       // ближайший инстанс композита (для MaterialMappings)
        public bool IsLight, IsParticle, IsFog, IsTrigger;
        public string? ZoneName;           // инстанс композита с сущностью Zone
        public bool InPhysics;             // внутри композита с PhysicsSystem → -rigid
        public bool NoDisplay;             // скрыт на старте (show_on_reset=false, disable_display, Master.hide)
        public bool NoCollision;           // disable_collision / delete_standard_collision / enable_on_reset=false
    }

    private static readonly ShortGuid NameParam = ShortGuidUtils.Generate("name");
    private static readonly ShortGuid IsTemplatePin = ShortGuidUtils.Generate("is_template");
    private static readonly ShortGuid DeletedPin = ShortGuidUtils.Generate("deleted");
    private static readonly ShortGuid DisableDisplayPin = ShortGuidUtils.Generate("disable_display");
    private static readonly ShortGuid DisableCollisionPin = ShortGuidUtils.Generate("disable_collision");
    private static readonly ShortGuid DeleteStdCollisionPin = ShortGuidUtils.Generate("delete_standard_collision");
    private static readonly ShortGuid ShowOnResetPin = ShortGuidUtils.Generate("show_on_reset");
    private static readonly ShortGuid EnableOnResetPin = ShortGuidUtils.Generate("enable_on_reset");
    private static readonly ShortGuid DecalScalePin = ShortGuidUtils.Generate("decal_scale");
    private static readonly ShortGuid ObjectsPin = ShortGuidUtils.Generate("objects");
    private static Logic _logic = null!;
    private static int _skippedTemplates, _skippedDeleted, _skippedHidden;

    /// <summary>
    /// Мини-интерпретатор скриптов Cathode «на момент загрузки». Нужен, чтобы не
    /// экспортировать то, чего в игре на старте нет: панели под резак на каждой
    /// двери (Door_Mechanism держит ВСЕ варианты механизма и удаляет лишние
    /// функциями Delete*), повреждённые варианты ламп (deleted ← LogicNot ←
    /// переменная композита), шаблоны эффектов (is_template), скрытые модели
    /// (show_on_reset ← LogicSwitch/VariableBool)…
    /// Значение пина = связь (this.pin → X.reference = вычислить X; → переменная
    /// композита = параметр инстанса выше по дереву либо значение по умолчанию),
    /// иначе статический параметр, иначе null (неизвестно → считаем «как есть»).
    /// </summary>
    private sealed class Logic
    {
        private readonly Dictionary<(ExNode, uint, uint), object?> _memo = new();
        private readonly HashSet<(ExNode, uint, uint)> _busy = new();
        private static readonly ShortGuid RefPin = ShortGuidUtils.Generate("reference");
        private static readonly ShortGuid InitialValuePin = ShortGuidUtils.Generate("initial_value");
        private static readonly ShortGuid LhsPin = ShortGuidUtils.Generate("LHS");
        private static readonly ShortGuid RhsPin = ShortGuidUtils.Generate("RHS");
        private static readonly ShortGuid InputPin = ShortGuidUtils.Generate("Input");
        private static readonly ShortGuid DoorMechPin = ShortGuidUtils.Generate("door_mechanism");
        private static readonly ShortGuid LeverTypePin = ShortGuidUtils.Generate("lever_type");
        private static readonly ShortGuid ButtonTypePin = ShortGuidUtils.Generate("button_type");

        /// <summary>Значение пина сущности e, объявленной в композите inst.Instanced.</summary>
        /// <summary>ALIENFORGE_NOLOGIC=1 — выключить интерпретатор (для сравнения)</summary>
        public bool Disabled = Environment.GetEnvironmentVariable("ALIENFORGE_NOLOGIC") == "1";

        public object? Pin(ExNode? inst, Entity e, ShortGuid pin)
        {
            if (Disabled) return null;
            if (inst is null) return Static(e, pin);
            var key = (inst, e.shortGUID.AsUInt32, pin.AsUInt32);
            if (_memo.TryGetValue(key, out var cached)) return cached;
            if (!_busy.Add(key)) return null; // цикл
            object? result = null;
            try
            {
                var comp = inst.Instanced;
                if (comp is not null)
                    foreach (var l in e.childLinks)
                    {
                        if (l.thisParamID != pin) continue;
                        var target = comp.GetEntityByID(l.linkedEntityID);
                        if (target is null) continue;
                        result = Source(inst, target, l.linkedParamID);
                        if (result is not null) break;
                    }
                result ??= Static(e, pin);
            }
            finally { _busy.Remove(key); }
            _memo[key] = result;
            return result;
        }

        private object? Source(ExNode inst, Entity target, ShortGuid pin)
        {
            if (target is VariableEntity v) return Variable(inst, v);
            if (target is FunctionEntity fe && pin == RefPin) return Eval(inst, fe);
            return Pin(inst, target, pin);
        }

        /// <summary>Переменная композита: параметр/связь на инстансе (уровнем выше), иначе значение по умолчанию.</summary>
        private object? Variable(ExNode inst, VariableEntity v)
        {
            if (inst.Entity is not null && inst.Parent is not null)
            {
                var o = Pin(inst.Parent, inst.Entity, v.name);
                if (o is not null) return o;
            }
            return Static(v, v.name);
        }

        private object? Eval(ExNode inst, FunctionEntity fe)
        {
            if (!fe.function.IsFunctionType) return null;
            switch (fe.function.AsFunctionType)
            {
                case FunctionType.VariableBool:
                case FunctionType.LogicSwitch:
                    return AsBool(Pin(inst, fe, InitialValuePin)) ?? false;
                case FunctionType.VariableInt:
                case FunctionType.VariableEnum:
                case FunctionType.VariableFloat:
                case FunctionType.VariableString:
                case FunctionType.VariableVector:
                    return Pin(inst, fe, InitialValuePin);
                case FunctionType.LogicNot:
                    { var b = AsBool(Pin(inst, fe, InputPin)); return b is null ? null : !b; }
                case FunctionType.LogicGateAnd:
                    { var a = AsBool(Pin(inst, fe, LhsPin)); var b = AsBool(Pin(inst, fe, RhsPin)); return a is null || b is null ? null : a & b; }
                case FunctionType.LogicGateOr:
                    { var a = AsBool(Pin(inst, fe, LhsPin)); var b = AsBool(Pin(inst, fe, RhsPin)); return a is null || b is null ? null : a | b; }
                case FunctionType.IntegerEquals:
                    { var a = AsInt(Pin(inst, fe, LhsPin)); var b = AsInt(Pin(inst, fe, RhsPin)); return a is null || b is null ? null : a == b; }
                case FunctionType.IntegerNotEqual:
                    { var a = AsInt(Pin(inst, fe, LhsPin)); var b = AsInt(Pin(inst, fe, RhsPin)); return a is null || b is null ? null : a != b; }
                case FunctionType.IntegerLessThan:
                    { var a = AsInt(Pin(inst, fe, LhsPin)); var b = AsInt(Pin(inst, fe, RhsPin)); return a is null || b is null ? null : a < b; }
                case FunctionType.IntegerLessThanOrEqual:
                    { var a = AsInt(Pin(inst, fe, LhsPin)); var b = AsInt(Pin(inst, fe, RhsPin)); return a is null || b is null ? null : a <= b; }
                case FunctionType.IntegerGreaterThan:
                    { var a = AsInt(Pin(inst, fe, LhsPin)); var b = AsInt(Pin(inst, fe, RhsPin)); return a is null || b is null ? null : a > b; }
                case FunctionType.IntegerGreaterThanOrEqual:
                    { var a = AsInt(Pin(inst, fe, LhsPin)); var b = AsInt(Pin(inst, fe, RhsPin)); return a is null || b is null ? null : a >= b; }
                // Door_Mechanism: в композите лежат ВСЕ варианты механизма двери, а
                // движок удаляет лишние по DOOR_MECHANISM (0 NONE, 1 BLANK, 2 KEYPAD,
                // 3 LEVER, 4 BUTTON, 5 HACKING, 6..9 HIDDEN_* — под панелью для резака).
                case FunctionType.DeleteCuttingPanel:
                    { var m = AsInt(Pin(inst, fe, DoorMechPin)); return m is null ? null : !(m >= 6 && m <= 9); }
                case FunctionType.DeleteBlankPanel:
                    { var m = AsInt(Pin(inst, fe, DoorMechPin)); return m is null ? null : m != 1; }
                case FunctionType.DeleteKeypad:
                    { var m = AsInt(Pin(inst, fe, DoorMechPin)); return m is null ? null : !(m == 2 || m == 6); }
                case FunctionType.DeleteHacking:
                    { var m = AsInt(Pin(inst, fe, DoorMechPin)); return m is null ? null : !(m == 5 || m == 8); }
                case FunctionType.DeleteHousing:
                    { var m = AsInt(Pin(inst, fe, DoorMechPin)); return m is null ? null : !(m == 2 || m == 5 || m == 6 || m == 8); }
                case FunctionType.DeleteButtonDisk:
                    { var m = AsInt(Pin(inst, fe, DoorMechPin)); var t = AsInt(Pin(inst, fe, ButtonTypePin)) ?? 0; return m is null ? null : !((m == 4 || m == 9) && t == 1); }
                case FunctionType.DeleteButtonKeys:
                    { var m = AsInt(Pin(inst, fe, DoorMechPin)); var t = AsInt(Pin(inst, fe, ButtonTypePin)) ?? 0; return m is null ? null : !((m == 4 || m == 9) && t == 0); }
                case FunctionType.DeletePullLever:
                    { var m = AsInt(Pin(inst, fe, DoorMechPin)); var t = AsInt(Pin(inst, fe, LeverTypePin)) ?? 0; return m is null ? null : !((m == 3 || m == 7) && t == 0); }
                case FunctionType.DeleteRotateLever:
                    { var m = AsInt(Pin(inst, fe, DoorMechPin)); var t = AsInt(Pin(inst, fe, LeverTypePin)) ?? 0; return m is null ? null : !((m == 3 || m == 7) && t == 1); }
                default:
                    return null;
            }
        }

        private static object? Static(Entity e, ShortGuid pin) => e.GetParameter(pin)?.content switch
        {
            cBool b => b.value,
            cInteger i => i.value,
            cEnum en => en.enumIndex,
            cFloat f => f.value,
            cVector3 v => new Vector3(v.value.X, v.value.Y, v.value.Z),
            cString st => st.value,
            _ => null,
        };

        public static bool? AsBool(object? o) => o switch { bool b => b, int i => i != 0, float f => f != 0f, _ => null };
        public static int? AsInt(object? o) => o switch { bool b => b ? 1 : 0, int i => i, float f => (int)f, _ => null };
    }

    private static readonly List<FunctionEntity> _fogSettings = new();

    private static readonly ShortGuid PositionParam = ShortGuidUtils.Generate("position");
    private static readonly ShortGuid ResourceParam = ShortGuidUtils.Generate("resource");
    private static readonly ShortGuid MappingParam = ShortGuidUtils.Generate("mapping");
    private static readonly ShortGuid MaterialParam = ShortGuidUtils.Generate("material");

    // ------------------------------------------------------------------ вход

    public static int Run(Level lvl, string levelName, string outPath, Options opt)
    {
        var commands = lvl.Commands;
        var root = commands.EntryPoints?.FirstOrDefault(e => e is not null);
        if (root is null) { Log("Нет точки входа (EntryPoints[0])."); return 2; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log($"корневой композит: {root.name}");

        // 1-2. дерево
        _fogSettings.Clear();
        _geomCache.Clear();
        _logic = new Logic();
        _skippedTemplates = _skippedDeleted = _skippedHidden = 0;
        var rootNode = new ExNode { Name = SafeName(levelName), Owner = root, Instanced = root };
        int nodeCount = 0, modelRefCount = 0;
        BuildComposite(commands, root, rootNode, rootNode, ref nodeCount, ref modelRefCount, 0);
        Log($"узлов: {nodeCount}, ModelReference: {modelRefCount} ({sw.Elapsed.TotalSeconds:0.0} с)");
        Log($"отсечено скриптом уровня: шаблонов {_skippedTemplates}, удалённых на старте {_skippedDeleted}, скрытых {_skippedHidden}");

        // Если у уровня есть секции (*_Section) — зонами считаем только их: сущности
        // Zone внутри секций логические (анимации, задачи), а не пространственные,
        // и дробили бы геометрию на сотни мелких кусков
        if (HasSectionZones(rootNode))
            DropNonSectionZones(rootNode);

        // 3. алиасы/прокси
        int overrides = 0;
        WireAliasProxies(commands, root, rootNode, rootNode, ref overrides);
        Log($"переопределений позиции алиасами/прокси: {overrides}");

        // индекс алиасов с "mapping" -> целевая сущность (для MaterialMappings)
        var aliasMappingByTarget = BuildAliasMappingIndex(commands);

        // 4-6. glTF
        var ctx = new GltfContext(lvl, opt);
        var scene = new SceneBuilder(SafeName(levelName));
        var gltfRoot = new NodeBuilder(rootNode.Name);
        ctx.CurrentZone = gltfRoot;
        EmitNode(ctx, scene, gltfRoot, rootNode, aliasMappingByTarget);
        if (opt.MergeStatic) FlushMerged(ctx, scene);

        Log($"мешей (инстансов): {ctx.InstanceCount}, уникальных сабмешей: {ctx.MeshCache.Count}, материалов: {ctx.MaterialCache.Count}, текстур: {ctx.ImageCache.Count}" + (ctx.Opt.GodotCollision ? $", с коллизией Godot (-col): {ctx.CollisionCount}" : ""));
        if (ctx.Skipped.Count > 0)
        {
            Log("пропущено (материал без диффуза, как в просмотрщике):");
            foreach (var kv in ctx.Skipped.OrderByDescending(k => k.Value).Take(25))
                Log($"   {kv.Value,6}  {kv.Key}");
        }

        if (opt.Verbose && AttrStats.Count > 0)
        {
            Log("атрибуты вершин (usage/type: сабмешей):");
            foreach (var kv in AttrStats.OrderByDescending(k => k.Value)) Log($"   {kv.Value,6}  {kv.Key}");
        }
        Console.Write("пишу glTF ... ");
        var model = scene.ToGltf2();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
        if (opt.Gltf || outPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
            model.SaveGLTF(outPath);
        else
            model.SaveGLB(outPath);
        // Sidecar для импорта в Godot: частицы, туман, триггеры, зоны, свет (см. tools/godot/)
        try
        {
            ctx.Sidecar["level"] = levelName;
            ctx.Sidecar["fog_settings"] = _fogSettings.Select(fs => (object)new Dictionary<string, object>
            {
                ["near_colour"] = V3(GetVec3Param(fs, "near_colour", new Vector3(2, 2, 10)) / 255f),
                ["far_colour"] = V3(GetVec3Param(fs, "far_colour", new Vector3(2, 2, 10)) / 255f),
                ["exponential_density"] = GetFloatParam(fs, "exponential_density", 0.05f),
                ["linear_density"] = GetFloatParam(fs, "linear_density", 0f),
                ["max_distance"] = GetFloatParam(fs, "max_distance", 500f),
            }).ToList();
            ctx.Sidecar["zones"] = ctx.ZoneNames.ToList();
            ctx.Sidecar["stats"] = new Dictionary<string, object>
            {
                ["instances"] = ctx.InstanceCount, ["collision"] = ctx.CollisionCount, ["occluders"] = ctx.OccluderCount,
                ["rigid"] = ctx.RigidCount, ["lights"] = ctx.LightCount, ["decals"] = ctx.DecalCount, ["particles"] = ctx.Particles.Count, ["fog"] = ctx.FogVolumes.Count,
            };
            ctx.Sidecar["particles"] = ctx.Particles;
            ctx.Sidecar["lights"] = ctx.LightsSidecar;
            ctx.Sidecar["fog"] = ctx.FogVolumes;
            ctx.Sidecar["triggers"] = ctx.Triggers;
            string side = outPath + ".alienforge.json";
            File.WriteAllText(side, System.Text.Json.JsonSerializer.Serialize(ctx.Sidecar, new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
            Log($"sidecar для Godot: {side}");
        }
        catch (Exception ex) { Log("sidecar не записан: " + ex.Message); }
        if (opt.Verbose)
            foreach (var kv in ctx.RigidByComposite.OrderByDescending(k => k.Value).Take(15)) Log($"   rigid {kv.Value,5}  {kv.Key}");
        Log($"свет: {ctx.LightCount}, декалей: {ctx.DecalCount}, дверей: {ctx.DoorCount}, окклюдеров: {ctx.OccluderCount}, rigid: {ctx.RigidCount}, частиц: {ctx.Particles.Count}, туман: {ctx.FogVolumes.Count}, зон: {ctx.ZoneNames.Count}");
        Log($"готово: {outPath} ({sw.Elapsed.TotalSeconds:0.0} с)");
        return 0;
    }

    private static float[] V3(Vector3 v) => new[] { v.X, v.Y, v.Z };

    // ------------------------------------------------------------------ дерево

    /// <summary>
    /// Копия LevelViewerPopulateTree.CollectEntityList: все сущности композита
    /// (функции + переменные? нет — просмотрщик при populate берёт
    /// includeVariables=false; алиасы/прокси откладываются на wiring).
    /// </summary>
    private static void BuildComposite(Commands commands, Composite composite, ExNode instanceNode, ExNode scope,
        ref int nodeCount, ref int modelRefCount, int depth)
    {
        if (depth > 64) return; // страховка от циклов
        // Master — групповой выключатель: его show_on_reset/enable_on_reset относятся к
        // сущностям, привязанным к пину objects (варианты комнаты: Lobby_Complete /
        // Lobby_Broken, включаемые сценарием группы света и т.п.)
        // objects → сущность напрямую или TriggerSequence со списком путей (в т.ч. вглубь
        // вложенных композитов); применяется после сборки поддерева (HideSubtree).
        List<(ShortGuid[] path, bool hide, bool disable)>? masterTargets = null;
        foreach (FunctionEntity m in composite.functions)
        {
            if (!m.function.IsFunctionType || m.function.AsFunctionType != FunctionType.Master) continue;
            bool hide = !(Logic.AsBool(_logic.Pin(instanceNode, m, ShowOnResetPin)) ?? true);
            bool disable = !(Logic.AsBool(_logic.Pin(instanceNode, m, EnableOnResetPin)) ?? true);
            if (!hide && !disable) continue;
            foreach (var l in m.childLinks)
            {
                if (l.thisParamID != ObjectsPin) continue;
                masterTargets ??= new();
                if (composite.GetEntityByID(l.linkedEntityID) is TriggerSequence ts)
                    foreach (var se in ts.sequence) masterTargets.Add((se.connectedEntity.path, hide, disable));
                else
                    masterTargets.Add((new[] { l.linkedEntityID }, hide, disable));
            }
        }
        foreach (FunctionEntity fe in composite.functions)
        {
            // Шаблоны (спавнятся по событию) и удалённые скриптом на старте — не существуют
            // …кроме шаблонов, которые AssetSpawner ставит при загрузке (физ. пропсы: Twinkie_Template и т.п.)
            if ((Logic.AsBool(_logic.Pin(instanceNode, fe, IsTemplatePin)) ?? false) && !SpawnedOnLoad(instanceNode, composite, fe)) { _skippedTemplates++; Trace(commands, composite, instanceNode, fe, "шаблон"); continue; }
            if (Logic.AsBool(_logic.Pin(instanceNode, fe, DeletedPin)) ?? false) { _skippedDeleted++; Trace(commands, composite, instanceNode, fe, "deleted"); continue; }
            var node = new ExNode
            {
                Name = fe.shortGUID.AsUInt32.ToString(),
                Entity = fe,
                Owner = composite,
                Parent = instanceNode,
                MappingScope = scope,
            };
            ReadTransform(fe, out node.Position, out node.Rotation);
            node.NoDisplay = instanceNode.NoDisplay || (Logic.AsBool(_logic.Pin(instanceNode, fe, DisableDisplayPin)) ?? false);
            if (node.NoDisplay && !instanceNode.NoDisplay) Trace(commands, composite, instanceNode, fe, "disable_display");
            node.NoCollision = instanceNode.NoCollision
                               || (Logic.AsBool(_logic.Pin(instanceNode, fe, DisableCollisionPin)) ?? false)
                               || (Logic.AsBool(_logic.Pin(instanceNode, fe, DeleteStdCollisionPin)) ?? false);
            instanceNode.Children.Add(node);
            instanceNode.ByEntityId[fe.shortGUID.AsUInt32] = node;
            nodeCount++;

            // Физика не наследуется вглубь: rigid — только меши того композита,
            // где стоит PhysicsSystem (сам проп), иначе целые секции уровня
            // помечались динамическими
            node.InPhysics = instanceNode.InPhysics && instanceNode.Instanced is not null && composite == instanceNode.Instanced && fe.function.IsFunctionType;
            if (fe.function.IsFunctionType)
            {
                switch (fe.function.AsFunctionType)
                {
                    case FunctionType.ModelReference:
                        node.IsModelRef = true;
                        modelRefCount++;
                        if (!(Logic.AsBool(_logic.Pin(instanceNode, fe, ShowOnResetPin)) ?? true)) { node.NoDisplay = true; _skippedHidden++; Trace(commands, composite, instanceNode, fe, "show_on_reset=false"); }
                        if (!(Logic.AsBool(_logic.Pin(instanceNode, fe, EnableOnResetPin)) ?? true)) node.NoCollision = true;
                        break;
                    case FunctionType.LightReference:
                        node.IsLight = true;
                        if (!(Logic.AsBool(_logic.Pin(instanceNode, fe, ShowOnResetPin)) ?? true)) node.NoDisplay = true;
                        break;
                    case FunctionType.ParticleEmitterReference: node.IsParticle = true; break;
                    case FunctionType.FogSphere: case FunctionType.FogBox: node.IsFog = true; break;
                    case FunctionType.PlayerTriggerBox: node.IsTrigger = true; break;
                    case FunctionType.FogSetting: _fogSettings.Add(fe); break;
                }
                continue;
            }
            Composite? nested = commands.GetComposite(fe.function);
            if (nested is null) continue;
            node.Instanced = nested;
            // Секции уровня (…\Emergency_Section, …\Staff_Section) — пространственные
            // куски, по которым игра стримит; для Godot это лучшие зоны
            {
                string cn = nested.name ?? "";
                int cut = cn.LastIndexOf('\\');
                string leaf = cut >= 0 ? cn[(cut + 1)..] : cn;
                if (leaf.EndsWith("_Section", StringComparison.OrdinalIgnoreCase))
                    node.ZoneName = leaf;
            }
            // Зона стриминга и физика — свойства композита, который инстанцируется
            foreach (FunctionEntity inner in nested.functions)
            {
                if (!inner.function.IsFunctionType) continue;
                var ft = inner.function.AsFunctionType;
                if (ft == FunctionType.Zone && inner.GetParameter(NameParam)?.content is cString zn && !string.IsNullOrWhiteSpace(zn.value))
                    node.ZoneName = zn.value;
                // PhysicsSystem стоит и у дверей/панелей/рычагов (анимированные
                // сет-писы) — RigidBody из них делать нельзя. Динамические
                // пропсы живут в _Props_\Physics (канистры, коробки, бутылки).
                else if (ft == FunctionType.PhysicsSystem && (nested.name ?? "").Contains(@"_Props_\Physics", StringComparison.OrdinalIgnoreCase))
                    node.InPhysics = true;
            }
            BuildComposite(commands, nested, node, node, ref nodeCount, ref modelRefCount, depth + 1);
        }
        if (masterTargets is not null)
            foreach (var (path, hide, disable) in masterTargets)
            {
                ExNode? cur = instanceNode;
                foreach (var g in path)
                {
                    if (g == ShortGuid.Invalid || g.AsUInt32 == 0) break;
                    if (cur is null || !cur.ByEntityId.TryGetValue(g.AsUInt32, out cur)) { cur = null; break; }
                }
                if (cur is null || cur == instanceNode) continue;
                if (hide && !cur.NoDisplay)
                {
                    _skippedHidden++;
                    if (cur.Entity is FunctionEntity tfe && cur.Owner is not null) Trace(commands, cur.Owner, cur.Parent ?? instanceNode, tfe, "Master.show_on_reset=false");
                }
                SetSubtree(cur, hide, disable);
            }
    }

    private static void SetSubtree(ExNode n, bool hide, bool disable)
    {
        if (hide) n.NoDisplay = true;
        if (disable) n.NoCollision = true;
        foreach (var c in n.Children) SetSubtree(c, hide, disable);
    }

    /// <summary>ALIENFORGE_TRACE=подстрока — печатать, почему отсекаются сущности с таким именем/композитом</summary>
    private static readonly string? _trace = Environment.GetEnvironmentVariable("ALIENFORGE_TRACE");
    private static void Trace(Commands commands, Composite composite, ExNode inst, FunctionEntity fe, string why)
    {
        if (string.IsNullOrEmpty(_trace)) return;
        string n = commands.Utils.GetEntityName(composite, fe);
        string nested = fe.function.IsFunctionType ? fe.function.AsFunctionType.ToString() : (commands.GetComposite(fe.function)?.name ?? "");
        if (!n.Contains(_trace, StringComparison.OrdinalIgnoreCase) && !nested.Contains(_trace, StringComparison.OrdinalIgnoreCase)) return;
        Log($"[trace] {why}: {PathOf(inst)} > {composite.name} :: {n} ({nested})");
    }

    private static readonly ShortGuid AssetPin = ShortGuidUtils.Generate("asset");
    private static readonly ShortGuid SpawnOnLoadPin = ShortGuidUtils.Generate("spawn_on_load");
    private static readonly ShortGuid SpawnOnResetPin = ShortGuidUtils.Generate("spawn_on_reset");
    private static readonly ShortGuid AllowPhysicsPin = ShortGuidUtils.Generate("allow_physics");

    /// <summary>Есть ли в композите AssetSpawner, который спавнит этот шаблон при загрузке.</summary>
    private static bool SpawnedOnLoad(ExNode inst, Composite composite, FunctionEntity template)
    {
        foreach (var sp in composite.functions)
        {
            if (!sp.function.IsFunctionType || sp.function.AsFunctionType != FunctionType.AssetSpawner) continue;
            bool refs = false;
            foreach (var l in sp.childLinks)
                if (l.thisParamID == AssetPin && l.linkedEntityID == template.shortGUID) { refs = true; break; }
            if (!refs) continue;
            if ((Logic.AsBool(_logic.Pin(inst, sp, SpawnOnLoadPin)) ?? false) || (Logic.AsBool(_logic.Pin(inst, sp, SpawnOnResetPin)) ?? false))
                return true;
        }
        return false;
    }

    private static bool HasSectionZones(ExNode n)
    {
        if (n.ZoneName is not null && n.ZoneName.EndsWith("_Section", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var c in n.Children) if (HasSectionZones(c)) return true;
        return false;
    }

    private static void DropNonSectionZones(ExNode n)
    {
        if (n.ZoneName is not null && !n.ZoneName.EndsWith("_Section", StringComparison.OrdinalIgnoreCase)) n.ZoneName = null;
        foreach (var c in n.Children) DropNonSectionZones(c);
    }

    /// <summary>
    /// Копия AlienScene.WireCompositeAliasProxies + ApplyAliasOrProxyOverrides:
    /// алиас/прокси с "position" переставляет узел, на который указывает.
    /// Порядок: сначала алиасы этого композита, потом рекурсивно вложенные —
    /// более глубокие переопределения побеждают, как в просмотрщике.
    /// </summary>
    private static void WireAliasProxies(Commands commands, Composite composite, ExNode instanceRoot, ExNode levelRoot, ref int overrides)
    {
        foreach (AliasEntity alias in composite.aliases)
        {
            if (alias.GetParameter(PositionParam) is null) continue;
            // путь алиаса: [инстанс, инстанс, ..., сущность] относительно этого композита
            ExNode? target = FollowPath(instanceRoot, alias.alias.path);
            if (target is null) continue;
            ReadTransform(alias, out target.Position, out target.Rotation);
            overrides++;
        }
        foreach (ProxyEntity proxy in composite.proxies)
        {
            if (proxy.GetParameter(PositionParam) is null) continue;
            // прокси: путь от корня уровня
            ExNode? target = FollowPath(levelRoot, proxy.proxy.path);
            if (target is null) continue;
            ReadTransform(proxy, out target.Position, out target.Rotation);
            overrides++;
        }
        foreach (FunctionEntity fe in composite.functions)
        {
            if (fe.function.IsFunctionType) continue;
            Composite? nested = commands.GetComposite(fe.function);
            if (nested is null) continue;
            if (instanceRoot.ByEntityId.TryGetValue(fe.shortGUID.AsUInt32, out var child))
                WireAliasProxies(commands, nested, child, levelRoot, ref overrides);
        }
    }

    private static ExNode? FollowPath(ExNode from, ShortGuid[] path)
    {
        ExNode cur = from;
        foreach (var id in path)
        {
            if (id == ShortGuid.Invalid) continue;
            if (!cur.ByEntityId.TryGetValue(id.AsUInt32, out var next)) return null;
            cur = next;
        }
        return cur == from ? null : cur;
    }

    private static void ReadTransform(Entity entity, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.Zero;
        rotation = Quaternion.Identity;
        if (entity.GetParameter(PositionParam)?.content is not cTransform t) return;
        position = new Vector3(t.position.X, t.position.Y, -t.position.Z);
        // Godot: RotationDegrees = (-x, -y, z), порядок YXZ (Ry * Rx * Rz для
        // вектора-столбца: сначала Z, потом X, потом Y).
        float rx = DegToRad(-t.rotation.X), ry = DegToRad(-t.rotation.Y), rz = DegToRad(t.rotation.Z);
        // System.Numerics — строчный порядок: "сначала A, потом B" = A * B
        Matrix4x4 m = Matrix4x4.CreateRotationZ(rz) * Matrix4x4.CreateRotationX(rx) * Matrix4x4.CreateRotationY(ry);
        rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    private static float DegToRad(float d) => d * (MathF.PI / 180f);

    // ------------------------------------------------------------------ glTF

    private sealed class GltfContext
    {
        public readonly Level Level;
        public readonly Options Opt;
        public readonly Dictionary<int, MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>?> MeshCache = new();
        public readonly Dictionary<MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>, float> MeshSize = new(ReferenceEqualityComparer.Instance);
        public int CollisionCount, OccluderCount, RigidCount, LightCount, DecalCount, DoorCount;
        public readonly Dictionary<string, object> Sidecar = new();
        public readonly List<object> Particles = new();
        public readonly List<object> LightsSidecar = new();
        public readonly List<object> FogVolumes = new();
        public readonly List<object> Triggers = new();
        public readonly HashSet<string> ZoneNames = new();
        public readonly Dictionary<string, int> RigidByComposite = new();
        public readonly Dictionary<int, RawMesh?> RawCache = new();
        public readonly Dictionary<NodeBuilder, ZoneMerge> Merges = new(ReferenceEqualityComparer.Instance);
        /// <summary>Узел зоны, в который сейчас сливаем (ZONE_* или корень)</summary>
        public NodeBuilder? CurrentZone;
        public readonly Dictionary<string, int> NameCounter = new();
        public readonly Dictionary<string, MaterialBuilder> PfxMaterials = new();
        public readonly Dictionary<string, MaterialBuilder> MaterialCache = new();
        public readonly Dictionary<string, ImageBuilder?> ImageCache = new();
        public readonly Dictionary<string, int> Skipped = new();
        public int InstanceCount;
        public readonly MaterialBuilder Gray;
        public GltfContext(Level level, Options opt)
        {
            Level = level; Opt = opt;
            Gray = new MaterialBuilder("unsupported_gray").WithMetallicRoughnessShader().WithBaseColor(new Vector4(0.6f, 0.6f, 0.6f, 1)).WithMetallicRoughness(0, 1).WithDoubleSide(true);
        }
    }

    private static void EmitNode(GltfContext ctx, SceneBuilder scene, NodeBuilder gnode, ExNode node,
        Dictionary<uint, List<(Composite owner, cResource res)>> aliasMappingByTarget)
    {
        gnode.LocalTransform = Matrix4x4.CreateFromQuaternion(node.Rotation) * Matrix4x4.CreateTranslation(node.Position);

        _currentOwner = node.Owner;
        _currentPath = PathOf(node);
        if (node.NoDisplay) { }
        else if (node.IsModelRef && node.Entity is FunctionEntity fe)
            EmitModelReference(ctx, scene, gnode, node, fe, aliasMappingByTarget);
        else if (node.IsLight && ctx.Opt.Lights && node.Entity is FunctionEntity lf && !IsEventFx(node))
            EmitLight(ctx, scene, gnode, lf);
        else if (node.IsParticle && ctx.Opt.Particles && node.Entity is FunctionEntity pf && !IsEventFx(node)
                 && GetBoolParam(pf, "start_on_reset", true) && GetBoolParam(pf, "show_on_reset", true)
                 && !GetBoolParam(pf, "BLENDING_DISTORTION", false) && !GetStringParam(pf, "TEXTURE_MAP", "").Contains("[N]", StringComparison.OrdinalIgnoreCase))
            EmitParticles(ctx, scene, gnode, pf);
        else if (node.IsFog && ctx.Opt.Fog && node.Entity is FunctionEntity ff && !IsEventFx(node)
                 && GetBoolParam(ff, "show_on_reset", true) && GetBoolParam(ff, "enable_on_reset", true)
                 && !(node.Owner?.name ?? "").Contains("Sparks", StringComparison.OrdinalIgnoreCase))
            EmitFog(ctx, gnode, ff);
        else if (node.IsTrigger && ctx.Opt.Zones && node.Entity is FunctionEntity tf)
            EmitTrigger(ctx, gnode, tf, node);

        foreach (var child in node.Children)
        {
            // Узлы без геометрии и без детей-с-геометрией не нужны — иначе
            // Blender получит десятки тысяч пустых Empty (звуки, триггеры...)
            if (!SubtreeHasGeometry(ctx, child)) continue;
            string name = NodeDisplayName(ctx.Level.Commands, child);
            if (child.ZoneName is not null && ctx.Opt.Zones)
            {
                name = "ZONE_" + SafeName(child.ZoneName);
                ctx.ZoneNames.Add(SafeName(child.ZoneName));
            }
            else if (child.IsLight) name = "LIGHT_" + name;
            else if (child.IsParticle) name = UniqueName(ctx, "PFX");
            else if (child.IsFog) name = UniqueName(ctx, "FOG");
            else if (child.IsTrigger) name = UniqueName(ctx, "TRIGGER");
            var gchild = gnode.CreateNode(name);
            var prevZone = ctx.CurrentZone;
            if (name.StartsWith("ZONE_")) ctx.CurrentZone = gchild;
            EmitNode(ctx, scene, gchild, child, aliasMappingByTarget);
            ctx.CurrentZone = prevZone;
        }
    }

    private static readonly Dictionary<ExNode, bool> _geomCache = new(ReferenceEqualityComparer.Instance);
    private static bool SubtreeHasGeometry(GltfContext ctx, ExNode n)
    {
        if (_geomCache.TryGetValue(n, out bool v)) return v;
        bool fx = !IsEventFx(n) && !n.NoDisplay;
        bool has = (n.IsModelRef && !n.NoDisplay)
                   || (fx && n.IsLight && ctx.Opt.Lights)
                   || (fx && n.IsParticle && ctx.Opt.Particles && n.Entity is FunctionEntity pf && GetBoolParam(pf, "start_on_reset", true) && GetBoolParam(pf, "show_on_reset", true) && !GetBoolParam(pf, "BLENDING_DISTORTION", false) && !GetStringParam(pf, "TEXTURE_MAP", "").Contains("[N]", StringComparison.OrdinalIgnoreCase))
                   || (fx && n.IsFog && ctx.Opt.Fog && n.Entity is FunctionEntity ff && GetBoolParam(ff, "show_on_reset", true) && GetBoolParam(ff, "enable_on_reset", true))
                   || (n.IsTrigger && ctx.Opt.Zones);
        if (!has) foreach (var c in n.Children) if (SubtreeHasGeometry(ctx, c)) { has = true; break; }
        _geomCache[n] = has;
        return has;
    }

    /// <summary>
    /// Шаблоны эффектов и всё, что игра спавнит по событию (попадания, взрывы,
    /// кровь, оружие, урон), лежат в дереве COMMANDS как обычные вложенные
    /// композиты — в статичном уровне их быть не должно. Отсекаем по пути.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex _eventFx = new(
        @"Required_Assets|Impact|Explosion|Weapons|Blood|SystemicDamage|Debris|Character|Lens|Cutting|HE_|IED|Tazer|Molotov|Flame|Death|Kill|Hit|Gib|Torch|Muzzle|_Template|Templates",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static bool IsEventFx(ExNode node)
        => _eventFx.IsMatch(PathOf(node) + " " + (node.Owner?.name ?? ""));

    private static Composite? _currentOwner;
    private static string _currentPath = "";
    private static string PathOf(ExNode n)
    {
        var parts = new List<string>();
        for (var p = n.Parent; p is not null; p = p.Parent)
            if (p.Instanced?.name is not null) parts.Add(p.Instanced.name);
        parts.Reverse();
        return string.Join(" > ", parts);
    }

    private static string UniqueName(GltfContext ctx, string prefix)
    {
        int n = ctx.NameCounter.GetValueOrDefault(prefix);
        ctx.NameCounter[prefix] = n + 1;
        return $"{prefix}_{n:D4}";
    }

    // ------------------------------------------------------------------ свет / частицы / туман / триггеры

    private static bool GetBoolParam(Entity e, string name, bool def)
        => e.GetParameter(name)?.content is cBool b ? b.value : def;
    private static int GetEnumParam(Entity e, string name, int def)
        => e.GetParameter(name)?.content is cEnum en ? en.enumIndex : (e.GetParameter(name)?.content is cInteger i ? i.value : def);
    private static string GetStringParam(Entity e, string name, string def)
        => e.GetParameter(name)?.content is cString s ? s.value : def;

    /// <summary>LightReference → KHR_lights_punctual. LIGHT_TYPE: 0 omni, 1 spot (остальное — omni).</summary>
    private static void EmitLight(GltfContext ctx, SceneBuilder scene, NodeBuilder gnode, FunctionEntity fe)
    {
        if (!GetBoolParam(fe, "on", true) || !GetBoolParam(fe, "light_on_reset", true)) return;
        var c = GetVec3Param(fe, "colour", new Vector3(255, 255, 255)) / 255f;
        // цвет задан в sRGB — в glTF свет линейный
        var lin = new Vector3(MathF.Pow(Math.Clamp(c.X, 0, 1), 2.2f), MathF.Pow(Math.Clamp(c.Y, 0, 1), 2.2f), MathF.Pow(Math.Clamp(c.Z, 0, 1), 2.2f));
        float mult = GetFloatParam(fe, "intensity_multiplier", 1000f);
        float energy = Math.Clamp(mult * ctx.Opt.LightEnergyScale, 0.05f, 16f);
        float range = MathF.Max(GetFloatParam(fe, "end_attenuation", 10f), 0.5f);
        int type = GetEnumParam(fe, "type", 0);
        LightBuilder light;
        if (type == 1)
        {
            float inner = DegToRad(Math.Clamp(GetFloatParam(fe, "inner_cone_angle", 30f), 1f, 89f));
            float outer = DegToRad(Math.Clamp(GetFloatParam(fe, "outer_cone_angle", 45f), 1f, 89.9f));
            if (outer <= inner) outer = inner + 0.02f; // glTF: inner строго меньше outer
            inner = MathF.Max(0f, MathF.Min(inner, outer - 0.01f));
            // SharpGLTF не пишет outerConeAngle, равный умолчанию (π/4), а импортёр
            // Godot без ключа падает — уводим от точного умолчания
            if (MathF.Abs(outer - MathF.PI / 4f) < 1e-4f) outer += 0.002f;
            light = new LightBuilder.Spot { Color = lin, Intensity = energy, Range = range, InnerConeAngle = inner, OuterConeAngle = outer };
        }
        else
            light = new LightBuilder.Point { Color = lin, Intensity = energy, Range = range };
        light.Name = "LIGHT";
        scene.AddLight(light, gnode);
        ctx.LightCount++;
        // полный набор параметров + мировое положение и направление (по -Z узла, как у KHR_lights) — в sidecar
        // для своих движков (AlienDeferred): gobo, start_attenuation, is_square_light, cast_shadow, physical_attenuation…
        var d = ParamsToDict(fe);
        var w = gnode.WorldMatrix;
        d["node"] = gnode.Name;
        d["composite"] = _currentOwner?.name ?? "";
        d["world_position"] = V3(w.Translation);
        d["world_direction"] = V3(Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, w)));
        d["world_up"] = V3(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, w)));
        d["colour_linear"] = V3(lin);
        d["energy"] = energy;
        d["range"] = range;
        d["light_type"] = type;
        ctx.LightsSidecar.Add(d);
    }

    private static Dictionary<string, object> ParamsToDict(FunctionEntity fe)
    {
        var d = new Dictionary<string, object>();
        foreach (var p in fe.parameters)
        {
            object? v = p.content switch
            {
                cFloat f => f.value,
                cInteger i => i.value,
                cBool b => b.value,
                cString s => s.value,
                cVector3 v3 => V3(new Vector3(v3.value.X, v3.value.Y, v3.value.Z)),
                cEnum en => en.enumIndex,
                _ => null,
            };
            string pname = p.name.ToString();
            if (v is not null && !string.IsNullOrEmpty(pname)) d[pname] = v;
        }
        return d;
    }

    /// <summary>
    /// ParticleEmitterReference: параметры целиком в sidecar (GPUParticles3D собирает
    /// скрипт импорта), спрайт — крошечный квад с unlit-материалом под узлом
    /// PFXTEX (Godot импортирует его текстуру, скрипт забирает и удаляет квад).
    /// </summary>
    private static void EmitParticles(GltfContext ctx, SceneBuilder scene, NodeBuilder gnode, FunctionEntity fe)
    {
        var d = ParamsToDict(fe);
        d["node"] = gnode.Name;
        d["composite"] = _currentOwner?.name ?? "";
        d["path"] = _currentPath;
        string texPath = GetStringParam(fe, "TEXTURE_MAP", "");
        var tex = FindTextureByPath(ctx, texPath);
        if (tex is not null && ctx.Opt.Textures)
        {
            string key = tex.Name ?? texPath;
            if (!ctx.PfxMaterials.TryGetValue(key, out var mb))
            {
                mb = new MaterialBuilder(SafeName("PFX_" + Path.GetFileNameWithoutExtension(key))).WithUnlitShader().WithAlpha(AlphaMode.BLEND).WithDoubleSide(true);
                var img = GetPlainImage(ctx, tex);
                if (img is not null) mb.UseChannel(KnownChannel.BaseColor).UseTexture().WithPrimaryImage(img);
                ctx.PfxMaterials[key] = mb;
            }
            var quad = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>("pfxtex");
            var prim = quad.UsePrimitive(mb);
            const float h = 0.005f;
            var n = Vector3.UnitZ;
            var a = new VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(new VertexPositionNormal(new Vector3(-h, -h, 0), n), new VertexColor1Texture1(Vector4.One, new Vector2(0, 1)));
            var b = new VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(new VertexPositionNormal(new Vector3(h, -h, 0), n), new VertexColor1Texture1(Vector4.One, new Vector2(1, 1)));
            var c = new VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(new VertexPositionNormal(new Vector3(h, h, 0), n), new VertexColor1Texture1(Vector4.One, new Vector2(1, 0)));
            var dd = new VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(new VertexPositionNormal(new Vector3(-h, h, 0), n), new VertexColor1Texture1(Vector4.One, new Vector2(0, 0)));
            prim.AddTriangle(a, b, c);
            prim.AddTriangle(a, c, dd);
            var tnode = gnode.CreateNode("PFXTEX");
            scene.AddRigidMesh(quad, tnode);
            d["texture"] = key;
        }
        ctx.Particles.Add(d);
    }

    /// <summary>"N:\Content\Build\Textures\AYZ\FX\FX_HEAT.TGA" → текстура уровня "ayz\fx\fx_heat.tga".</summary>
    private static Textures.TEX4? FindTextureByPath(GltfContext ctx, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string p = path.Replace('/', '\\');
        int at = p.IndexOf("Textures\\", StringComparison.OrdinalIgnoreCase);
        if (at >= 0) p = p[(at + 9)..];
        string want = Path.ChangeExtension(p, null).ToLowerInvariant();
        foreach (var tex in ctx.Level.Textures.Entries)
        {
            string n = Path.ChangeExtension((tex.Name ?? "").Replace('/', '\\'), null).ToLowerInvariant();
            if (n == want) return tex;
        }
        var gl = ctx.Level.Global?.Textures;
        if (gl is not null)
            foreach (var tex in gl.Entries)
            {
                string n = Path.ChangeExtension((tex.Name ?? "").Replace('/', '\\'), null).ToLowerInvariant();
                if (n == want) return tex;
            }
        return null;
    }

    private static void EmitFog(GltfContext ctx, NodeBuilder gnode, FunctionEntity fe)
    {
        var d = ParamsToDict(fe);
        d["node"] = gnode.Name;
        d["composite"] = _currentOwner?.name ?? "";
        d["path"] = _currentPath;
        d["kind"] = fe.function.AsFunctionType == FunctionType.FogBox ? "box" : "sphere";
        var w = gnode.WorldMatrix;
        d["world_position"] = V3(w.Translation);
        ctx.FogVolumes.Add(d);
    }

    private static void EmitTrigger(GltfContext ctx, NodeBuilder gnode, FunctionEntity fe, ExNode node)
    {
        var d = new Dictionary<string, object>
        {
            ["node"] = gnode.Name,
            ["half_dimensions"] = V3(GetVec3Param(fe, "half_dimensions", Vector3.One)),
            ["world_position"] = V3(gnode.WorldMatrix.Translation),
        };
        // ближайшая зона по дереву
        for (var p = node; p is not null; p = p.Parent)
            if (p.ZoneName is not null) { d["zone"] = SafeName(p.ZoneName); break; }
        ctx.Triggers.Add(d);
    }

    private static readonly System.Text.RegularExpressions.Regex DoorPanelName = new(@"^(Door|Door_L|Door_R|Door_Left|Door_Right|LiftDoorInt_L|LiftDoorInt_R|DoorHandle_Panel)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    /// <summary>Створка двери: сущность Door/Door_L/… (или панель взлома DoorHandle_Panel в её центре) внутри композита AYZ\Doors\* (до 4 уровней вверх); doorNode — узел glTF этого композита.</summary>
    private static bool IsDoorPanel(ExNode node, NodeBuilder gnode, out string doorNode)
    {
        doorNode = "";
        string displayName = gnode.Name;
        int br = displayName.IndexOf(" [", StringComparison.Ordinal);
        string entName = br >= 0 ? displayName[..br] : displayName;
        // индикаторы замка (LockStatus_Lights: DoorButtons_* / Decals) сидят на створке в центре проёма
        bool lockLight = (entName.StartsWith("DoorButtons_", StringComparison.OrdinalIgnoreCase) || entName.Equals("Decals", StringComparison.OrdinalIgnoreCase))
            && (node.Parent?.Instanced?.name ?? "").Contains("LockStatus_Lights", StringComparison.OrdinalIgnoreCase);
        if (!lockLight && !DoorPanelName.IsMatch(entName)) return false;
        var ex = node.Parent; var gn = gnode.Parent;
        for (int up = 0; up < 4 && ex is not null && gn is not null; up++, ex = ex.Parent, gn = gn.Parent)
        {
            string comp = (ex.Instanced?.name ?? "").Replace('/', '\\');
            if (comp.Contains(@"\Doors\", StringComparison.OrdinalIgnoreCase) || comp.Contains(@"\Door\", StringComparison.OrdinalIgnoreCase)) { doorNode = gn.Name; return true; }
        }
        return false;
    }

    private static string NodeDisplayName(Commands commands, ExNode node)
    {
        string name = node.Name;
        try
        {
            if (node.Entity is not null && node.Owner is not null)
            {
                string n = commands.Utils.GetEntityName(node.Owner, node.Entity);
                if (!string.IsNullOrWhiteSpace(n)) name = n;
            }
        }
        catch { /* имя не обязательно */ }
        if (node.Instanced is not null && node.Instanced.name is not null)
        {
            string comp = node.Instanced.name;
            int slash = comp.LastIndexOf('/');
            if (slash >= 0) comp = comp[(slash + 1)..];
            name = $"{name} [{comp}]";
        }
        return SafeName(name);
    }

    private static void EmitModelReference(GltfContext ctx, SceneBuilder scene, NodeBuilder gnode, ExNode node, FunctionEntity fe,
        Dictionary<uint, List<(Composite owner, cResource res)>> aliasMappingByTarget)
    {
        var level = ctx.Level;
        if (fe.GetParameter(ResourceParam)?.content is not cResource res) return;
        var rr = res.GetResource(ResourceType.RENDERABLE_INSTANCE);
        if (rr is null || rr.RenderableInstance is null || rr.RenderableInstance.Count == 0) return;

        // (сабмеш, материал) — как GetRenderableIndexes в просмотрщике
        var renderables = new List<(Models.CS2.Component.LOD.Submesh submesh, Materials.Material material)>();
        foreach (var el in rr.RenderableInstance)
        {
            if (el.Model is null || el.Material is null) continue;
            renderables.Add((el.Model, el.Material));
        }
        if (renderables.Count == 0) return;

        // MaterialMappings по ближайшему инстансу композита (scope)
        var mapping = ResolveMapping(ctx.Level, node, aliasMappingByTarget);
        if (mapping is not null)
        {
            for (int i = 0; i < renderables.Count; i++)
            {
                var m = RemapMaterial(level, mapping, renderables[i].material);
                if (m is not null) renderables[i] = (renderables[i].submesh, m);
            }
        }
        // Параметр "material" на самой ModelReference (только когда один сабмеш)
        if (renderables.Count == 1 && fe.GetParameter(MaterialParam)?.content is cString ms && !string.IsNullOrWhiteSpace(ms.value))
        {
            var m = FindMaterialByName(level.Materials, ms.value);
            if (m is not null) renderables[0] = (renderables[0].submesh, m);
        }

        NodeBuilder? rigidNode = null;
        foreach (var (submesh, material) in renderables)
        {
            // Окклюдеры игры: невидимы, но нужны Godot для occlusion culling
            if (ctx.Opt.Occluders && material.Shader?.Ubershader == SHADER_LIST.CA_OCCLUSION_CULLING)
            {
                var omesh = GetMesh(ctx, submesh, ctx.Gray);
                if (omesh is null) continue;
                var onode = gnode.CreateNode(gnode.Name + "-occonly");
                scene.AddRigidMesh(omesh, onode);
                ctx.OccluderCount++;
                continue;
            }
            // CA_DECAL — не меш, а проектор: единичный куб ±1, умноженный на decal_scale,
            // проецирует текстуру на геометрию под собой. В Godot это узел Decal —
            // alienforge_level_import.gd делает его из MeshInstance «DECAL_*».
            if (material.Shader?.Ubershader == SHADER_LIST.CA_DECAL)
            {
                if (!ctx.Opt.Decals) continue;
                var dmat = GetMaterial(ctx, material, fe);
                var dmesh = dmat is null ? null : GetMesh(ctx, submesh, dmat);
                if (dmesh is null) continue;
                var scale = _logic.Pin(node.Parent, fe, DecalScalePin) is Vector3 sv ? sv : Vector3.One;
                var dnode = gnode.CreateNode(UniqueName(ctx, "DECAL"));
                dnode.LocalTransform = Matrix4x4.CreateScale(scale);
                scene.AddRigidMesh(dmesh, dnode);
                ctx.DecalCount++;
                continue;
            }
            var mat = GetMaterial(ctx, material, fe);
            if (mat is null)
            {
                string key = $"{material.Name} ({material.Shader?.Ubershader.ToString() ?? "no shader"})";
                ctx.Skipped[key] = ctx.Skipped.GetValueOrDefault(key) + 1;
                if (!ctx.Opt.IncludeUnsupported) continue;
                mat = ctx.Gray;
            }
            var mesh = GetMesh(ctx, submesh, mat);
            if (mesh is null) continue;
            // Створки дверей (композиты AYZ\Doors\*, сущности Door / Door_L / Door_R …): не сливать со статикой —
            // отдельный узел с extras {"door": <узел композита двери>}, чтобы движок мог их открывать.
            if (IsDoorPanel(node, gnode, out string doorNode))
            {
                var dn = gnode.CreateNode(gnode.Name + "-door");
                dn.Extras = new System.Text.Json.Nodes.JsonObject { ["door"] = doorNode };
                scene.AddRigidMesh(mesh, dn);
                ctx.DoorCount++;
                ctx.InstanceCount++;
                continue;
            }
            if (ctx.Opt.RigidBodies && node.InPhysics)
            {
                // Динамический проп: RigidBody3D с выпуклой коллизией (суффикс Godot
                // «-rigid»). Один RigidBody на сущность: первый сабмеш — на самом
                // узле (по нему Godot строит коллизию), остальные — его дети.
                if (rigidNode is null)
                {
                    rigidNode = gnode.CreateNode(gnode.Name + "-rigid");
                    scene.AddRigidMesh(mesh, rigidNode);
                    ctx.RigidCount++;
                }
                else
                    scene.AddRigidMesh(mesh, rigidNode.CreateNode(gnode.Name + "-part"));
                ctx.RigidByComposite[node.Owner?.name ?? "?"] = ctx.RigidByComposite.GetValueOrDefault(node.Owner?.name ?? "?") + 1;
                ctx.InstanceCount++;
                continue;
            }
            if (ctx.Opt.MergeStatic && ctx.CurrentZone is not null)
            {
                var raw = GetRawMesh(ctx, submesh);
                if (raw is not null)
                {
                    bool col = ctx.Opt.GodotCollision && !node.NoCollision && WantsCollision(ctx, mesh, material);
                    MergeInto(ctx, ZoneMergeFor(ctx, ctx.CurrentZone), raw, mat, gnode.WorldMatrix, col);
                    if (col) ctx.CollisionCount++;
                    ctx.InstanceCount++;
                    continue;
                }
            }
            if (ctx.Opt.GodotCollision && !node.NoCollision && WantsCollision(ctx, mesh, material))
            {
                // Отдельный дочерний узел с суффиксом Godot «-col»: у него и меш, и
                // трансформ родителя (свой — единичный)
                // Суффикс «+col» (не «-col»): коллизию строит tools/godot/alienforge_level_import.gd
                // сам — один тримеш на зону. Родной «-col» заставлял Godot варить 16 тысяч
                // отдельных тримешей при импорте (десятки минут) и плодил столько же узлов.
                var cnode = gnode.CreateNode(gnode.Name + "+col");
                scene.AddRigidMesh(mesh, cnode);
                ctx.CollisionCount++;
            }
            else
                scene.AddRigidMesh(mesh, gnode);
            ctx.InstanceCount++;
        }
    }

    /// <summary>Нужна ли этому мешу коллизия в Godot: не декаль/эффект и не мелочь.</summary>
    private static bool WantsCollision(GltfContext ctx, MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty> mesh, Materials.Material material)
    {
        var u = material.Shader?.Ubershader;
        if (u is SHADER_LIST.CA_DECAL or SHADER_LIST.CA_DECAL_ENVIRONMENT or SHADER_LIST.CA_FOGPLANE or SHADER_LIST.CA_FOGSPHERE
            or SHADER_LIST.CA_PARTICLE or SHADER_LIST.CA_RIBBON or SHADER_LIST.CA_LIGHT_DECAL or SHADER_LIST.CA_VOLUME_LIGHT
            or SHADER_LIST.CA_SIMPLEWATER or SHADER_LIST.CA_NONINTERACTIVE_WATER)
            return false;
        if (!ctx.MeshSize.TryGetValue(mesh, out float size))
        {
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var prim in mesh.Primitives)
                foreach (var v in prim.Vertices)
                {
                    var pos = v.Position;
                    mn = Vector3.Min(mn, pos); mx = Vector3.Max(mx, pos);
                }
            var ext = mx - mn;
            size = mn.X == float.MaxValue ? 0f : MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z));
            ctx.MeshSize[mesh] = size;
        }
        return size >= ctx.Opt.CollisionMinSize;
    }

    // ------------------------------------------------------------------ MaterialMappings

    private static Dictionary<uint, List<(Composite owner, cResource res)>> BuildAliasMappingIndex(Commands commands)
    {
        var index = new Dictionary<uint, List<(Composite, cResource)>>();
        foreach (var composite in commands.Entries)
        {
            foreach (AliasEntity alias in composite.aliases)
            {
                if (alias.GetParameter(MappingParam)?.content is not cResource r || r.shortGUID == ShortGuid.Invalid) continue;
                Entity? target;
                try { target = commands.Utils.GetResolvedTarget(commands.Utils.ResolveAlias(alias, composite)).Item2; }
                catch { continue; }
                if (target is null) continue;
                uint id = target.shortGUID.AsUInt32;
                if (!index.TryGetValue(id, out var list)) index[id] = list = new();
                list.Add((composite, r));
            }
        }
        return index;
    }

    private static MaterialMappings.MaterialMapping? ResolveMapping(Level level, ExNode modelRef,
        Dictionary<uint, List<(Composite owner, cResource res)>> aliasMappingByTarget)
    {
        if (level.MaterialMappings?.Entries is null) return null;
        // Просмотрщик: сначала алиасы с "mapping", указывающие на инстанс-scope
        // (ищутся в композитах по цепочке от ModelReference вверх к scope),
        // потом параметр "mapping" самого инстанса. Идём по scope'ам вверх,
        // пока не найдём.
        for (ExNode? scope = modelRef.MappingScope; scope is not null && scope.Entity is not null; scope = scope.MappingScope)
        {
            uint scopeId = scope.Entity.shortGUID.AsUInt32;
            MaterialMappings.MaterialMapping? found = null;
            if (aliasMappingByTarget.TryGetValue(scopeId, out var sources))
            {
                var chain = new List<Composite>();
                for (ExNode? n = modelRef; n is not null && n != scope; n = n.Parent)
                    if (n.Owner is not null && !chain.Contains(n.Owner)) chain.Add(n.Owner);
                if (scope.Owner is not null && !chain.Contains(scope.Owner)) chain.Add(scope.Owner);
                foreach (var comp in chain)
                    foreach (var (owner, r) in sources)
                        if (owner == comp)
                        {
                            var mm = FindMappingById(level, r.shortGUID);
                            if (mm is not null) found = mm;
                        }
            }
            if (found is not null) return found;
            if (scope.Entity.GetParameter(MappingParam)?.content is cResource own && own.shortGUID != ShortGuid.Invalid)
            {
                var mm = FindMappingById(level, own.shortGUID);
                if (mm is not null) return mm;
            }
        }
        return null;
    }

    private static readonly Dictionary<uint, MaterialMappings.MaterialMapping?> _mappingById = new();
    private static MaterialMappings.MaterialMapping? FindMappingById(Level level, ShortGuid id)
    {
        if (_mappingById.TryGetValue(id.AsUInt32, out var cached)) return cached;
        MaterialMappings.MaterialMapping? found = null;
        foreach (var entry in level.MaterialMappings.Entries)
        {
            // ID = ShortGuid(имя с "/" -> "\" в верхнем регистре), см. CathodeLib master
            var gen = ShortGuidUtils.Generate((entry.Name ?? "").Replace("/", "\\").ToUpperInvariant());
            if (gen == id) { found = entry; break; }
        }
        _mappingById[id.AsUInt32] = found;
        return found;
    }

    private static Materials.Material? RemapMaterial(Level level, MaterialMappings.MaterialMapping mapping, Materials.Material material)
    {
        if (string.IsNullOrEmpty(material.Name)) return null;
        var entry = mapping.Mappings.FirstOrDefault(m => m.from == material.Name)
                    ?? mapping.Mappings.FirstOrDefault(m => Normalize(m.from) == Normalize(material.Name));
        if (entry is null)
        {
            // материал уже "to" какой-то записи -> берём её from -> to (как в просмотрщике)
            var byTarget = mapping.Mappings.FirstOrDefault(m => m.to == material.Name)
                           ?? mapping.Mappings.FirstOrDefault(m => Normalize(m.to) == Normalize(material.Name));
            if (byTarget is null) return null;
            entry = mapping.Mappings.FirstOrDefault(m => m.from == byTarget.from);
            if (entry is null) return null;
        }
        if (string.IsNullOrEmpty(entry.to)) return null;
        return FindMaterialByName(level.Materials, entry.to);
    }

    private static Materials.Material? FindMaterialByName(Materials materials, string name)
    {
        var m = materials.Entries.FirstOrDefault(x => x.Name == name);
        if (m is not null) return m;
        string norm = Normalize(name);
        return materials.Entries.FirstOrDefault(x => x is not null && !string.IsNullOrEmpty(x.Name) && Normalize(x.Name) == norm);
    }

    private static string Normalize(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        string s = name;
        if (s[^1] == ']') { int i = s.LastIndexOf('['); if (i > 0) s = s[..i]; }
        s = s.ToUpperInvariant();
        if (!s.Contains("->")) s = s + "->" + s;
        return s;
    }

    // ------------------------------------------------------------------ меши

    /// <summary>Сырая геометрия сабмеша в осях glTF (для слияния по зонам).</summary>
    private sealed class RawMesh
    {
        public Vector3[] Pos = Array.Empty<Vector3>();
        public Vector3[] Nrm = Array.Empty<Vector3>();
        public Vector2[] Uv = Array.Empty<Vector2>();
        public Vector4[]? Col;                 // COLOR0: у окружения .r — чистота (маска грязи DIRT_MAP), .g — контраст шума, .b — AO
        public int[] Idx = Array.Empty<int>(); // уже с перевёрнутой обмоткой
    }

    private static RawMesh? GetRawMesh(GltfContext ctx, Models.CS2.Component.LOD.Submesh submesh)
    {
        int idx = ctx.Level.Models.GetWriteIndex(submesh);
        if (ctx.RawCache.TryGetValue(idx, out var cached)) return cached;
        RawMesh? raw = null;
        if (submesh.Data is not null && submesh.Data.Length > 0)
        {
            cMesh cm = submesh.ToMesh();
            int n = cm.Vertices.Count;
            if (n > 0 && cm.Indices.Count >= 3)
            {
                raw = new RawMesh { Pos = new Vector3[n], Nrm = new Vector3[n], Uv = new Vector2[n] };
                for (int i = 0; i < n; i++) raw.Pos[i] = new Vector3(cm.Vertices[i].X, cm.Vertices[i].Y, -cm.Vertices[i].Z);
                if (cm.Normals.Count == n)
                    for (int i = 0; i < n; i++)
                    {
                        var v = new Vector3(cm.Normals[i].X, cm.Normals[i].Y, -cm.Normals[i].Z);
                        raw.Nrm[i] = v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : Vector3.UnitY;
                    }
                else raw.Nrm = ComputeNormals(raw.Pos, cm.Indices);
                var uvs = (cm.UVs.Length > 0 && cm.UVs[0] is not null && cm.UVs[0].Count == n) ? cm.UVs[0] : null;
                if (uvs is not null) for (int i = 0; i < n; i++) raw.Uv[i] = new Vector2(uvs[i].X, uvs[i].Y);
                try { raw.Col = AlienForge.Core.Mesh.MeshDecoder.ReadVertexColors(submesh); } catch { }
                if (raw.Col is not null && raw.Col.Length != n) raw.Col = null;
                var list = new List<int>(cm.Indices.Count);
                for (int i = 0; i + 2 < cm.Indices.Count; i += 3)
                {
                    int a = cm.Indices[i], b = cm.Indices[i + 1], c = cm.Indices[i + 2];
                    if (a >= n || b >= n || c >= n) continue;
                    list.Add(a); list.Add(c); list.Add(b);
                }
                raw.Idx = list.ToArray();
            }
        }
        ctx.RawCache[idx] = raw;
        return raw;
    }

    /// <summary>Ведро слияния: (зона, материал) → меш в координатах узла зоны.</summary>
    private sealed class MergeBucket
    {
        public MaterialBuilder Material = null!;
        public List<MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>> Meshes = new();
        public int VertsInLast;
    }

    private sealed class ZoneMerge
    {
        public NodeBuilder Node = null!;
        public Matrix4x4 Inverse = Matrix4x4.Identity;
        public Dictionary<string, MergeBucket> Buckets = new();
        public MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>? Collision;
        public int CollisionVerts;
        public int Instances;
    }

    private static void MergeInto(GltfContext ctx, ZoneMerge zm, RawMesh raw, MaterialBuilder material, Matrix4x4 world, bool collision)
    {
        var toZone = world * zm.Inverse; // строчный порядок: сначала world, потом в локальные зоны
        int n = raw.Pos.Length;
        var pos = new Vector3[n];
        var nrm = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            pos[i] = Vector3.Transform(raw.Pos[i], toZone);
            var tn = Vector3.TransformNormal(raw.Nrm[i], toZone);
            nrm[i] = tn.LengthSquared() > 1e-10f ? Vector3.Normalize(tn) : Vector3.UnitY;
        }
        string key = material.Name;
        if (!zm.Buckets.TryGetValue(key, out var bucket))
            zm.Buckets[key] = bucket = new MergeBucket { Material = material };
        if (bucket.Meshes.Count == 0 || bucket.VertsInLast + n > ctx.Opt.MergeMaxVertices)
        {
            bucket.Meshes.Add(new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>($"GEO_{SafeName(key)}_{bucket.Meshes.Count}"));
            bucket.VertsInLast = 0;
        }
        var mesh = bucket.Meshes[^1];
        var prim = mesh.UsePrimitive(material);
        for (int i = 0; i + 2 < raw.Idx.Length; i += 3)
        {
            int a = raw.Idx[i], b = raw.Idx[i + 1], c = raw.Idx[i + 2];
            prim.AddTriangle(
                new VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(new VertexPositionNormal(pos[a], nrm[a]), new VertexColor1Texture1(raw.Col?[a] ?? Vector4.One, raw.Uv[a])),
                new VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(new VertexPositionNormal(pos[b], nrm[b]), new VertexColor1Texture1(raw.Col?[b] ?? Vector4.One, raw.Uv[b])),
                new VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(new VertexPositionNormal(pos[c], nrm[c]), new VertexColor1Texture1(raw.Col?[c] ?? Vector4.One, raw.Uv[c])));
        }
        bucket.VertsInLast += n;
        zm.Instances++;

        if (collision)
        {
            zm.Collision ??= new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>("COLLISION");
            var cprim = zm.Collision.UsePrimitive(ctx.Gray);
            for (int i = 0; i + 2 < raw.Idx.Length; i += 3)
            {
                int a = raw.Idx[i], b = raw.Idx[i + 1], c = raw.Idx[i + 2];
                cprim.AddTriangle(
                    new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(new VertexPositionNormal(pos[a], nrm[a]), new VertexTexture1(Vector2.Zero)),
                    new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(new VertexPositionNormal(pos[b], nrm[b]), new VertexTexture1(Vector2.Zero)),
                    new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(new VertexPositionNormal(pos[c], nrm[c]), new VertexTexture1(Vector2.Zero)));
            }
            zm.CollisionVerts += n;
        }
    }

    /// <summary>Выложить слитые меши зон в сцену (после обхода дерева).</summary>
    private static void FlushMerged(GltfContext ctx, SceneBuilder scene)
    {
        int meshes = 0, cols = 0;
        foreach (var zm in ctx.Merges.Values)
        {
            foreach (var bucket in zm.Buckets.Values)
                foreach (var mesh in bucket.Meshes)
                {
                    var node = zm.Node.CreateNode(mesh.Name);
                    scene.AddRigidMesh(mesh, node);
                    meshes++;
                }
            if (zm.Collision is not null)
            {
                // «+col»: скрипт импорта Godot делает из него StaticBody с тримешем и убирает меш
                var node = zm.Node.CreateNode("COLLISION+colonly");
                scene.AddRigidMesh(zm.Collision, node);
                cols++;
            }
        }
        Log($"слияние: зон {ctx.Merges.Count}, слитых мешей {meshes}, коллизий {cols}");
    }

    private static ZoneMerge ZoneMergeFor(GltfContext ctx, NodeBuilder zoneNode)
    {
        if (!ctx.Merges.TryGetValue(zoneNode, out var zm))
        {
            Matrix4x4.Invert(zoneNode.WorldMatrix, out var inv);
            ctx.Merges[zoneNode] = zm = new ZoneMerge { Node = zoneNode, Inverse = inv };
        }
        return zm;
    }

    private static MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>? GetMesh(GltfContext ctx,
        Models.CS2.Component.LOD.Submesh submesh, MaterialBuilder material)
    {
        int idx = ctx.Level.Models.GetWriteIndex(submesh);
        // ключ: сабмеш + материал (в glTF материал живёт в примитиве)
        int key = HashCode.Combine(idx, material.Name);
        if (ctx.MeshCache.TryGetValue(key, out var cached)) return cached;

        MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>? mesh = null;
        if (submesh.Data is not null && submesh.Data.Length > 0)
        {
            if (ctx.Opt.Verbose && submesh.VertexFormatFull is not null)
                foreach (var block in submesh.VertexFormatFull.Attributes)
                    foreach (var attr in block)
                    {
                        string k = $"{attr.Usage}/{attr.Type}";
                        AttrStats[k] = AttrStats.GetValueOrDefault(k) + 1;
                    }
            cMesh cm = submesh.ToMesh();
            if (cm.Vertices.Count > 0 && cm.Indices.Count >= 3)
            {
                var cs2 = ctx.Level.Models.FindModel(submesh);
                var lod = ctx.Level.Models.FindModelLOD(submesh);
                string name = SafeName($"{cs2?.Name ?? "model"}:{lod?.Name ?? "lod"}");
                mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(name);
                var prim = mesh.UsePrimitive(material);

                int n = cm.Vertices.Count;
                var pos = new Vector3[n];
                for (int i = 0; i < n; i++) pos[i] = new Vector3(cm.Vertices[i].X, cm.Vertices[i].Y, -cm.Vertices[i].Z);
                Vector3[] nrm;
                if (cm.Normals.Count == n)
                {
                    nrm = new Vector3[n];
                    for (int i = 0; i < n; i++)
                    {
                        var v = new Vector3(cm.Normals[i].X, cm.Normals[i].Y, -cm.Normals[i].Z);
                        nrm[i] = v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : Vector3.UnitY;
                    }
                }
                else nrm = ComputeNormals(pos, cm.Indices);
                var uvs = (cm.UVs.Length > 0 && cm.UVs[0] is not null && cm.UVs[0].Count == n) ? cm.UVs[0] : null;
                // Цвет вершин (COLOR0): у окружения умножает альбедо (VERTEX_COLOUR),
                // у персонажей .r — вес грязи. Читаем своим декодером — CathodeLib
                // путает порядок байтов D3DCOLOR.
                Vector4[]? cols = null;
                try { cols = AlienForge.Core.Mesh.MeshDecoder.ReadVertexColors(submesh); } catch { }
                if (cols is not null && cols.Length != n) cols = null;

                // Обмотка: после зеркалирования Z (левосторонняя Cathode -> правосторонний
                // glTF) треугольники надо перевернуть (a, c, b), иначе 99.6% граней
                // смотрят против своих же нормалей (проверено по экспорту госпиталя).
                // Просмотрщик OpenCAGE этого не делает, но у него unshaded-шейдер и
                // ему всё равно; WPF-экспортёр OpenCAGE переворачивает так же.
                for (int i = 0; i + 2 < cm.Indices.Count; i += 3)
                {
                    int a = cm.Indices[i], b = cm.Indices[i + 1], c = cm.Indices[i + 2];
                    if (a >= n || b >= n || c >= n) continue;
                    prim.AddTriangle(MakeVertex(pos, nrm, uvs, cols, a), MakeVertex(pos, nrm, uvs, cols, c), MakeVertex(pos, nrm, uvs, cols, b));
                }
            }
        }
        ctx.MeshCache[key] = mesh;
        return mesh;
    }

    internal static readonly Dictionary<string, int> AttrStats = new();

    private static VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty> MakeVertex(Vector3[] pos, Vector3[] nrm, List<Vector2>? uvs, Vector4[]? cols, int i)
    {
        var uv = uvs is null ? Vector2.Zero : new Vector2(uvs[i].X, uvs[i].Y);
        var col = cols is null ? Vector4.One : cols[i];
        return new VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(new VertexPositionNormal(pos[i], nrm[i]), new VertexColor1Texture1(col, uv));
    }

    private static Vector3[] ComputeNormals(Vector3[] pos, List<ushort> indices)
    {
        var acc = new Vector3[pos.Length];
        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a >= pos.Length || b >= pos.Length || c >= pos.Length) continue;
            var fn = Vector3.Cross(pos[c] - pos[a], pos[b] - pos[a]); // обмотка (a, c, b), см. GetMesh
            acc[a] += fn; acc[b] += fn; acc[c] += fn;
        }
        for (int i = 0; i < acc.Length; i++)
            acc[i] = acc[i].LengthSquared() > 1e-12f ? Vector3.Normalize(acc[i]) : Vector3.UnitY;
        return acc;
    }

    // ------------------------------------------------------------------ материалы

    /// <summary>
    /// null = материал не поддерживается просмотрщиком (нет diffuse-сэмплера
    /// и нет separate alpha) — меш не рисуется.
    /// </summary>
    private static MaterialBuilder? GetMaterial(GltfContext ctx, Materials.Material material, FunctionEntity modelRef)
    {
        var shader = material.Shader;
        if (shader is null) return null;

        // Overrides цвета с ModelReference (только CA_ENVIRONMENT)
        Vector4 diffuseScale = Vector4.One;
        if (shader.Ubershader == SHADER_LIST.CA_ENVIRONMENT)
        {
            var dcs = GetVec3Param(modelRef, "diffuse_colour_scale", new Vector3(255, 255, 255));
            float dos = GetFloatParam(modelRef, "diffuse_opacity_scale", 1f);
            diffuseScale = new Vector4(Math.Clamp(dcs.X, 0, 255) / 255f, Math.Clamp(dcs.Y, 0, 255) / 255f, Math.Clamp(dcs.Z, 0, 255) / 255f, Math.Clamp(dos, 0, 255));
        }

        int matIdx = ctx.Level.Materials.GetWriteIndex(material);
        string cacheKey = $"{matIdx}|{diffuseScale}";
        if (ctx.MaterialCache.TryGetValue(cacheKey, out var cachedMat)) return cachedMat;

        // Вода и преломление: в игре это прозрачная поверхность с рябью
        // (NORMAL_MAP + маска), диффуза нет. Отдаём тёмное полупрозрачное стекло
        // с картой нормалей, а не синюю normal-карту как цвет.
        // (CA_REFRACTION — стёкла-искажения над лампами — как и раньше не
        // экспортируются: в игре это только преломление, поверхности нет)
        if (shader.Ubershader is SHADER_LIST.CA_SIMPLEWATER or SHADER_LIST.CA_NONINTERACTIVE_WATER)
        {
            var wb = new MaterialBuilder(SafeName($"{material.Name} [{shader.Ubershader}]"));
            if (ctx.Opt.Unlit) wb.WithUnlitShader(); else wb.WithMetallicRoughnessShader().WithMetallicRoughness(0f, 0.05f);
            wb.WithBaseColor(new Vector4(0.02f, 0.04f, 0.05f, 0.45f));
            wb.WithAlpha(AlphaMode.BLEND);
            wb.WithDoubleSide(true);
            if (ctx.Opt.AllMaps && ctx.Opt.Textures)
            {
                var wl = ShaderLayout.Read(material);
                var nslot = wl.Slots.FirstOrDefault(x => x.SamplerName == "NORMAL_MAP");
                var nimg = nslot?.Texture is not null ? GetPlainImage(ctx, nslot.Texture) : null;
                if (nimg is not null) wb.UseChannel(KnownChannel.Normal).UseTexture().WithPrimaryImage(nimg);
                wb.Extras = BuildExtras(wl, diffuseScale);
            }
            ctx.MaterialCache[cacheKey] = wb;
            return wb;
        }

        int diffuseSampler = GetDiffuseSamplerIndex(shader.Ubershader);
        if (diffuseSampler < 0 && ctx.Opt.Decals && shader.Ubershader == SHADER_LIST.CA_DECAL)
            diffuseSampler = 0; // CA_DECAL.SAMPLERS.DIFFUSE_MAP (из OpenCAGE MaterialApplier)
        Textures.TEX4? separateAlpha = null;
        if (HasFeature(shader, "SEPARATE_ALPHA"))
        {
            int sa = GetSeparateAlphaSamplerIndex(shader.Ubershader);
            if (sa >= 0) separateAlpha = GetSamplerTexture(material, shader, sa);
        }
        if (diffuseSampler < 0 && separateAlpha is null) return null;

        Textures.TEX4? diffuse = diffuseSampler >= 0 ? GetSamplerTexture(material, shader, diffuseSampler) : null;
        if (diffuse is null && HasFeature(shader, "SECONDARY_DIFFUSE_MAPPING"))
        {
            int sd = GetSecondaryDiffuseSamplerIndex(shader.Ubershader);
            if (sd >= 0) diffuse = GetSamplerTexture(material, shader, sd);
        }

        bool transparent = ShouldUseTransparentBlend(shader, separateAlpha is not null)
                           || shader.Ubershader == SHADER_LIST.CA_DECAL; // декали всегда блендятся поверх
        bool cutout = HasFeature(shader, "ALPHA_TEST") && IsRenderStateEnabled(shader, Shaders.RenderState.AlphaTestEnable);
        bool preserveAlpha = transparent || cutout;
        var p = GetParams(shader.Ubershader);
        Vector4 tint = GetDiffuseTint(material, shader, p, preserveAlpha);
        // glTF: baseColorFactor строго 0..1 (в шейдере просмотрщика множители могут быть >1)
        tint = new Vector4(
            Math.Clamp(tint.X * diffuseScale.X, 0f, 1f), Math.Clamp(tint.Y * diffuseScale.Y, 0f, 1f),
            Math.Clamp(tint.Z * diffuseScale.Z, 0f, 1f), Math.Clamp(tint.W * diffuseScale.W, 0f, 1f));
        float uvMult = GetFloat(shader, material, p.DiffuseUvMultIndex, 1f);
        float saUvMult = GetFloat(shader, material, GetSeparateAlphaUvMultIndex(shader.Ubershader), 1f);
        float cutoff = Math.Clamp(GetFloat(shader, material, shader.Ubershader == SHADER_LIST.CA_DECAL_ENVIRONMENT ? 51 : -1, 0.5f), 0f, 1f);
        bool separateFromGreen = HasFeature(shader, "SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL");
        bool doubleSided = HasFeature(shader, "DOUBLE_SIDED");

        string matName = SafeName($"{material.Name} [{shader.Ubershader}]");
        var mb = new MaterialBuilder(matName);
        if (ctx.Opt.Unlit) mb.WithUnlitShader(); else mb.WithMetallicRoughnessShader().WithMetallicRoughness(0f, 1f);
        mb.WithDoubleSide(doubleSided);

        ImageBuilder? image = null;
        if (ctx.Opt.Textures)
        {
            // alpha_from_luminance: separate alpha без прозрачности, либо cutout
            // на диффузе без альфы — альфа = яркость
            image = GetImage(ctx, diffuse, separateAlpha, separateFromGreen, preserveAlpha, cutout, saUvMult / Math.Max(uvMult, 1e-6f));
        }
        if (image is not null)
        {
            var ch = mb.UseChannel(KnownChannel.BaseColor);
            ch.Parameter = tint;
            var tex = ch.UseTexture();
            tex.WithPrimaryImage(image);
            if (Math.Abs(uvMult - 1f) > 1e-4f) tex.WithTransform(Vector2.Zero, new Vector2(uvMult, uvMult));
        }
        else
        {
            mb.WithBaseColor(new Vector4(tint.X, tint.Y, tint.Z, preserveAlpha ? tint.W : 1f));
        }

        if (transparent) mb.WithAlpha(AlphaMode.BLEND);
        else if (cutout) mb.WithAlpha(AlphaMode.MASK, cutoff);
        else mb.WithAlpha(AlphaMode.OPAQUE);

        // Остальные карты + extras (роли каналов из дизассемблированных шейдеров,
        // фичи, параметры) — то же, что пишет экспорт модели; см. alienforge_blender.py
        var layout = ShaderLayout.Read(material);
        if (ctx.Opt.AllMaps && ctx.Opt.Textures)
        {
            foreach (var slot in layout.Slots)
            {
                if (slot.SamplerName is null || slot.Texture is null) continue;
                KnownChannel? channel = slot.SamplerName switch
                {
                    "NORMAL_MAP" => KnownChannel.Normal,
                    "SPECULAR_MAP" => KnownChannel.MetallicRoughness, // собирается как MR-карта, см. GetMetallicRoughnessImage
                    "AMBIENT_OCCLUSION_MAP" => KnownChannel.Occlusion,
                    "GLOW_MAP" => KnownChannel.Emissive, // LIGHTMAP_MAP — запечённый свет, не свечение; остаётся в extras
                    // карты, которых в glTF нет, но нужные своему движку (AlienDeferred читает их по имени из extras.shader):
                    // грязь (DIRT_MAPPING: умножает диффуз по маске из цвета вершин) и шум альфа-смешивания — кладём в
                    // «чужие» каналы KHR-расширений, чтобы картинки попали в файл
                    "DIRT_MAP" => KnownChannel.ClearCoat,
                    "ALPHABLEND_NOISE_MAP" => KnownChannel.SheenColor,
                    _ => null,
                };
                if (channel is null) continue;
                // канал MetallicRoughness уже создан с константами (WithMetallicRoughness) — проверяем именно текстуру,
                // иначе спекуляр уровня никогда не экспортировался (плоские матовые материалы)
                if (mb.GetChannel(channel.Value)?.Texture is not null) continue;
                var extraImage = channel == KnownChannel.MetallicRoughness
                    ? GetMetallicRoughnessImage(ctx, slot.Texture, layout)
                    : GetPlainImage(ctx, slot.Texture);
                if (extraImage is null) continue;
                var chb = mb.UseChannel(channel.Value);
                chb.UseTexture().WithPrimaryImage(extraImage);
                if (channel == KnownChannel.Emissive) chb.Parameters[KnownProperty.RGB] = new Vector3(1, 1, 1);
                if (channel == KnownChannel.MetallicRoughness)
                {
                    chb.Parameters[KnownProperty.MetallicFactor] = 1f;
                    chb.Parameters[KnownProperty.RoughnessFactor] = 1f;
                }
            }
        }
        // Свечение (фича EMISSIVE): лампы/экраны/надписи светятся своим диффузом
        if (!ctx.Opt.Unlit)
        {
            var (ec, es) = layout.Emission();
            if (es > 0f && image is not null)
            {
                var em = mb.UseChannel(KnownChannel.Emissive);
                em.UseTexture().WithPrimaryImage(image);
                em.Parameters[KnownProperty.RGB] = ec;
                em.Parameters[KnownProperty.EmissiveStrength] = Math.Max(1f, es * 4f);
            }
        }
        mb.Extras = BuildExtras(layout, diffuseScale);

        ctx.MaterialCache[cacheKey] = mb;
        return mb;
    }

    private static Vector3 GetVec3Param(Entity e, string name, Vector3 def)
    {
        if (e.GetParameter(name)?.content is cVector3 v) return new Vector3(v.value.X, v.value.Y, v.value.Z);
        return def;
    }
    private static float GetFloatParam(Entity e, string name, float def)
    {
        var c = e.GetParameter(name)?.content;
        if (c is cFloat f) return f.value;
        if (c is cInteger i) return i.value;
        return def;
    }

    /// <summary>Диагностика: где диффуз по таблице просмотрщика расходится с привязкой сэмплера.</summary>
    public static void DiagnoseMaterials(Level lvl)
    {
        int total = 0, mismatch = 0;
        foreach (var material in lvl.Materials.Entries)
        {
            var shader = material.Shader;
            if (shader is null) continue;
            total++;
            int ds = GetDiffuseSamplerIndex(shader.Ubershader);
            if (ds < 0 && shader.Ubershader == SHADER_LIST.CA_DECAL) ds = 0;
            var byTable = ds >= 0 ? GetSamplerTexture(material, shader, ds) : null;
            var layout = ShaderLayout.Read(material);
            var bySampler = layout.First(TextureRole.Diffuse)?.Texture;
            string tn = byTable?.Name ?? "-", sn = bySampler?.Name ?? "-";
            // тинт и UV: таблица индексов против параметров по именам
            var pp = GetParams(shader.Ubershader);
            var tintT = GetDiffuseTint(material, shader, pp, true);
            float uvT = GetFloat(shader, material, pp.DiffuseUvMultIndex, 1f);
            var tintP = layout.DiffuseTint;
            float uvP = layout.DiffuseUvMult;
            if ((tintT - tintP).Length() > 0.01f || MathF.Abs(uvT - uvP) > 0.01f)
            {
                mismatch++;
                if (mismatch <= 400)
                    Log($"  {material.Name} [{shader.Ubershader}] ТИНТ таблица {tintT} / имя {tintP}; UV таблица {uvT} / имя {uvP}");
                continue;
            }
            bool suspicious = tn.Contains("[n]", StringComparison.OrdinalIgnoreCase) || (byTable is not null && byTable.Format == Textures.TextureFormat.DXN);
            if (shader.Ubershader is SHADER_LIST.CA_PARTICLE or SHADER_LIST.CA_RIBBON or SHADER_LIST.CA_FOGPLANE or SHADER_LIST.CA_FOGSPHERE) continue;
            if (tn != sn || suspicious)
            {
                mismatch++;
                if (mismatch <= 400)
                    Log($"  {material.Name} [{shader.Ubershader}] таблица: {tn} ({byTable?.Format}) | сэмплер: {sn}");
            }
        }
        Log($"материалов {total}, расхождений {mismatch}");
    }

    // --- таблицы из просмотрщика (AlienSceneMaterials / AlienSceneShaderParams)

    private static int GetDiffuseSamplerIndex(SHADER_LIST u) => u switch
    {
        SHADER_LIST.CA_ENVIRONMENT => 1,
        SHADER_LIST.CA_DECAL_ENVIRONMENT => 4,
        SHADER_LIST.CA_CHARACTER => 3,
        SHADER_LIST.CA_SKIN => 1,
        SHADER_LIST.CA_HAIR => 1,
        SHADER_LIST.CA_EYE => 1,
        SHADER_LIST.CA_SKIN_OCCLUSION => 0,
        SHADER_LIST.CA_SKYDOME => 0,
        SHADER_LIST.CA_SURFACE_EFFECTS => 0,
        SHADER_LIST.CA_EFFECT_OVERLAY => 0,
        SHADER_LIST.CA_TERRAIN => 0,
        // Вода: у просмотрщика тут 0, но сэмплер 0 у воды — NORMAL_MAP, и лужи
        // выходили синими нормалями. Вода собирается отдельно (см. GetMaterial).
        SHADER_LIST.CA_NONINTERACTIVE_WATER => -1,
        SHADER_LIST.CA_SIMPLEWATER => -1,
        SHADER_LIST.CA_PLANET => 0,
        SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT => 5,
        SHADER_LIST.CA_STREAMER => 3,
        SHADER_LIST.CA_LOW_LOD_CHARACTER => 0,
        SHADER_LIST.CA_SPACESUIT_VISOR => 3, // FACE_MAP; сэмплер 1 у визора — NORMAL_MAP
        SHADER_LIST.CA_CAMERA_MAP => 0,
        _ => -1,
    };

    private static int GetSeparateAlphaSamplerIndex(SHADER_LIST u) => u switch
    {
        SHADER_LIST.CA_ENVIRONMENT => 0,
        SHADER_LIST.CA_DECAL_ENVIRONMENT => 3,
        SHADER_LIST.CA_CHARACTER => 2,
        SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT => 4,
        SHADER_LIST.CA_STREAMER => 2,
        _ => -1,
    };

    private static int GetSecondaryDiffuseSamplerIndex(SHADER_LIST u) => u switch
    {
        SHADER_LIST.CA_ENVIRONMENT => 2,
        SHADER_LIST.CA_DECAL_ENVIRONMENT => 5,
        SHADER_LIST.CA_CHARACTER => 4,
        SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT => 6,
        SHADER_LIST.CA_STREAMER => 4,
        _ => -1,
    };

    private static int GetSeparateAlphaUvMultIndex(SHADER_LIST u) => u switch
    {
        SHADER_LIST.CA_ENVIRONMENT => 5,
        SHADER_LIST.CA_DECAL_ENVIRONMENT => 9,
        SHADER_LIST.CA_CHARACTER => 14,
        SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT => 10,
        SHADER_LIST.CA_STREAMER => 15,
        _ => -1,
    };

    private readonly record struct MatParams(int DiffuseTintIndex, int DiffuseUvMultIndex, string TintName);
    private static MatParams GetParams(SHADER_LIST u) => u switch
    {
        SHADER_LIST.CA_ENVIRONMENT => new(7, 6, "DIFFUSE_TINT"),
        SHADER_LIST.CA_DECAL_ENVIRONMENT => new(11, 10, "DIFFUSE_TINT"),
        SHADER_LIST.CA_CHARACTER => new(16, 15, "DIFFUSE_TINT"),
        SHADER_LIST.CA_SKIN => new(5, 4, "DIFFUSE_TINT"),
        SHADER_LIST.CA_HAIR => new(2, 1, "DIFFUSE_TINT"),
        SHADER_LIST.CA_TERRAIN => new(4, 3, "DIFFUSE_TINT"),
        SHADER_LIST.CA_SURFACE_EFFECTS => new(5, 4, "DIFFUSE_TINT"),
        SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT => new(12, 11, "DIFFUSE_TINT"),
        SHADER_LIST.CA_STREAMER => new(17, 16, "DIFFUSE_TINT"),
        SHADER_LIST.CA_LOW_LOD_CHARACTER => new(6, 5, "DIFFUSE_TINT"),
        SHADER_LIST.CA_EFFECT_OVERLAY => new(0, -1, "COLOUR_TINT"),
        SHADER_LIST.CA_PLANET => new(16, -1, "ATMOSPHERE_RIM_COLOUR"),
        _ => new(-1, -1, "DIFFUSE_TINT"),
    };

    private static Vector4 GetDiffuseTint(Materials.Material material, Shaders.Shader shader, MatParams p, bool preserveAlpha)
    {
        if (p.DiffuseTintIndex < 0 || p.DiffuseTintIndex >= shader.PixelShaderParameterRemaps.Count) return Vector4.One;
        int r = shader.PixelShaderParameterRemaps[p.DiffuseTintIndex];
        var consts = material.PixelShaderConstants;
        if (r == 255 || r >= consts.Count) return Vector4.One;
        float x = 0, y = 0, z = 0, w = 1;
        var type = ShaderUtility.GetParameterType(shader.Ubershader, p.TintName);
        int comps = type switch
        {
            UberShaderParameterType.Float3 or UberShaderParameterType.Half3 => 3,
            UberShaderParameterType.Float4 or UberShaderParameterType.Half4 => 4,
            null => 4,
            _ => 4,
        };
        if (r < consts.Count) x = consts[r];
        if (r + 1 < consts.Count) y = consts[r + 1];
        if (r + 2 < consts.Count) z = consts[r + 2];
        if (comps == 4 && r + 3 < consts.Count) w = consts[r + 3];
        if (comps == 4 && type is null && r + 3 >= consts.Count) return Vector4.One;
        x = Math.Clamp(x, 0, 1); y = Math.Clamp(y, 0, 1); z = Math.Clamp(z, 0, 1); w = Math.Clamp(w, 0, 1);
        if (!preserveAlpha) w = 1f;
        return new Vector4(x, y, z, w);
    }

    private static float GetFloat(Shaders.Shader shader, Materials.Material material, int index, float fallback)
    {
        if (index < 0 || shader.PixelShaderParameterRemaps.Count <= index) return fallback;
        int r = shader.PixelShaderParameterRemaps[index];
        if (r == 255 || r >= material.PixelShaderConstants.Count) return fallback;
        return material.PixelShaderConstants[r];
    }

    private static Textures.TEX4? GetSamplerTexture(Materials.Material material, Shaders.Shader shader, int sampler)
    {
        if (sampler < 0 || sampler >= shader.SamplerRemaps.Count) return null;
        int ti = shader.SamplerRemaps[sampler];
        if (ti == 255 || ti < 0 || ti >= material.TextureReferences.Count) return null;
        return material.TextureReferences[ti]?.Texture;
    }

    private static bool HasFeature(Shaders.Shader shader, string feature)
    {
        int? idx = ShaderUtility.GetShaderFunctionalityIndex(shader.Ubershader, ShaderIndexType.FEATURES, feature);
        return idx.HasValue && (shader.UbershaderFeatureFlags & (1L << idx.Value)) != 0;
    }

    private static readonly string[] AlphaBlendFeatures = { "USE_ALPHA_AS_BLENDFACTOR", "FORCE_TO_ALPHA", "GLASS", "FOG_ALPHA", "VERTEX_ALPHA_OPACITY_ONLY" };

    private static bool ShouldUseTransparentBlend(Shaders.Shader shader, bool hasSeparateAlpha)
    {
        if (shader.Ubershader == SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT)
            return AlphaBlendFeatures.Any(f => HasFeature(shader, f));
        if (hasSeparateAlpha) return true;
        long req = shader.UbershaderRequirementFlags;
        if ((req & 0x1000000L) != 0 || (req & 0x400000000L) != 0 || (req & 0x400000000000L) != 0 || (req & 0x40000000L) != 0 || (req & 0x4000000000000L) != 0)
            return true;
        if (AlphaBlendFeatures.Any(f => HasFeature(shader, f))) return true;
        if (HasFeature(shader, "DECAL")) return true;
        return false;
    }

    private static bool IsRenderStateEnabled(Shaders.Shader shader, Shaders.RenderState state)
    {
        var entries = shader.RenderStates?.Entries;
        if (entries is null) return false;
        foreach (var e in entries) if (e.StateId == (int)state) return e.Value != 0;
        return false;
    }

    // ------------------------------------------------------------------ текстуры

    /// <summary>
    /// MR-карта glTF из spec-карты игры: R = F0 (spec.r × SPECULAR_TINT), G = roughness
    /// = 1 − spec.g × SPECULAR_POWER, B = металл только при
    /// SPECULAR_MAPPING_METALNESS_MASKING. Сырая spec-карта в B несёт маску, а не
    /// металл, и напрямую делала поверхности металлическими (синие отражения HDRI).
    /// </summary>
    private static ImageBuilder? GetMetallicRoughnessImage(GltfContext ctx, Textures.TEX4 tex, ShaderLayout layout)
    {
        float tint = layout.Param("SPECULAR_TINT", 1.0f);
        if (layout.Ubershader == "CA_SKIN") tint *= 0.28f; // кодировка блика кожи: F0 = 0.28·spec
        float power = layout.Param("SPECULAR_POWER", 1.0f);
        bool metal = layout.HasFeature("SPECULAR_MAPPING_METALNESS_MASKING");
        string key = $"mr|{tex.Name}|{tint:0.###}|{power:0.###}|{metal}";
        if (ctx.ImageCache.TryGetValue(key, out var cached)) return cached;
        ImageBuilder? result = null;
        try
        {
            using var img = DecodeTexture(tex);
            if (img is not null)
            {
                img.ProcessPixelRows(acc =>
                {
                    for (int y = 0; y < acc.Height; y++)
                    {
                        var row = acc.GetRowSpan(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            var px = row[x];
                            float r = px.R / 255f, g = px.G / 255f, b = px.B / 255f;
                            px.R = (byte)Math.Clamp(r * tint * 255f, 0, 255);
                            px.G = (byte)Math.Clamp((1f - Math.Min(g * power, 0.999f)) * 255f, 0, 255);
                            px.B = (byte)(metal ? Math.Clamp(b * 255f, 0, 255) : 0);
                            px.A = 255;
                            row[x] = px;
                        }
                    }
                });
                using var ms = new MemoryStream();
                img.SaveAsPng(ms);
                result = ImageBuilder.From(new SharpGLTF.Memory.MemoryImage(ms.ToArray()), SafeName(Path.GetFileNameWithoutExtension(tex.Name ?? "spec")) + "_mr");
            }
        }
        catch (Exception ex)
        {
            if (ctx.Opt.Verbose) Log($"   spec-текстура {tex.Name}: {ex.Message}");
        }
        ctx.ImageCache[key] = result;
        return result;
    }

    /// <summary>Карта как есть (normal/spec/AO...), PNG, с кэшем по имени.</summary>
    private static ImageBuilder? GetPlainImage(GltfContext ctx, Textures.TEX4 tex)
    {
        string key = $"plain|{tex.Name}";
        if (ctx.ImageCache.TryGetValue(key, out var cached)) return cached;
        ImageBuilder? result = null;
        try
        {
            using var img = DecodeTexture(tex);
            if (img is not null)
            {
                using var ms = new MemoryStream();
                img.SaveAsPng(ms);
                result = ImageBuilder.From(new SharpGLTF.Memory.MemoryImage(ms.ToArray()), SafeName(Path.GetFileNameWithoutExtension(tex.Name ?? "tex")));
            }
        }
        catch (Exception ex)
        {
            if (ctx.Opt.Verbose) Log($"   текстура {tex.Name}: {ex.Message}");
        }
        ctx.ImageCache[key] = result;
        return result;
    }

    private static System.Text.Json.Nodes.JsonNode BuildExtras(ShaderLayout layout, Vector4 diffuseScale)
    {
        var channels = new System.Text.Json.Nodes.JsonArray();
        foreach (var slot in layout.Slots)
        {
            var ch = layout.ChannelsOf(slot);
            channels.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["slot"] = slot.Index,
                ["sampler"] = slot.SamplerName,
                ["texture"] = slot.TextureName,
                ["image"] = SafeName(Path.GetFileNameWithoutExtension(slot.TextureName)) + (slot.SamplerName == "SPECULAR_MAP" ? "_mr" : ""),
                ["derived_mr"] = slot.SamplerName == "SPECULAR_MAP",
                ["R"] = ch.R, ["G"] = ch.G, ["B"] = ch.B, ["A"] = ch.A,
                ["blender"] = ch.Blender,
            });
        }
        var features = new System.Text.Json.Nodes.JsonArray();
        foreach (var f in layout.Features) features.Add(f);
        var parameters = new System.Text.Json.Nodes.JsonObject();
        foreach (var kv in layout.Parameters)
        {
            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var v in kv.Value) arr.Add(v);
            parameters[kv.Key] = arr;
        }
        var extras = new System.Text.Json.Nodes.JsonObject
        {
            ["ubershader"] = layout.Ubershader,
            ["shader"] = channels,
            ["features"] = features,
            ["features_csv"] = string.Join(",", layout.Features),
            ["params"] = parameters,
            ["alpha"] = layout.Transparent ? "BLEND" : layout.Cutout ? "MASK" : "OPAQUE",
            ["diffuse_colour_scale"] = new System.Text.Json.Nodes.JsonArray { diffuseScale.X, diffuseScale.Y, diffuseScale.Z, diffuseScale.W },
        };
        extras["alienforge_json"] = extras.ToJsonString();
        return extras;
    }

    private static ImageBuilder? GetImage(GltfContext ctx, Textures.TEX4? diffuse, Textures.TEX4? separateAlpha,
        bool separateFromGreen, bool preserveAlpha, bool cutout, float saUvRatio)
    {
        if (diffuse is null && separateAlpha is null) return null;
        string key = $"{diffuse?.Name ?? "-"}|{separateAlpha?.Name ?? "-"}|{separateFromGreen}|{preserveAlpha}|{cutout}";
        if (ctx.ImageCache.TryGetValue(key, out var cached)) return cached;

        ImageBuilder? result = null;
        try
        {
            Image<Rgba32>? img = diffuse is not null ? DecodeTexture(diffuse) : null;
            Image<Rgba32>? alphaImg = separateAlpha is not null ? DecodeTexture(separateAlpha) : null;

            if (img is null && alphaImg is not null)
            {
                // нет диффуза, только альфа: белая картинка с этой альфой
                img = new Image<Rgba32>(alphaImg.Width, alphaImg.Height, new Rgba32(255, 255, 255, 255));
            }
            if (img is not null)
            {
                if (alphaImg is not null)
                {
                    bool alphaHasTransparency = HasTransparency(alphaImg);
                    BakeSeparateAlpha(img, alphaImg, separateFromGreen, !alphaHasTransparency, saUvRatio);
                }
                else if (cutout && !HasTransparency(img))
                {
                    // alpha_from_luminance
                    img.ProcessPixelRows(acc =>
                    {
                        for (int y = 0; y < acc.Height; y++)
                        {
                            var row = acc.GetRowSpan(y);
                            for (int x = 0; x < row.Length; x++)
                            {
                                var px = row[x];
                                px.A = (byte)Math.Clamp((0.299f * px.R + 0.587f * px.G + 0.114f * px.B), 0, 255);
                                row[x] = px;
                            }
                        }
                    });
                }
                else if (!preserveAlpha)
                {
                    // непрозрачный материал: альфу текстуры игнорируем (иначе Blender покажет дыры)
                    img.ProcessPixelRows(acc =>
                    {
                        for (int y = 0; y < acc.Height; y++)
                        {
                            var row = acc.GetRowSpan(y);
                            for (int x = 0; x < row.Length; x++) { var px = row[x]; px.A = 255; row[x] = px; }
                        }
                    });
                }
                using var ms = new MemoryStream();
                img.SaveAsPng(ms);
                string imgName = SafeName(Path.GetFileNameWithoutExtension(diffuse?.Name ?? separateAlpha?.Name ?? "tex"));
                result = ImageBuilder.From(new SharpGLTF.Memory.MemoryImage(ms.ToArray()), imgName);
                img.Dispose();
                alphaImg?.Dispose();
            }
        }
        catch (Exception ex)
        {
            if (ctx.Opt.Verbose) Log($"   текстура {diffuse?.Name}: {ex.Message}");
            result = null;
        }
        ctx.ImageCache[key] = result;
        return result;
    }

    private static void BakeSeparateAlpha(Image<Rgba32> img, Image<Rgba32> alpha, bool fromGreen, bool fromLuminance, float uvRatio)
    {
        int w = img.Width, h = img.Height, aw = alpha.Width, ah = alpha.Height;
        var abuf = new byte[aw * ah];
        alpha.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < ah; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < aw; x++)
                {
                    var px = row[x];
                    byte a = fromLuminance ? (byte)Math.Clamp(0.299f * px.R + 0.587f * px.G + 0.114f * px.B, 0, 255) : (fromGreen ? px.G : px.A);
                    abuf[y * aw + x] = a;
                }
            }
        });
        float ratio = uvRatio <= 0 ? 1f : uvRatio;
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    // отдельная альфа может тайлиться с другим множителем UV
                    float u = (x / (float)w) * ratio, v = (y / (float)h) * ratio;
                    int ax = ((int)(u * aw) % aw + aw) % aw, ay = ((int)(v * ah) % ah + ah) % ah;
                    var px = row[x]; px.A = abuf[ay * aw + ax]; row[x] = px;
                }
            }
        });
    }

    private static bool HasTransparency(Image<Rgba32> img)
    {
        bool has = false;
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height && !has; y += Math.Max(1, acc.Height / 64))
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x += Math.Max(1, row.Length / 64))
                    if (row[x].A < 250) { has = true; break; }
            }
        });
        return has;
    }

    private static readonly BcDecoder _bc = new();

    /// <summary>Мип 0 текстуры (streamed, если есть, иначе persistent) в RGBA8.</summary>
    private static Image<Rgba32>? DecodeTexture(Textures.TEX4 tex)
    {
        Textures.TEX4.Texture part = (tex.TextureStreamed?.Content is { Length: > 0 } && tex.TextureStreamed.Width > 0) ? tex.TextureStreamed : tex.TexturePersistent;
        if (part?.Content is null || part.Content.Length == 0 || part.Width <= 0 || part.Height <= 0) return null;
        int w = part.Width, h = part.Height;
        byte[] data = part.Content;

        CompressionFormat? bc = tex.Format switch
        {
            Textures.TextureFormat.DXT1 => CompressionFormat.Bc1WithAlpha,
            Textures.TextureFormat.DXT3 => CompressionFormat.Bc2,
            Textures.TextureFormat.DXT5 => CompressionFormat.Bc3,
            Textures.TextureFormat.DXN => CompressionFormat.Bc5,
            Textures.TextureFormat.BC7 => CompressionFormat.Bc7,
            Textures.TextureFormat.BC6H => CompressionFormat.Bc6U,
            _ => null,
        };
        if (bc is not null)
        {
            int block = (bc == CompressionFormat.Bc1 || bc == CompressionFormat.Bc1WithAlpha) ? 8 : 16;
            int mip0 = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * block;
            if (data.Length < mip0) return null;
            byte[] mip = data.Length == mip0 ? data : data.AsSpan(0, mip0).ToArray();
            ColorRgba32[] pixels = _bc.DecodeRaw(mip, w, h, bc.Value);
            var img = new Image<Rgba32>(w, h);
            img.ProcessPixelRows(acc =>
            {
                var span = MemoryMarshal.Cast<ColorRgba32, Rgba32>(pixels.AsSpan());
                for (int y = 0; y < h; y++) span.Slice(y * w, w).CopyTo(acc.GetRowSpan(y));
            });
            if (tex.Format == Textures.TextureFormat.DXN)
            {
                // нормал-мапа: синий восстанавливаем, чтобы картинка была осмысленной
                img.ProcessPixelRows(acc =>
                {
                    for (int y = 0; y < h; y++)
                    {
                        var row = acc.GetRowSpan(y);
                        for (int x = 0; x < w; x++)
                        {
                            var px = row[x];
                            float nx = px.R / 127.5f - 1f, ny = px.G / 127.5f - 1f;
                            float nz = MathF.Sqrt(Math.Max(0f, 1f - nx * nx - ny * ny));
                            px.B = (byte)Math.Clamp((nz * 0.5f + 0.5f) * 255f, 0, 255); px.A = 255; row[x] = px;
                        }
                    }
                });
            }
            return img;
        }

        // несжатые
        switch (tex.Format)
        {
            case Textures.TextureFormat.A8R8G8B8:
            case Textures.TextureFormat.X8R8G8B8:
            {
                if (data.Length < w * h * 4) return null;
                var img = new Image<Rgba32>(w, h);
                bool hasA = tex.Format == Textures.TextureFormat.A8R8G8B8;
                img.ProcessPixelRows(acc =>
                {
                    for (int y = 0; y < h; y++)
                    {
                        var row = acc.GetRowSpan(y);
                        for (int x = 0; x < w; x++)
                        {
                            int o = (y * w + x) * 4; // BGRA в памяти
                            row[x] = new Rgba32(data[o + 2], data[o + 1], data[o], hasA ? data[o + 3] : (byte)255);
                        }
                    }
                });
                return img;
            }
            case Textures.TextureFormat.L8:
            case Textures.TextureFormat.A8:
            {
                if (data.Length < w * h) return null;
                var img = new Image<Rgba32>(w, h);
                bool isA = tex.Format == Textures.TextureFormat.A8;
                img.ProcessPixelRows(acc =>
                {
                    for (int y = 0; y < h; y++)
                    {
                        var row = acc.GetRowSpan(y);
                        for (int x = 0; x < w; x++)
                        {
                            byte v = data[y * w + x];
                            row[x] = isA ? new Rgba32(255, 255, 255, v) : new Rgba32(v, v, v, 255);
                        }
                    }
                });
                return img;
            }
            default:
                return null; // A16R16G16B16 / float / A4R4G4B4 — в уровнях для диффуза не встречаются
        }
    }

    // ------------------------------------------------------------------ утилиты

    private static string SafeName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "_";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsControl(c) ? '_' : c);
        return sb.ToString();
    }
}
