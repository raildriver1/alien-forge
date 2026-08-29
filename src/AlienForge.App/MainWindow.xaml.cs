using System.Text;
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
    private bool _busy;

    // animation playback
    private MeshPreview.Built? _built;
    private AnimationBundle? _bundle;
    private ClipData? _clip;
    private List<MeshSkinner>? _skinners;
    private DispatcherTimer? _timer;
    private int _frame;

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

    private void OnLanguageRussian(object sender, RoutedEventArgs e)
        => Loc.Current.Language = Lang.Russian;

    private void OnLanguageEnglish(object sender, RoutedEventArgs e)
        => Loc.Current.Language = Lang.English;

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
            ExportClipButton.IsEnabled = false;
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
                SelectTab(TabMap);
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
            clips.Select(clip => new MapNode
            {
                Kind = MapNodeKind.Animation,
                Title = clip.ShortName,
                Subtitle = clip.SizeText,
                Payload = clip,
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
            case AnimationClipRef clip:
                AnimationList.SelectedItem = clip;
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
            scene.Children.Add(new AmbientLight(Color.FromRgb(0x55, 0x58, 0x5C)));
            scene.Children.Add(new DirectionalLight(Color.FromRgb(0xE8, 0xEC, 0xF0),
                new Vector3D(-0.5, -0.8, -0.6)));
            scene.Children.Add(new DirectionalLight(Color.FromRgb(0x30, 0x40, 0x50),
                new Vector3D(0.7, 0.3, 0.5)));
            SceneRoot.Content = scene;

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
        double cp = Math.Cos(_pitch), sp = Math.Sin(_pitch);
        var offset = new Vector3D(Math.Sin(_yaw) * cp, sp, Math.Cos(_yaw) * cp) * _distance;
        Camera.Position = _target + offset;
        Camera.LookDirection = -offset;
        Camera.UpDirection = new Vector3D(0, 1, 0);
        Camera.NearPlaneDistance = Math.Max(0.001, _distance * 0.005);
        Camera.FarPlaneDistance = _distance * 40;
    }

    private void OnResetCamera(object sender, RoutedEventArgs e)
    {
        if (SceneRoot.Content is Model3DGroup group)
            FrameCamera(group.Bounds);
    }

    private void OnToggleCollision(object sender, RoutedEventArgs e)
    {
        if (_selectedModel is not null)
            ShowModel(_selectedModel);
    }

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(Viewport);
        _dragOrbit = e.ChangedButton == MouseButton.Left;
        _dragPan = e.ChangedButton == MouseButton.Right;
        Viewport.CaptureMouse();
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragOrbit = _dragPan = false;
        Viewport.ReleaseMouseCapture();
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragOrbit && !_dragPan)
            return;
        var now = e.GetPosition(Viewport);
        double dx = now.X - _dragFrom.X, dy = now.Y - _dragFrom.Y;
        _dragFrom = now;

        if (_dragOrbit)
        {
            _yaw -= dx * 0.01;
            _pitch = Math.Clamp(_pitch + dy * 0.01, -1.5, 1.5);
        }
        else
        {
            // Pan across the camera plane, scaled so it feels the same at any zoom.
            var forward = Camera.LookDirection;
            forward.Normalize();
            var right = Vector3D.CrossProduct(new Vector3D(0, 1, 0), forward);
            if (right.Length < 1e-6)
                right = new Vector3D(1, 0, 0);
            right.Normalize();
            var up = Vector3D.CrossProduct(forward, right);
            double scale = _distance * 0.0015;
            _target += right * (dx * scale) + up * (dy * scale);
        }
        UpdateCamera();
    }

    private void OnViewportWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.88 : 1.135), 0.05, 5000);
        UpdateCamera();
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
            var found = await System.Threading.Tasks.Task.Run(() =>
            {
                catalog ??= AnimationCatalog.Open(install);
                return (catalog, clips: catalog.ClipsFor(modelName));
            });

            _catalog = found.catalog;
            _clips = found.clips;
            ApplyClipFilter();

            AnimationSummary.Text =
                $"{Loc.T("ANIM_SKELETON")}: {_catalog!.DescribeSkeleton(modelName)}    " +
                $"{Loc.T("ANIM_CLIPS")}: {_clips.Count}";
            ExportAllClipsButton.IsEnabled = _clips.Count > 0;
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

    private void OnClipFilterChanged(object sender, TextChangedEventArgs e) => ApplyClipFilter();

    private void ApplyClipFilter()
    {
        if (_clips is null)
            return;
        string needle = ClipFilterBox.Text.Trim();
        AnimationList.ItemsSource = needle.Length == 0
            ? _clips
            : _clips.Where(c => c.ShortName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    .ToList();
    }

    private void OnAnimationSelected(object sender, SelectionChangedEventArgs e)
    {
        bool has = AnimationList.SelectedItem is AnimationClipRef;
        ExportClipButton.IsEnabled = has;
        PlayButton.IsEnabled = has && _built is not null;
        if (AnimationList.SelectedItem is AnimationClipRef clip)
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
        if (AnimationList.SelectedItem is not AnimationClipRef clip)
            return;

        var dump = HavokDump.TryCreate();
        if (dump is null)
        {
            SetStatus(Loc.F("ANIM_NO_TOOL", HavokDump.ToolName));
            return;
        }

        SetBusy(true);
        SetStatus(Loc.F("ANIM_DECODING", clip.ShortName));
        try
        {
            var catalog = _catalog;
            string modelName = _selectedModel.Name ?? string.Empty;

            // Sampling a clip runs an external process, so it stays off the UI thread.
            var bundle = await System.Threading.Tasks.Task.Run(() =>
            {
                byte[] clipHkx = catalog.ExtractHavok(clip);
                byte[]? skeletonHkx = catalog.ExtractSkeletonHavok(modelName);
                return dump.Load(clipHkx, skeletonHkx);
            });

            if (bundle.Skeleton.Count == 0 || bundle.Clips.Count == 0)
            {
                SetStatus(Loc.T("ANIM_EMPTY"));
                return;
            }

            _bundle = bundle;
            // A container can hold several animations; the longest is the real one.
            _clip = bundle.Clips.OrderByDescending(c => c.Frames).First();
            _skinners = _built.Parts3D
                .Select(part => new MeshSkinner(part.mesh))
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
            foreach (var (mesh, geometry) in _built.Parts3D)
                WriteGeometry(geometry, mesh.Positions, mesh.Normals);
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
            WriteGeometry(_built.Parts3D[i].geometry, skinner.Positions, skinner.Normals);
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
    /// Writes the selected clip's embedded Havok packfile out unchanged. Not a
    /// finished animation yet, but it is the real payload and can be inspected or
    /// converted outside the app.
    /// </summary>
    private void OnExportAnimation(object sender, RoutedEventArgs e)
    {
        if (_catalog is null || AnimationList.SelectedItem is not AnimationClipRef clip)
            return;
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("DLG_PICK_OUT"),
            Filter = Loc.T("DLG_HKX_FILTER"),
            FileName = Path.ChangeExtension(AssetNaming.Flatten(clip.ShortName), ".hkx"),
        };
        if (dialog.ShowDialog(this) != true)
            return;

        byte[] havok = _catalog.ExtractHavok(clip);
        File.WriteAllBytes(dialog.FileName, havok);
        SetStatus(Loc.F("STATUS_EXPORTED", dialog.FileName, havok.Length / 1048576.0));
    }

    private void OnExportAllAnimations(object sender, RoutedEventArgs e)
    {
        if (_catalog is null || _clips is null || _clips.Count == 0)
            return;
        var dialog = new OpenFolderDialog { Title = Loc.T("DLG_PICK_OUT") };
        if (dialog.ShowDialog(this) != true)
            return;

        int written = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var clip in _clips)
        {
            byte[] havok = _catalog.ExtractHavok(clip);
            if (havok.Length == 0)
                continue;
            // Names repeat inside the archive, so a suffix keeps variants apart.
            string bare = Path.GetFileNameWithoutExtension(AssetNaming.Flatten(clip.ShortName));
            string candidate = bare + ".hkx";
            int n = 2;
            while (!used.Add(candidate))
                candidate = $"{bare}_{n++}.hkx";
            File.WriteAllBytes(Path.Combine(dialog.FolderName, candidate), havok);
            written++;
        }
        SetStatus(Loc.F("SAVED_N", written, dialog.FolderName));
    }
}
