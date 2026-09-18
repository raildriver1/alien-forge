using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using AlienForge.Core;
using AlienForge.Core.Animation;
using AlienForge.Core.Export;
using AlienForge.Core.Imaging;
using CATHODE;
using Microsoft.Win32;

namespace AlienForge.App;

public partial class MainWindow : Window
{
    private GameInstall? _install;
    private AssetWorkspace? _workspace;
    private Textures? _globalTextures;

    private Models.CS2? _selectedModel;
    private Textures.TEX4? _selectedTexture;
    private AnimationCatalog? _catalog;
    private List<AnimationClipRef>? _clips;
    /// <summary>Клипы по одному, с настоящими именами (секции развёрнуты).</summary>
    private List<AnimationClipItem>? _items;
    private bool _busy;

    // animation playback
    private MeshPreview.Built? _built;
    private AnimationBundle? _bundle;
    private ClipData? _clip;
    private List<MeshSkinner>? _skinners;
    private DispatcherTimer? _timer;
    private int _frame;

    // Lighting the user can move. Kept as fields rather than rebuilt with the scene,
    // so dragging a slider changes a direction instead of re-decoding the mesh.
    private readonly AmbientLight _ambient = new(Color.FromRgb(0x55, 0x58, 0x5C));
    private readonly DirectionalLight _keyLight = new(Color.FromRgb(0xE8, 0xEC, 0xF0),
        new Vector3D(-0.5, -0.8, -0.6));
    private readonly DirectionalLight _fillLight = new(Color.FromRgb(0x30, 0x40, 0x50),
        new Vector3D(0.7, 0.3, 0.5));
    private double _lightAzimuth = 215, _lightElevation = 35;
    private double _lightIntensity = 1.0, _ambientLevel = 0.34;
    private bool _lightFollowsCamera;
    private bool _dragZoom;

    // orbit camera state
    private double _yaw = 0.6, _pitch = 0.35, _distance = 4;
    private Point3D _target = new(0, 0, 0);
    private System.Windows.Point _dragFrom;
    private bool _dragOrbit, _dragPan;

    public MainWindow()
    {
        InitializeComponent();
        PreviewTabs.SelectionChanged += OnTabChanged;
        MapView.NodeClicked += OnMapNodeClicked;
        Loaded += OnWindowLoaded;
    }

    // ------------------------------------------------------------------ setup
    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        DetectGame(quiet: true);

        // Автотест GUI-пути: AlienForge.exe --autotest <уровень> <фрагмент модели> <out.glb> [log]
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--autotest");
        if (at >= 0 && args.Length > at + 3)
            _ = RunAutoTest(args[at + 1], args[at + 2], args[at + 3],
                args.Length > at + 4 ? args[at + 4] : Path.Combine(AppContext.BaseDirectory, "autotest.log"));
    }

    private string? _autoLog;

    private void AutoLog(string line)
    {
        if (_autoLog is null) return;
        File.AppendAllText(_autoLog, $"[{DateTime.Now:HH:mm:ss}] {line}" + Environment.NewLine);
    }

    private async System.Threading.Tasks.Task WaitIdle()
    {
        while (_busy)
            await System.Threading.Tasks.Task.Delay(100);
    }

    /// <summary>
    /// Тот же путь, что и руками: уровень -> модель -> «Найти анимации» -> первый
    /// клип -> «Сохранить .glb». Каждый статус пишется в лог, окно закрывается.
    /// </summary>
    private async System.Threading.Tasks.Task RunAutoTest(string levelName, string modelFragment, string outPath, string log)
    {
        _autoLog = log;
        File.WriteAllText(log, "");
        try
        {
            AutoLog($"install={_install?.Root}");
            if (_install is null) { AutoLog("нет игры"); Close(); return; }
            var levels = (List<LevelRef>)LevelBox.ItemsSource;
            int idx = levels.FindIndex(l => l.Name.Contains(levelName, StringComparison.OrdinalIgnoreCase));
            AutoLog($"level index={idx}");
            if (idx < 0) { Close(); return; }
            await WaitIdle(); // уровень по умолчанию уже грузится после DetectGame
            LevelBox.SelectedIndex = idx; // смена выбора сама запускает загрузку
            await System.Threading.Tasks.Task.Delay(200);
            await WaitIdle();
            if (LevelBox.SelectedIndex == idx && _workspace is not null && !_workspace.Level.Name.Contains(levelName, StringComparison.OrdinalIgnoreCase))
            {
                OnLoadLevel(this, new RoutedEventArgs());
                await System.Threading.Tasks.Task.Delay(200);
                await WaitIdle();
            }
            AutoLog($"после загрузки: {StatusText.Text}");
            if (_workspace is null) { Close(); return; }

            var model = _workspace.FindModelByPath(modelFragment) ?? _workspace.FindModels(modelFragment).FirstOrDefault();
            AutoLog($"модель: {model?.Name ?? "не найдена"}");
            if (model is null) { Close(); return; }
            _selectedModel = model;
            ShowModel(model);
            AutoLog($"после ShowModel: {StatusText.Text}");

            OnLoadAnimations(this, new RoutedEventArgs());
            await System.Threading.Tasks.Task.Delay(200);
            await WaitIdle();
            AutoLog($"после поиска анимаций: {StatusText.Text} | клипов {_clips?.Count ?? -1} | summary: {AnimationSummary.Text}");
            if (_clips is null || _clips.Count == 0) { Close(); return; }
            foreach (var c in _clips.Take(5)) AutoLog($"   {c.DisplayName}  ({c.ShortName}, имён {c.Names.Count})");

            _items ??= AnimationClipItem.Expand(_clips);
            var first = _items.FirstOrDefault(i => i.ShortName.Contains("WALK_FORWARD", StringComparison.OrdinalIgnoreCase)) ?? _items[0];
            AutoLog($"клипов в списке: {_items.Count}; выбран {first.FullName}");
            AnimationList.SelectedItem = first;
            await ExportWithAnimations(new[] { first.Section }, outPath, first);
            AutoLog($"после экспорта одного: {StatusText.Text}");

            // и «Проиграть» — второй путь, где могла быть ошибка
            OnPlayAnimation(this, new RoutedEventArgs());
            await System.Threading.Tasks.Task.Delay(300);
            await WaitIdle();
            AutoLog($"после Play: {StatusText.Text}");
        }
        catch (Exception ex)
        {
            AutoLog($"ИСКЛЮЧЕНИЕ: {ex}");
        }
        finally
        {
            AutoLog("конец");
            Close();
        }
    }

    private void DetectGame(bool quiet)
    {
        var install = GameInstall.AutoDetect();
        if (install is null)
        {
            if (!quiet)
                MessageBox.Show(Loc.T("STATUS_NO_GAME"), Loc.T("APP_TITLE"));
            SetStatus(Loc.T("STATUS_NO_GAME"));
            return;
        }
        UseInstall(install);
    }

    private void UseInstall(GameInstall install)
    {
        _install = install;
        GamePathBox.Text = install.Root;

        var levels = install.EnumerateLevels();
        LevelBox.ItemsSource = levels;
        LevelBox.DisplayMemberPath = nameof(LevelRef.Name);
        if (levels.Count > 0)
        {
            // Nest is where the Alien lives, so it is the friendliest default.
            int index = levels.FindIndex(l =>
                l.Name.Contains("ALIEN_NEST", StringComparison.OrdinalIgnoreCase));
            LevelBox.SelectedIndex = index >= 0 ? index : 0;
        }
        SetStatus(Loc.T("STATUS_READY"));
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ----------------------------------------------------------- menu / file
    private void OnDetectGame(object sender, RoutedEventArgs e) => DetectGame(quiet: false);

    private void OnBrowseGame(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("DLG_PICK_GAME") };
        if (!string.IsNullOrWhiteSpace(GamePathBox.Text) && Directory.Exists(GamePathBox.Text))
            dialog.InitialDirectory = GamePathBox.Text;
        if (dialog.ShowDialog(this) != true)
            return;

        var install = GameInstall.Open(dialog.FolderName);
        string? problem = install.Validate();
        if (problem is not null)
        {
            MessageBox.Show(problem, Loc.T("APP_TITLE"), MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        UseInstall(install);
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnAbout(object sender, RoutedEventArgs e)
        => MessageBox.Show(Loc.T("ABOUT_TEXT"), Loc.T("MENU_ABOUT"));

    private void OnLanguageRussian(object sender, RoutedEventArgs e) => SetLanguage(Lang.Russian);

    private void OnLanguageEnglish(object sender, RoutedEventArgs e) => SetLanguage(Lang.English);

    /// <summary>
    /// Switches language and rewrites the headers that carry a model name. Those have
    /// their binding replaced by plain text, so they cannot follow the language on
    /// their own.
    /// </summary>
    private void SetLanguage(Lang language)
    {
        Loc.Current.Language = language;
        if (_selectedModel is not null)
            ShowSelectedName(_selectedModel.Name);
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnLoadLevel(sender, e);
    }

    // ------------------------------------------------------------ level load
    /// <summary>
    /// Picking a level loads it. Previously the choice only armed the Load button,
    /// which looked like nothing had happened at all.
    /// </summary>
    private void OnLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded || _install is not null)
            OnLoadLevel(sender, e);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LoadButton.IsEnabled = !busy;
        LevelBox.IsEnabled = !busy;
        Cursor = busy ? Cursors.AppStarting : null;
    }

    private async void OnLoadLevel(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            SetStatus(Loc.T("STATUS_BUSY"));
            return;
        }
        if (_install is null || LevelBox.SelectedItem is not LevelRef level)
            return;

        SetBusy(true);
        SetStatus(Loc.F("STATUS_LOADING", level.Name));
        string? filter = string.IsNullOrWhiteSpace(FilterBox.Text) ? null : FilterBox.Text.Trim();

        try
        {
            var install = _install;
            // The 88 MB shared texture pack is loaded once and reused across levels.
            var global = _globalTextures;

            // Stages arrive from the loader thread, so they are marshalled back before
            // touching the status line.
            void Report(LoadStage stage) => Dispatcher.BeginInvoke(new Action(() =>
                SetStatus(Loc.F("STATUS_STAGE", level.Name,
                    Loc.T("STAGE_" + stage.ToString().ToUpperInvariant())))));

            var workspace = await System.Threading.Tasks.Task.Run(() =>
            {
                global ??= AssetWorkspace.LoadGlobalTextures(install);
                return AssetWorkspace.OpenLevel(install, level, global, Report);
            });
            _globalTextures = global;
            _workspace = workspace;
            _catalog = null;

            var root = AssetTree.BuildArchiveTree(workspace, filter);
            var rootView = new AssetNodeView(root);
            rootView.EnsureLoaded();
            ArchiveTree.ItemsSource = new[] { rootView };
            DependencyTree.ItemsSource = null;
            ClearModelPreview();
            MapView.Clear();
            MapEmpty.Visibility = Visibility.Visible;
            AnimationList.ItemsSource = null;
            _clips = null;
            _items = null;
            ExportClipButton.IsEnabled = false;
            ExportRawClipButton.IsEnabled = false;
            ExportAllClipsButton.IsEnabled = false;

            SetStatus(Loc.F("STATUS_LOADED", workspace.Name, workspace.ModelCount,
                workspace.MaterialCount,
                workspace.LevelTextureCount + workspace.GlobalTextureCount,
                workspace.LoadTime.TotalSeconds));
        }
        catch (Exception ex)
        {
            SetStatus($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // -------------------------------------------------------------- tree glue
    private void OnTreeExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: AssetNodeView view })
            view.EnsureLoaded();
    }

    private void OnArchiveSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
        => HandleSelection(e.NewValue as AssetNodeView, updateDependencies: true);

    private void OnDependencySelected(object sender, RoutedPropertyChangedEventArgs<object> e)
        => HandleSelection(e.NewValue as AssetNodeView, updateDependencies: false);

    private void HandleSelection(AssetNodeView? view, bool updateDependencies)
    {
        if (view is null || _workspace is null)
            return;

        switch (view.Node.Payload)
        {
            case Models.CS2 model:
                _selectedModel = model;
                _clips = null;
                if (updateDependencies)
                {
                    var deps = AssetTree.BuildDependencyTree(_workspace, model);
                    DependencyTree.ItemsSource = new[] { new AssetNodeView(deps) };
                }
                ShowModel(model);
                BuildMap(model);
                ShowSelectedName(model.Name);

                // Picking another model while looking at one should swap the model, not
                // throw the view somewhere else. Only a tab that cannot show a model at
                // all gives way, and then the model view is the useful place to land.
                if (PreviewTabs.SelectedIndex is not (TabMap or TabModel or TabInfo))
                    SelectTab(TabModel);
                else
                    UpdatePaneVisibility();
                break;

            case Textures.TEX4 texture:
                _selectedTexture = texture;
                ShowTexture(texture);
                SelectTab(TabTexture);
                break;

            case Materials.Material material:
                ShowInfo(DescribeMaterial(material));
                SelectTab(TabInfo);
                break;

            default:
                ShowInfo(view.Node.Detail ?? view.Node.Name);
                break;
        }
    }

    private void SelectTab(int index)
    {
        if (PreviewTabs.SelectedIndex != index)
            PreviewTabs.SelectedIndex = index;
        else
            UpdatePaneVisibility();
    }

    /// <summary>
    /// Puts the current model's name on the tabs that show it, so the header reads
    /// "Модель — ALIEN" and it is obvious which model is on screen.
    /// </summary>
    private void ShowSelectedName(string? modelName)
    {
        string leaf = LeafOf(modelName);
        TabMapItem.Header = string.IsNullOrEmpty(leaf)
            ? Loc.T("TAB_MAP")
            : $"{Loc.T("TAB_MAP")} — {leaf}";
        TabModelItem.Header = string.IsNullOrEmpty(leaf)
            ? Loc.T("TAB_MODEL")
            : $"{Loc.T("TAB_MODEL")} — {leaf}";
        TabAnimationItem.Header = string.IsNullOrEmpty(leaf)
            ? Loc.T("TAB_ANIMATION")
            : $"{Loc.T("TAB_ANIMATION")} — {leaf}";
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
        => UpdatePaneVisibility();

    private void UpdatePaneVisibility()
    {
        int index = PreviewTabs.SelectedIndex;
        MapPaneHost.Visibility = index == TabMap ? Visibility.Visible : Visibility.Collapsed;
        ModelPane.Visibility = index == TabModel ? Visibility.Visible : Visibility.Collapsed;
        TexturePane.Visibility = index == TabTexture ? Visibility.Visible : Visibility.Collapsed;
        AnimationPane.Visibility = index == TabAnimation ? Visibility.Visible : Visibility.Collapsed;
        InfoPane.Visibility = index == TabInfo ? Visibility.Visible : Visibility.Collapsed;
    }

    private const int TabMap = 0;
    private const int TabModel = 1;
    private const int TabTexture = 2;
    private const int TabAnimation = 3;
    private const int TabInfo = 4;

    // ------------------------------------------------------------------- map
    /// <summary>
    /// Rebuilds the dependency map for a model. Animation clips are only included
    /// once they have been looked up, because scanning the archive is an explicit
    /// action rather than something to do on every selection.
    /// </summary>
    private void BuildMap(Models.CS2 model)
    {
        if (_workspace is null)
            return;

        var materials = _workspace.MaterialsOf(model);
        var textures = _workspace.TexturesOf(model);

        var centre = new MapNode
        {
            Kind = MapNodeKind.Model,
            Title = SafeLeaf(model.Name),
            Subtitle = $"{_workspace.SubmeshCount(model)} × {Loc.T("TAB_MODEL").ToLowerInvariant()}",
            Payload = model,
        };

        var groups = new List<(MapNode, IReadOnlyList<MapNode>)>
        {
            (new MapNode
                {
                    Kind = MapNodeKind.Hub,
                    Title = Loc.T("HUB_TEXTURES"),
                    Subtitle = textures.Count.ToString(),
                },
                textures.Select(use => new MapNode
                {
                    Kind = MapNodeKind.Texture,
                    Title = LeafOf(use.Name),
                    Subtitle = $"{use.Texture.Format}",
                    Payload = use.Texture,
                }).ToList()),

            (new MapNode
                {
                    Kind = MapNodeKind.Hub,
                    Title = Loc.T("HUB_MATERIALS"),
                    Subtitle = materials.Count.ToString(),
                },
                materials.Select(material => new MapNode
                {
                    Kind = MapNodeKind.Material,
                    Title = material.Name ?? "material",
                    Subtitle = $"{material.TextureReferences.Count} × tex",
                    Payload = material,
                }).ToList()),
        };

        var clips = _clips ?? new List<AnimationClipRef>();
        groups.Add((new MapNode
            {
                Kind = MapNodeKind.Hub,
                Title = Loc.T("HUB_ANIMATIONS"),
                Subtitle = clips.Count > 0 ? clips.Count.ToString() : Loc.T("HUB_ANIM_HINT"),
            },
            (_items ??= AnimationClipItem.Expand(clips)).Select(item => new MapNode
            {
                Kind = MapNodeKind.Animation,
                Title = item.ShortName,
                Subtitle = item.Folder,
                Payload = item,
            }).ToList()));

        MapView.Build(centre, groups);
        MapEmpty.Visibility = Visibility.Collapsed;
    }

    private void OnMapNodeClicked(MapNode node)
    {
        switch (node.Payload)
        {
            case Textures.TEX4 texture:
                _selectedTexture = texture;
                ShowTexture(texture);
                SelectTab(TabTexture);
                break;
            case Materials.Material material:
                ShowInfo(DescribeMaterial(material));
                SelectTab(TabInfo);
                break;
            case AnimationClipItem clip:
                AnimationList.SelectedItem = clip;
                AnimationList.ScrollIntoView(clip);
                ShowInfo(clip.Describe());
                SelectTab(TabAnimation);
                break;
            case Models.CS2 model:
                ShowModel(model);
                SelectTab(TabModel);
                break;
        }
    }

    private static string LeafOf(string? name)
    {
        string normalised = (name ?? string.Empty).Replace('/', '\\');
        int slash = normalised.LastIndexOf('\\');
        return slash >= 0 ? normalised[(slash + 1)..] : normalised;
    }

    // ------------------------------------------------------------ model view
    private void ClearModelPreview()
    {
        SceneRoot.Content = null;
        ModelStats.Text = string.Empty;
        ModelEmpty.Visibility = Visibility.Visible;
        _selectedModel = null;
        _built = null;
        _suppressPartEvents = true;
        PartList.Items.Clear();
        PartCount.Text = "0";
        _suppressPartEvents = false;
    }

    private void ShowModel(Models.CS2 model)
    {
        if (_workspace is null)
            return;
        SetStatus(Loc.F("STATUS_DECODING", model.Name ?? "model"));

        try
        {
            _timer?.Stop();
            _bundle = null;
            _clip = null;
            _skinners = null;
            FrameSlider.IsEnabled = false;
            StopButton.IsEnabled = false;

            var built = MeshPreview.Build(_workspace, model, lodIndex: 0,
                includeCollision: MenuShowCollision.IsChecked);
            _built = built;

            var scene = new Model3DGroup();
            scene.Children.Add(built.Model);
            scene.Children.Add(_ambient);
            scene.Children.Add(_keyLight);
            scene.Children.Add(_fillLight);
            SceneRoot.Content = scene;
            UpdateLighting();

            FillPartList(built);
            FrameCamera(built.Bounds);

            ModelStats.Text = string.Join("\n", new[]
            {
                $"{Loc.T("MODEL_PARTS")}: {built.Parts}",
                $"{Loc.T("MODEL_VERTICES")}: {built.Vertices}",
                $"{Loc.T("MODEL_TRIANGLES")}: {built.Triangles}",
                $"{Loc.T("MODEL_TEXTURED_PARTS")}: {built.Textured}",
            });
            ModelEmpty.Visibility = built.Parts > 0 ? Visibility.Collapsed : Visibility.Visible;

            ShowInfo(DescribeModel(model, built));
            SetStatus(Loc.T("STATUS_READY"));
        }
        catch (Exception ex)
        {
            SetStatus($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void FrameCamera(Rect3D bounds)
    {
        if (bounds.IsEmpty)
            bounds = new Rect3D(0, 0, 0, 1, 1, 1);
        _target = new Point3D(bounds.X + bounds.SizeX / 2,
            bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        double radius = Math.Max(0.25, new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ).Length / 2);
        _distance = radius * 2.6;
        _yaw = 0.6;
        _pitch = 0.28;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        // Турнтейбл как в Blender: тангаж не ограничен, за полюсом камера
        // переворачивается (up меняет знак), так что модель можно крутить на 360° по
        // обеим осям и смотреть снизу.
        double cp = Math.Cos(_pitch), sp = Math.Sin(_pitch);
        var offset = new Vector3D(Math.Sin(_yaw) * cp, sp, Math.Cos(_yaw) * cp) * _distance;
        Camera.Position = _target + offset;
        Camera.LookDirection = -offset;
        Camera.UpDirection = new Vector3D(0, cp >= 0 ? 1 : -1, 0);
        Camera.NearPlaneDistance = Math.Max(0.001, _distance * 0.005);
        Camera.FarPlaneDistance = _distance * 40;

        // A headlamp has to be re-aimed whenever the camera moves.
        if (_lightFollowsCamera)
            UpdateLighting();
    }

    private void OnResetCamera(object sender, RoutedEventArgs e)
    {
        if (SceneRoot.Content is Model3DGroup group)
            FrameCamera(group.Bounds);
    }

    // --------------------------------------------------------------- lighting
    /// <summary>
    /// Points the lights where the sliders say and sets how hard they burn.
    /// </summary>
    /// <remarks>
    /// Azimuth turns the light around the model, elevation lifts it overhead. The fill
    /// light stays opposite the key light at a fraction of its strength, which keeps
    /// the shadowed side readable instead of solid black. Colours are scaled rather
    /// than replaced, so the cool fill and warm key keep their character at any
    /// brightness.
    /// </remarks>
    private void UpdateLighting()
    {
        double azimuth = _lightAzimuth * Math.PI / 180.0;
        double elevation = _lightElevation * Math.PI / 180.0;

        Vector3D toLight;
        if (_lightFollowsCamera)
        {
            // A headlamp: the key light sits at the camera, so nothing is ever unlit.
            toLight = -Camera.LookDirection;
            if (toLight.Length > 1e-6)
                toLight.Normalize();
        }
        else
        {
            double ce = Math.Cos(elevation);
            toLight = new Vector3D(Math.Sin(azimuth) * ce, Math.Sin(elevation),
                Math.Cos(azimuth) * ce);
        }

        // A DirectionalLight travels along its direction, so it points from the light
        // towards the model: the negation of the vector aimed at the light.
        _keyLight.Direction = -toLight;
        _fillLight.Direction = toLight;

        _keyLight.Color = Scale(Color.FromRgb(0xE8, 0xEC, 0xF0), _lightIntensity);
        _fillLight.Color = Scale(Color.FromRgb(0x6E, 0x86, 0xA4), _lightIntensity * 0.28);
        _ambient.Color = Scale(Color.FromRgb(0xFF, 0xFF, 0xFF), _ambientLevel);

        if (LightReadout is not null)
            LightReadout.Text = _lightFollowsCamera
                ? Loc.T("LIGHT_FROM_CAMERA")
                : $"{_lightAzimuth:F0}° / {_lightElevation:F0}°";
    }

    private static Color Scale(Color color, double factor)
    {
        factor = Math.Clamp(factor, 0, 2);
        return Color.FromRgb(
            (byte)Math.Clamp(color.R * factor, 0, 255),
            (byte)Math.Clamp(color.G * factor, 0, 255),
            (byte)Math.Clamp(color.B * factor, 0, 255));
    }

    private void OnLightAzimuthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _lightAzimuth = e.NewValue;
        UpdateLighting();
    }

    private void OnLightElevationChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _lightElevation = e.NewValue;
        UpdateLighting();
    }

    private void OnLightIntensityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _lightIntensity = e.NewValue;
        UpdateLighting();
    }

    private void OnAmbientChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _ambientLevel = e.NewValue;
        UpdateLighting();
    }

    private void OnLightFollowCamera(object sender, RoutedEventArgs e)
    {
        _lightFollowsCamera = FollowCameraCheck.IsChecked == true;
        LightAzimuth.IsEnabled = !_lightFollowsCamera;
        LightElevation.IsEnabled = !_lightFollowsCamera;
        UpdateLighting();
    }

    private void OnResetLight(object sender, RoutedEventArgs e)
    {
        FollowCameraCheck.IsChecked = false;
        _lightFollowsCamera = false;
        LightAzimuth.IsEnabled = true;
        LightElevation.IsEnabled = true;
        LightAzimuth.Value = 215;
        LightElevation.Value = 35;
        LightIntensity.Value = 1.0;
        AmbientLevel.Value = 0.34;
        UpdateLighting();
    }

    // ------------------------------------------------------------- submeshes
    /// <summary>
    /// Lists the model's parts so they can be picked apart one at a time. A character
    /// arrives as a dozen submeshes with separate materials, and telling them apart on
    /// a solid silhouette is impossible.
    /// </summary>
    private void FillPartList(MeshPreview.Built built)
    {
        _suppressPartEvents = true;
        PartList.Items.Clear();
        foreach (var part in built.Parts3D)
            PartList.Items.Add(part);
        PartCount.Text = built.Parts3D.Count.ToString();
        _suppressPartEvents = false;
    }

    private bool _suppressPartEvents;

    /// <summary>Draws only the ticked parts, keeping their order in the group.</summary>
    private void ApplyPartVisibility()
    {
        if (_built is null)
            return;

        _built.Geometry.Children.Clear();
        foreach (var part in _built.Parts3D)
            if (part.Visible)
                _built.Geometry.Children.Add(part.Drawing);
    }

    private void OnPartVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPartEvents || sender is not CheckBox { DataContext: MeshPreview.PreviewPart part })
            return;
        part.Visible = ((CheckBox)sender).IsChecked == true;
        ApplyPartVisibility();
    }

    private void OnPartSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPartEvents || PartList.SelectedItem is not MeshPreview.PreviewPart part)
            return;
        ShowInfo(DescribePart(part));
    }

    /// <summary>Shows one part alone, which is the quickest way to find what it is.</summary>
    private void OnIsolatePart(object sender, RoutedEventArgs e)
    {
        if (_built is null || PartList.SelectedItem is not MeshPreview.PreviewPart chosen)
            return;

        _suppressPartEvents = true;
        foreach (var part in _built.Parts3D)
            part.Visible = ReferenceEquals(part, chosen);
        RefreshPartChecks();
        _suppressPartEvents = false;
        ApplyPartVisibility();
        FrameCamera(chosen.Drawing.Bounds);
    }

    private void OnShowAllParts(object sender, RoutedEventArgs e)
    {
        if (_built is null)
            return;

        _suppressPartEvents = true;
        foreach (var part in _built.Parts3D)
            part.Visible = true;
        RefreshPartChecks();
        _suppressPartEvents = false;
        ApplyPartVisibility();
        FrameCamera(_built.Geometry.Bounds);
    }

    /// <summary>
    /// Rebuilds the list so the tick boxes match the parts. The items are the same
    /// objects, so this only refreshes what is drawn in the list.
    /// </summary>
    private void RefreshPartChecks()
    {
        var selected = PartList.SelectedItem;
        var items = PartList.Items.Cast<object>().ToList();
        PartList.Items.Clear();
        foreach (var item in items)
            PartList.Items.Add(item);
        PartList.SelectedItem = selected;
    }

    private void OnToggleCollision(object sender, RoutedEventArgs e)
    {
        if (_selectedModel is not null)
            ShowModel(_selectedModel);
    }

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(ViewportHost);

        // Раскладка Blender: СКМ — вращение, Shift+СКМ — сдвиг, Ctrl+СКМ — зум
        // перетаскиванием. Плюс ЛКМ — вращение, ПКМ и Alt+ЛКМ — сдвиг для мыши без
        // удобного колеса.
        bool altHeld = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        bool shiftHeld = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        _dragZoom = e.ChangedButton == MouseButton.Middle && ctrlHeld;
        _dragPan = !_dragZoom && (e.ChangedButton == MouseButton.Right
                   || (e.ChangedButton == MouseButton.Middle && shiftHeld)
                   || (e.ChangedButton == MouseButton.Left && altHeld));
        _dragOrbit = !_dragZoom && !_dragPan
                     && (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Middle);
        // Focus so the keyboard shortcuts below reach the viewport.
        ViewportHost.Focus();
        ViewportHost.CaptureMouse();
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragOrbit = _dragPan = _dragZoom = false;
        ViewportHost.ReleaseMouseCapture();
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragOrbit && !_dragPan && !_dragZoom)
            return;
        var now = e.GetPosition(ViewportHost);
        double dx = now.X - _dragFrom.X, dy = now.Y - _dragFrom.Y;
        _dragFrom = now;

        if (_dragOrbit)
        {
            // Turn by how far the pointer crossed the window, not by a fixed amount per
            // pixel. A constant rate means a small window spins wildly while a large one
            // barely moves; tied to height, dragging the full height is half a turn
            // whatever the window size.
            double perPixel = Math.PI / Math.Max(200.0, ViewportHost.ActualHeight);
            // Когда камера перевёрнута (за полюсом), горизонтальное вращение
            // инвертируется, иначе движение мыши идёт «против» картинки.
            bool flipped = Math.Cos(_pitch) < 0;
            _yaw -= dx * perPixel * (flipped ? -1 : 1);
            _pitch = WrapAngle(_pitch + dy * perPixel);
        }
        else if (_dragZoom)
        {
            SetDistance(_distance * Math.Exp(dy * 0.01));
        }
        else
        {
            // Exact panning: one pixel of drag moves the world by exactly one pixel's
            // worth at the pivot's depth, so whatever is under the pointer stays under
            // it. The old constant was a guess and drifted at every zoom level.
            var (_, screenRight, screenUp) = CameraAxes();
            double perPixel = WorldUnitsPerPixel();
            _target -= screenRight * (dx * perPixel);
            _target += screenUp * (dy * perPixel);
        }
        UpdateCamera();
    }

    private static double WrapAngle(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }

    private void SetDistance(double next)
    {
        _distance = Math.Clamp(next, 0.02, 20000);
        UpdateCamera();
    }

    /// <summary>Поворот камеры к стандартному виду (numpad как в Blender).</summary>
    private void SnapView(double yaw, double pitch)
    {
        _yaw = yaw;
        _pitch = pitch;
        UpdateCamera();
    }

    /// <summary>Forward, screen-right and screen-up of the camera, all unit length.</summary>
    private (Vector3D forward, Vector3D right, Vector3D up) CameraAxes()
    {
        var forward = Camera.LookDirection;
        if (forward.Length < 1e-9)
            forward = new Vector3D(0, 0, -1);
        forward.Normalize();

        var right = Vector3D.CrossProduct(forward, Camera.UpDirection);
        if (right.Length < 1e-6)
            right = new Vector3D(1, 0, 0);   // looking straight up or down
        right.Normalize();

        var up = Vector3D.CrossProduct(right, forward);
        up.Normalize();
        return (forward, right, up);
    }

    /// <summary>
    /// How much world space one screen pixel covers at the pivot's distance.
    /// </summary>
    /// <remarks>
    /// WPF states <see cref="PerspectiveCamera.FieldOfView"/> horizontally, so the
    /// vertical half-angle comes from it through the aspect ratio. Getting this right is
    /// what makes a drag track the pointer instead of merely moving in the right
    /// direction.
    /// </remarks>
    private double WorldUnitsPerPixel()
    {
        double height = Math.Max(1, ViewportHost.ActualHeight);
        double width = Math.Max(1, ViewportHost.ActualWidth);
        double tanHalfX = Math.Tan(Camera.FieldOfView * Math.PI / 360.0);
        double tanHalfY = tanHalfX * height / width;
        return 2 * _distance * tanHalfY / height;
    }

    /// <summary>
    /// The world point the pointer is aimed at, taken on the plane through the pivot.
    /// </summary>
    private Point3D AimPoint(System.Windows.Point cursor)
    {
        double height = Math.Max(1, ViewportHost.ActualHeight);
        double width = Math.Max(1, ViewportHost.ActualWidth);
        double tanHalfX = Math.Tan(Camera.FieldOfView * Math.PI / 360.0);
        double tanHalfY = tanHalfX * height / width;

        // Normalised device coordinates: -1..1 across the window, y up.
        double ndcX = 2 * cursor.X / width - 1;
        double ndcY = 1 - 2 * cursor.Y / height;

        var (forward, right, up) = CameraAxes();
        var direction = forward + right * (ndcX * tanHalfX) + up * (ndcY * tanHalfY);
        if (direction.Length < 1e-9)
            return _target;
        direction.Normalize();

        // Walk out to the pivot's plane so the result sits at the depth being examined.
        double along = Vector3D.DotProduct(_target - Camera.Position, forward);
        double reach = Vector3D.DotProduct(direction, forward);
        if (Math.Abs(reach) < 1e-6)
            return _target;
        return Camera.Position + direction * (along / reach);
    }

    /// <summary>
    /// Zooms toward whatever the pointer is over, not toward the pivot.
    /// </summary>
    /// <remarks>
    /// A plain dolly moves along the line to the pivot, so anything off-centre slides
    /// out of frame as you close in and you end up alternating zoom and pan to look at
    /// one detail. Dragging the pivot toward the aimed point by the same fraction the
    /// distance shrinks makes the view converge on that point instead. Zooming out
    /// leaves the pivot alone, so backing off does not wander.
    /// </remarks>
    private void OnViewportWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 0.86 : 1 / 0.86;
        double next = Math.Clamp(_distance * factor, 0.02, 20000);
        double applied = _distance <= 0 ? 1 : next / _distance;

        if (applied < 1)
        {
            var aim = AimPoint(e.GetPosition(ViewportHost));
            double pull = 1 - applied;
            _target = new Point3D(
                _target.X + (aim.X - _target.X) * pull,
                _target.Y + (aim.Y - _target.Y) * pull,
                _target.Z + (aim.Z - _target.Z) * pull);
        }

        _distance = next;
        UpdateCamera();
    }

    /// <summary>
    /// Keyboard navigation, so the view can be driven without a spare hand on the mouse.
    /// </summary>
    /// <remarks>
    /// W and S move along the view direction, A and D sideways, Q and E straight up and
    /// down. Steps scale with how far the camera is from what it is looking at, which is
    /// what keeps the same key useful both when inspecting a hand and when crossing a
    /// room. Shift multiplies the step for covering distance.
    /// </remarks>
    private void OnViewportKeyDown(object sender, KeyEventArgs e)
    {
        var forward = Camera.LookDirection;
        if (forward.Length < 1e-6)
            return;
        forward.Normalize();

        var right = Vector3D.CrossProduct(Camera.UpDirection, forward);
        if (right.Length < 1e-6)
            right = new Vector3D(1, 0, 0);
        right.Normalize();
        var up = Camera.UpDirection;

        double step = Math.Max(0.02, _distance * 0.08);
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            step *= 4;

        // Numpad как в Blender: 1/3/7 — спереди/справа/сверху, Ctrl — с обратной
        // стороны, 4/6 и 8/2 — шаг 15°, 9 — развернуть на 180°, Home/F — вписать.
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        const double step15 = Math.PI / 12;
        switch (e.Key)
        {
            case Key.NumPad1: SnapView(ctrl ? Math.PI : 0, 0); e.Handled = true; return;
            case Key.NumPad3: SnapView(ctrl ? -Math.PI / 2 : Math.PI / 2, 0); e.Handled = true; return;
            case Key.NumPad7: SnapView(_yaw, ctrl ? -Math.PI / 2 + 1e-4 : Math.PI / 2 - 1e-4); e.Handled = true; return;
            case Key.NumPad4: _yaw += step15; UpdateCamera(); e.Handled = true; return;
            case Key.NumPad6: _yaw -= step15; UpdateCamera(); e.Handled = true; return;
            case Key.NumPad8: _pitch = WrapAngle(_pitch + step15); UpdateCamera(); e.Handled = true; return;
            case Key.NumPad2: _pitch = WrapAngle(_pitch - step15); UpdateCamera(); e.Handled = true; return;
            case Key.NumPad9: _yaw += Math.PI; UpdateCamera(); e.Handled = true; return;
            case Key.Add or Key.OemPlus: SetDistance(_distance / 1.2); e.Handled = true; return;
            case Key.Subtract or Key.OemMinus: SetDistance(_distance * 1.2); e.Handled = true; return;
        }

        Vector3D move = default;
        switch (e.Key)
        {
            case Key.W or Key.Up: move = forward * step; break;
            case Key.S or Key.Down: move = -forward * step; break;
            case Key.A or Key.Left: move = -right * step; break;
            case Key.D or Key.Right: move = right * step; break;
            case Key.E or Key.PageUp: move = up * step; break;
            case Key.Q or Key.PageDown: move = -up * step; break;

            case Key.F or Key.Home or Key.Decimal:
                // Frame whatever is on screen, the usual shortcut for "show me it all".
                if (SceneRoot.Content is Model3DGroup group)
                    FrameCamera(group.Bounds);
                e.Handled = true;
                return;

            default:
                return;
        }

        _target += move;
        UpdateCamera();
        e.Handled = true;
    }

    // ---------------------------------------------------------- texture view
    private void ShowTexture(Textures.TEX4 texture)
    {
        var result = TextureDecoder.Decode(texture);
        if (!result.Ok)
        {
            TextureImage.Source = null;
            TextureInfo.Text = result.Error ?? "?";
            TextureEmpty.Visibility = Visibility.Visible;
            return;
        }

        var image = result.Image!;
        TextureImage.Source = MeshPreview.ToBitmap(image);
        TextureEmpty.Visibility = Visibility.Collapsed;
        TextureInfo.Text =
            $"{texture.Name}   {Loc.T("TEX_FORMAT")}: {result.Format}   " +
            $"{Loc.T("TEX_SIZE")}: {image.Width}x{image.Height}   " +
            $"{Loc.T("TEX_SOURCE")}: {result.Source}   " +
            $"{Loc.T("TEX_ALPHA")}: " +
            (image.HasMeaningfulAlpha() ? Loc.T("TEX_ALPHA_YES") : Loc.T("TEX_ALPHA_NO"));
    }

    private void ShowInfo(string text)
    {
        InfoText.Text = text;
    }

    private string DescribeModel(Models.CS2 model, MeshPreview.Built built)
    {
        if (_workspace is null)
            return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine(model.Name ?? "model");
        sb.AppendLine();
        sb.AppendLine($"{Loc.T("MODEL_PARTS")}: {built.Parts}");
        sb.AppendLine($"{Loc.T("MODEL_VERTICES")}: {built.Vertices}");
        sb.AppendLine($"{Loc.T("MODEL_TRIANGLES")}: {built.Triangles}");

        var materials = _workspace.MaterialsOf(model);
        var textures = _workspace.TexturesOf(model);
        sb.AppendLine($"{Loc.T("MODEL_MATERIALS")}: {materials.Count}");
        sb.AppendLine($"{Loc.T("MODEL_TEXTURES")}: {textures.Count}");
        sb.AppendLine();

        foreach (var use in textures)
            sb.AppendLine($"  [{use.Source}] {use.Texture.Format,-10} {use.Texture.Name}");

        foreach (string note in built.Notes)
            sb.AppendLine($"  ! {note}");
        return sb.ToString();
    }

    /// <summary>
    /// One part on its own: its geometry, and the material read as a shader so it is
    /// clear which texture feeds colour, which feeds the normals and which the blick.
    /// </summary>
    private string DescribePart(MeshPreview.PreviewPart part)
    {
        var sb = new StringBuilder();
        sb.AppendLine(part.Mesh.Name);
        sb.AppendLine();
        sb.AppendLine($"вершин: {part.Mesh.VertexCount}");
        sb.AppendLine($"треугольников: {part.Mesh.TriangleCount}");
        sb.AppendLine($"LOD: {part.Mesh.LodName} (компонент {part.Mesh.ComponentIndex}, " +
                      $"сабмеш {part.Mesh.SubmeshIndex})");
        sb.AppendLine($"скин: {(part.Mesh.IsSkinned ? "есть, кости и веса прочитаны" : "нет")}");
        sb.AppendLine($"нормали: {(part.Mesh.Normals is { Length: > 0 } ? "есть" : "нет")}, " +
                      $"UV: {(part.Mesh.Uv0 is { Length: > 0 } ? "есть" : "нет")}");

        var (min, max) = part.Mesh.Bounds();
        sb.AppendLine($"габариты: {max.X - min.X:F2} x {max.Y - min.Y:F2} x {max.Z - min.Z:F2} м");
        sb.AppendLine();

        if (part.Material is not null)
            sb.Append(ShaderLayout.Read(part.Material).Describe());
        else
            sb.AppendLine("материала нет");

        foreach (string warning in part.Mesh.Warnings)
            sb.AppendLine($"  ! {warning}");
        return sb.ToString();
    }

    private static string DescribeMaterial(Materials.Material material)
    {
        var sb = new StringBuilder();
        sb.AppendLine(material.Name ?? "material");
        sb.AppendLine();
        sb.AppendLine($"environmentMapIndex: {material.EnvironmentMapIndex}");
        sb.AppendLine($"physicalMaterialIndex: {material.PhysicalMaterialIndex}");
        sb.AppendLine($"priority: {material.Priority}");
        sb.AppendLine();
        for (int i = 0; i < material.TextureReferences.Count; i++)
        {
            var ptr = material.TextureReferences[i];
            sb.AppendLine($"  {i,2}  [{ptr.Location}]  {ptr.Texture?.Name ?? "-"}" +
                          (ptr.Texture is null ? "" : $"  {ptr.Texture.Format}"));
        }
        return sb.ToString();
    }

    // --------------------------------------------------------------- exports
    private void OnExportModel(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _selectedModel is null)
            return;
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("DLG_PICK_OUT"),
            Filter = Loc.T("DLG_GLB_FILTER"),
            FileName = SafeLeaf(_selectedModel.Name) + ".glb",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var result = ModelExporter.ExportGlb(_workspace, _selectedModel, dialog.FileName);
            var report = GlbValidator.Validate(result.Path);
            SetStatus(Loc.F("STATUS_EXPORTED", result.Path, result.Bytes / 1048576.0)
                      + "   " + Loc.F("STATUS_VALID", report.ToString()));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("STATUS_EXPORT_FAILED", ex.Message));
        }
    }

    private void OnExportModelTextures(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _selectedModel is null)
            return;
        var dialog = new OpenFolderDialog { Title = Loc.T("DLG_PICK_OUT") };
        if (dialog.ShowDialog(this) != true)
            return;

        int ok = 0, failed = 0;
        foreach (var use in _workspace.TexturesOf(_selectedModel))
        {
            var result = TextureDecoder.Decode(use.Texture);
            if (!result.Ok)
            {
                failed++;
                continue;
            }
            string file = AssetNaming.TextureFileName(use.Name,
                result.Image!.Width, result.Image.Height);
            PngEncoder.Save(result.Image, Path.Combine(dialog.FolderName, file));
            ok++;
        }
        SetStatus($"{ok} PNG -> {dialog.FolderName}" + (failed > 0 ? $", {failed} ?" : ""));
    }

    private void OnExportCurrentTexture(object sender, RoutedEventArgs e)
    {
        if (_selectedTexture is null)
            return;
        var result = TextureDecoder.Decode(_selectedTexture);
        if (!result.Ok)
        {
            SetStatus(result.Error ?? "?");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("DLG_PICK_OUT"),
            Filter = Loc.T("DLG_PNG_FILTER"),
            FileName = AssetNaming.TextureFileName(_selectedTexture.Name ?? "texture",
                result.Image!.Width, result.Image.Height),
        };
        if (dialog.ShowDialog(this) != true)
            return;
        PngEncoder.Save(result.Image, dialog.FileName);
        SetStatus(Loc.F("STATUS_EXPORTED", dialog.FileName,
            new FileInfo(dialog.FileName).Length / 1048576.0));
    }

    /// <summary>
    /// Уровень целиком в glTF ровно как в игре: иерархия композитов из COMMANDS,
    /// позиции алиасов/прокси, MaterialMappings, без окклюдеров, LOD и служебных
    /// мешей (см. Core/Export/LevelExporter). Загрузка через CathodeLib.Level идёт
    /// заново — экспортёру нужны COMMANDS и REDS, которых в AssetWorkspace нет.
    /// </summary>
    private void OnBrowseUi(object sender, RoutedEventArgs e)
    {
        if (_install is null)
            return;
        try
        {
            var window = new UiBrowserWindow(_install) { Owner = this };
            window.Show();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async void OnExportScene(object sender, RoutedEventArgs e)
    {
        if (_install is null || _busy || LevelBox.SelectedItem is not LevelRef level)
            return;

        var options = LevelExportDialog.Ask(this);
        if (options is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = Loc.T("DLG_PICK_OUT"),
            Filter = options.Gltf ? "glTF (*.gltf)|*.gltf" : Loc.T("DLG_GLB_FILTER"),
            FileName = level.Name + (options.Gltf ? ".gltf" : ".glb"),
        };
        if (dialog.ShowDialog(this) != true)
            return;

        SetBusy(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string root = _install.Root;
        string outPath = dialog.FileName;
        try
        {
            AlienForge.Core.Export.LevelExporter.Log = line =>
                Dispatcher.BeginInvoke(new Action(() => SetStatus(Loc.F("SCENE_STATUS", level.Name, line))));
            int code = await System.Threading.Tasks.Task.Run(() =>
            {
                var lvl = AlienForge.Core.Export.LevelExporter.OpenLevel(root, level.Directory);
                return AlienForge.Core.Export.LevelExporter.Run(lvl, level.Name, outPath, options);
            });
            if (code == 0 && File.Exists(outPath))
                SetStatus(Loc.F("SCENE_DONE", outPath, new FileInfo(outPath).Length / 1048576.0, sw.Elapsed.TotalSeconds));
            else
                SetStatus(Loc.F("SCENE_FAIL", $"код {code}"));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("SCENE_FAIL", ex.Message));
        }
        finally
        {
            AlienForge.Core.Export.LevelExporter.Log = Console.WriteLine;
            SetBusy(false);
        }
    }

    private async void OnExportLevel(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _busy)
            return;
        var dialog = new OpenFolderDialog { Title = Loc.T("DLG_PICK_OUT") };
        if (dialog.ShowDialog(this) != true)
            return;

        _busy = true;
        try
        {
            var workspace = _workspace;
            string outDir = dialog.FolderName;
            var models = workspace.Models.Entries.ToList();
            int ok = 0, failed = 0;

            await System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var model in models)
                {
                    try
                    {
                        string target = Path.Combine(outDir,
                            AssetNaming.RelativePath(model.Name ?? "model", ".glb"));
                        ModelExporter.ExportGlb(workspace, model, target);
                        ok++;
                    }
                    catch (Exception)
                    {
                        failed++;
                    }
                }
            });
            SetStatus($"{ok} .glb -> {outDir}" + (failed > 0 ? $", {failed} ?" : ""));
        }
        finally
        {
            _busy = false;
        }
    }

    private static string SafeLeaf(string? name)
    {
        string normalised = (name ?? "model").Replace('/', '\\');
        int slash = normalised.LastIndexOf('\\');
        string leaf = slash >= 0 ? normalised[(slash + 1)..] : normalised;
        string bare = Path.GetFileNameWithoutExtension(leaf);
        return AssetNaming.Flatten(string.IsNullOrEmpty(bare) ? leaf : bare);
    }

    // ------------------------------------------------------------ animations
    private async void OnLoadAnimations(object sender, RoutedEventArgs e)
    {
        if (_install is null || _busy)
            return;
        if (_selectedModel is null)
        {
            AnimationSummary.Text = Loc.T("ANIM_PICK_MODEL");
            return;
        }

        _busy = true;
        try
        {
            var install = _install;
            string modelName = _selectedModel.Name ?? string.Empty;
            var catalog = _catalog;

            // Indexing is quick, but the clip sweep reads a header per record across
            // 26 thousand records, so it is kept off the UI thread.
            var model = _selectedModel;
            var found = await Task.Run(() =>
            {
                catalog ??= AnimationCatalog.Open(install);
                // Риг подбирается сам: по пути, по имени (RIPLEY_FP -> FEMALEFP...) и
                // с проверкой числа костей против скина модели
                var guess = catalog.GuessSkeleton(modelName, model, HavokDump.TryCreate());
                var (autoId, _, autoEntry) = catalog.ResolveSkeleton(modelName);
                uint? force = guess.entry is not null && (autoEntry is null || guess.id != autoId) ? guess.id : null;
                return (catalog, clips: catalog.ClipsFor(modelName, force),
                    rigs: catalog.ListSkeletons(), force, guess);
            });

            _catalog = found.catalog;
            _clips = found.clips;
            _items = null;
            _forcedSkeleton = found.force;
            if (found.force is not null)
                SetStatus($"{Loc.T("ANIM_SKELETON")}: {found.guess.name} ({found.guess.bones} {Loc.T("ANIM_BONES_SHORT")})");
            FillSkeletonPicker(found.rigs, modelName);
            ApplyClipFilter();
            ShowAnimationSummary(modelName);

            if (_clips.Count == 0)
                SetStatus(Loc.T("ANIM_NONE"));

            BuildMap(_selectedModel);
        }
        catch (Exception ex)
        {
            SetStatus($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Skeleton chosen by hand, overriding what the model path suggests.</summary>
    private uint? _forcedSkeleton;

    private bool _suppressSkeletonEvents;

    /// <summary>One entry of the rig picker.</summary>
    private sealed record RigChoice(uint Id, string Name, int Clips)
    {
        public override string ToString() => $"{Name}  ·  {Clips}";
    }

    /// <summary>
    /// Fills the rig picker with every skeleton that has clips.
    /// </summary>
    /// <remarks>
    /// A level only contains the characters that appear in it: the Alien's nest has the
    /// Alien and a facehugger, and its other 426 models are decals, lights and fog. The
    /// archive, though, holds 653 rigs and 362 of them have clips, Ripley's 984 among
    /// them. Listing them all is what makes those reachable without hunting for a level
    /// that happens to include the right model.
    /// </remarks>
    private void FillSkeletonPicker(List<(uint id, string name, int clips)> rigs, string? modelName)
    {
        _suppressSkeletonEvents = true;
        SkeletonPicker.Items.Clear();

        var (autoId, _, autoEntry) = _catalog!.ResolveSkeleton(modelName);
        RigChoice? select = null;

        foreach (var (id, name, clips) in rigs)
        {
            if (clips == 0)
                continue;
            var choice = new RigChoice(id, name, clips);
            SkeletonPicker.Items.Add(choice);
            if (autoEntry is not null && id == autoId)
                select = choice;
        }

        SkeletonPicker.SelectedItem = select;
        _suppressSkeletonEvents = false;
    }

    private async void OnSkeletonPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSkeletonEvents || _catalog is null || _busy)
            return;
        if (SkeletonPicker.SelectedItem is not RigChoice choice)
            return;

        string modelName = _selectedModel?.Name ?? string.Empty;
        var (autoId, _, autoEntry) = _catalog.ResolveSkeleton(modelName);

        // Remember the override only when it differs from what the model itself implies.
        _forcedSkeleton = autoEntry is not null && choice.Id == autoId ? null : choice.Id;

        _busy = true;
        try
        {
            var catalog = _catalog;
            uint? force = _forcedSkeleton;
            _clips = await Task.Run(() => catalog.ClipsFor(modelName, force));
            _items = null;
            ApplyClipFilter();
            ShowAnimationSummary(modelName);
        }
        catch (Exception ex)
        {
            SetStatus($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Says which rig the list belongs to, and warns when it is not this model's own.
    /// </summary>
    /// <remarks>
    /// Clips can be listed and exported for any rig, but posing needs the mesh that rig
    /// was built for. Playing another character's clip on this model would put bones
    /// where there are none, so the mismatch is stated rather than left to look broken.
    /// </remarks>
    private void ShowAnimationSummary(string? modelName)
    {
        if (_catalog is null)
            return;

        int count = _clips?.Count ?? 0;
        AnimationSummary.Text = $"{Loc.T("ANIM_SKELETON")}: " +
                                $"{_catalog.DescribeSkeleton(modelName, _forcedSkeleton)}    " +
                                $"{Loc.T("ANIM_CLIPS")}: {count}";

        var (_, _, autoEntry) = _catalog.ResolveSkeleton(modelName);
        SkeletonNote.Text = _forcedSkeleton is not null
            ? Loc.T("ANIM_RIG_MISMATCH")
            : autoEntry is null
                ? Loc.T("ANIM_RIG_UNKNOWN")
                : string.Empty;

        ExportAllClipsButton.IsEnabled = count > 0;
    }

    private void OnClipFilterChanged(object sender, TextChangedEventArgs e) => ApplyClipFilter();

    private void ApplyClipFilter()
    {
        if (_clips is null)
            return;
        _items ??= AnimationClipItem.Expand(_clips);
        string needle = ClipFilterBox.Text.Trim();
        AnimationList.ItemsSource = needle.Length == 0
            ? _items
            : _items.Where(c => c.FullName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    .ToList();
    }

    private void OnAnimationSelected(object sender, SelectionChangedEventArgs e)
    {
        bool has = AnimationList.SelectedItem is AnimationClipItem;
        ExportClipButton.IsEnabled = has;
        ExportRawClipButton.IsEnabled = has;
        PlayButton.IsEnabled = has && _built is not null;
        if (AnimationList.SelectedItem is AnimationClipItem clip)
            ShowInfo(clip.Describe());
    }

    // ------------------------------------------------------------- playback
    /// <summary>
    /// Decodes the selected clip through the Havok dumper and starts playing it on
    /// the mesh already in the viewport.
    /// </summary>
    private async void OnPlayAnimation(object sender, RoutedEventArgs e)
    {
        if (_catalog is null || _built is null || _selectedModel is null || _busy)
            return;
        if (AnimationList.SelectedItem is not AnimationClipItem item)
            return;
        var clip = item.Section;

        var dump = HavokDump.TryCreate();
        if (dump is null)
        {
            SetStatus(Loc.F("ANIM_NO_TOOL", HavokDump.ToolName));
            return;
        }

        SetBusy(true);
        SetStatus(Loc.F("ANIM_DECODING", item.ShortName));
        try
        {
            var catalog = _catalog;
            string modelName = _selectedModel.Name ?? string.Empty;
            uint? force = _forcedSkeleton;

            // Sampling a clip runs an external process, so it stays off the UI thread.
            var bundle = await Task.Run(() =>
            {
                byte[] clipHkx = catalog.ExtractHavok(clip);
                byte[]? skeletonHkx = catalog.ExtractSkeletonHavok(modelName, force);
                var loaded = dump.Load(clipHkx, skeletonHkx);
                // Real names, so the frame counter and the status line say something
                // more useful than a section hash.
                AnimationCatalog.ApplyNames(clip, loaded);
                return loaded;
            });

            if (bundle.Skeleton.Count == 0 || bundle.Clips.Count == 0)
            {
                SetStatus(Loc.T("ANIM_EMPTY"));
                return;
            }

            _bundle = bundle;
            // Секция может держать сотни клипов — играем именно выбранный
            _clip = item.Pick(bundle) ?? bundle.Clips.OrderByDescending(c => c.Frames).First();
            _skinners = _built.Parts3D
                .Select(part => new MeshSkinner(part.Mesh))
                .ToList();

            FrameSlider.Minimum = 0;
            FrameSlider.Maximum = Math.Max(0, _clip.Frames - 1);
            FrameSlider.IsEnabled = true;
            StopButton.IsEnabled = true;

            SetStatus($"{_clip.Name}: {_clip.Frames} × {Loc.T("ANIM_FRAME").ToLowerInvariant()}, " +
                      $"{_clip.Duration:F2} с, {Loc.T("ANIM_SKELETON").ToLowerInvariant()} " +
                      $"{bundle.Skeleton.Count}");

            SelectTab(TabModel);
            StartPlayback();
        }
        catch (Exception ex)
        {
            SetStatus($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void StartPlayback()
    {
        if (_clip is null)
            return;
        _timer ??= new DispatcherTimer(DispatcherPriority.Render);
        _timer.Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1f, _clip.Fps));
        _timer.Tick -= OnPlaybackTick;
        _timer.Tick += OnPlaybackTick;
        _timer.Start();
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        if (_clip is null)
        {
            _timer?.Stop();
            return;
        }
        _frame = (_frame + 1) % Math.Max(1, _clip.Frames);
        // Setting the slider raises ValueChanged, which is what applies the pose.
        FrameSlider.Value = _frame;
    }

    private void OnStopAnimation(object sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        // Put the mesh back the way it was decoded.
        if (_built is not null)
            foreach (var part in _built.Parts3D)
                WriteGeometry(part.Geometry, part.Mesh.Positions, part.Mesh.Normals);
        SetStatus(Loc.T("STATUS_READY"));
    }

    private void OnFrameChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _frame = (int)e.NewValue;
        FrameLabel.Text = _clip is null ? _frame.ToString() : $"{_frame} / {_clip.Frames - 1}";
        ApplyPose(_frame);
    }

    private void ApplyPose(int frame)
    {
        if (_bundle is null || _clip is null || _skinners is null || _built is null)
            return;

        var pose = PoseEvaluator.WorldPose(_bundle.Skeleton, _clip, frame);
        var skin = PoseEvaluator.SkinMatrices(_bundle.Skeleton, pose);

        for (int i = 0; i < _skinners.Count && i < _built.Parts3D.Count; i++)
        {
            var skinner = _skinners[i];
            skinner.Apply(skin);
            WriteGeometry(_built.Parts3D[i].Geometry, skinner.Positions, skinner.Normals);
        }

        // Clips that travel, and most of them do, walk the character straight out of
        // frame. Following the centre keeps it in view without touching zoom or angle,
        // so the camera the user set up still applies.
        if (FollowModelCheck?.IsChecked == true)
        {
            var bounds = _built.Geometry.Bounds;
            if (!bounds.IsEmpty)
            {
                var centre = new Point3D(bounds.X + bounds.SizeX / 2,
                    bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
                // Eased rather than snapped: a hard follow makes the whole scene jitter.
                _target = new Point3D(
                    _target.X + (centre.X - _target.X) * 0.25,
                    _target.Y + (centre.Y - _target.Y) * 0.25,
                    _target.Z + (centre.Z - _target.Z) * 0.25);
                UpdateCamera();
            }
        }
    }

    /// <summary>
    /// Pushes new vertex data into an existing geometry. Whole collections are
    /// replaced rather than edited in place: WPF raises a change notification per
    /// element otherwise, which costs far more than one rebuild.
    /// </summary>
    private static void WriteGeometry(MeshGeometry3D geometry,
        System.Numerics.Vector3[] positions, System.Numerics.Vector3[]? normals)
    {
        var points = new Point3DCollection(positions.Length);
        foreach (var p in positions)
            points.Add(new Point3D(p.X, p.Y, p.Z));
        geometry.Positions = points;

        if (normals is { Length: > 0 })
        {
            var vectors = new Vector3DCollection(normals.Length);
            foreach (var n in normals)
                vectors.Add(new Vector3D(n.X, n.Y, n.Z));
            geometry.Normals = vectors;
        }
    }

    /// <summary>
    /// Writes the selected clip as a .glb: the model, its rig, and the animation
    /// baked in, which is what Blender opens directly.
    /// </summary>
    /// <remarks>
    /// The raw Havok packfile is still available beside this, because it is the exact
    /// payload the game ships and nothing about it is interpreted. But a .hkx cannot be
    /// opened by anything a person is likely to have, so the animation goes out as
    /// glTF by default.
    /// </remarks>
    private async void OnExportAnimation(object sender, RoutedEventArgs e)
    {
        if (_catalog is null || _selectedModel is null || _workspace is null
            || AnimationList.SelectedItem is not AnimationClipItem item)
            return;

        var dialog = new SaveFileDialog
        {
            Title = Loc.T("DLG_PICK_OUT"),
            Filter = Loc.T("DLG_GLB_FILTER"),
            FileName = Path.ChangeExtension(AssetNaming.Flatten(item.ShortName), ".glb"),
        };
        if (dialog.ShowDialog(this) != true)
            return;

        await ExportWithAnimations(new[] { item.Section }, dialog.FileName, item);
    }

    /// <summary>Every clip of this character in one file, ready to browse in Blender.</summary>
    private async void OnExportAllAnimations(object sender, RoutedEventArgs e)
    {
        if (_catalog is null || _clips is null || _clips.Count == 0 || _selectedModel is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = Loc.T("DLG_PICK_OUT"),
            Filter = Loc.T("DLG_GLB_FILTER"),
            FileName = SafeLeaf(_selectedModel.Name) + "_all_animations.glb",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        await ExportWithAnimations(_clips, dialog.FileName);
    }

    /// <summary>
    /// Decodes the given sections, names their clips, and writes model, skin and
    /// animations into one .glb.
    /// </summary>
    private async Task ExportWithAnimations(IReadOnlyList<AnimationClipRef> sections, string path,
        AnimationClipItem? only = null)
    {
        if (_catalog is null || _workspace is null || _selectedModel is null)
            return;

        var dumper = HavokDump.TryCreate();
        if (dumper is null)
        {
            SetStatus(Loc.F("ANIM_NO_TOOL", HavokDump.ToolName));
            return;
        }

        var model = _selectedModel;
        var workspace = _workspace;
        var catalog = _catalog;
        uint? force = _forcedSkeleton;
        SetBusy(true);

        try
        {
            var result = await Task.Run(() =>
            {
                byte[]? skeletonHkx = catalog.ExtractSkeletonHavok(model.Name, force);
                if (skeletonHkx is null)
                    throw new InvalidOperationException(Loc.T("ANIM_NO_SKELETON"));

                SkeletonData? skeleton = null;
                var clips = new List<ClipData>();
                float fps = 30f;
                int failed = 0;

                foreach (var section in sections)
                {
                    try
                    {
                        var bundle = dumper.Load(catalog.ExtractHavok(section), skeletonHkx);
                        AnimationCatalog.ApplyNames(section, bundle);
                        skeleton ??= bundle.Skeleton;
                        if (bundle.Fps > 0f)
                            fps = bundle.Fps;
                        // Additive clips are deltas meant to be layered, and on their own
                        // they read as a mesh that barely moves. Kept, but they are not
                        // what makes the export useful.
                        if (only is not null && only.Section == section)
                        {
                            var picked = only.Pick(bundle);
                            if (picked is not null)
                                clips.Add(picked);
                        }
                        else
                            clips.AddRange(bundle.Clips);
                    }
                    catch (Exception)
                    {
                        failed++;
                    }
                }

                skeleton ??= dumper.LoadSkeletonOnly(skeletonHkx);
                var animation = new AnimationBundle
                {
                    Skeleton = skeleton,
                    Clips = clips,
                    Fps = fps,
                };

                var export = ModelExporter.ExportGlb(workspace, model, path, null, animation);
                var report = GlbValidator.Validate(path);
                return (export, report, failed);
            });

            string note = result.failed > 0
                ? Loc.F("ANIM_EXPORT_PARTIAL", result.export.Clips, result.failed)
                : Loc.F("ANIM_EXPORT_OK", result.export.Clips, result.export.Bones);
            SetStatus($"{note}  {path}  {result.export.Bytes / 1048576.0:F2} МБ  " +
                      $"{(result.report.Ok ? Loc.T("EXPORT_VALID") : result.report.ToString())}");
        }
        catch (Exception ex)
        {
            SetStatus($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// The untouched Havok packfile, for anyone who wants the original bytes rather
    /// than a conversion.
    /// </summary>
    private void OnExportAnimationRaw(object sender, RoutedEventArgs e)
    {
        if (_catalog is null || AnimationList.SelectedItem is not AnimationClipItem item)
            return;
        var clip = item.Section;
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("DLG_PICK_OUT"),
            Filter = Loc.T("DLG_HKX_FILTER"),
            FileName = Path.ChangeExtension(AssetNaming.Flatten(item.ShortName), ".hkx"),
        };
        if (dialog.ShowDialog(this) != true)
            return;

        byte[] havok = _catalog.ExtractHavok(clip);
        File.WriteAllBytes(dialog.FileName, havok);
        SetStatus(Loc.F("STATUS_EXPORTED", dialog.FileName, havok.Length / 1048576.0));
    }
}
