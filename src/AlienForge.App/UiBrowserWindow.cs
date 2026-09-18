using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AlienForge.Core;
using AlienForge.Core.Animation;
using AlienForge.Core.Imaging;
using AlienForge.Core.Ui;
using Microsoft.Win32;

namespace AlienForge.App;

/// <summary>
/// Окно «Интерфейс игры»: список UI.PAK, предпросмотр прямо здесь (DDS — сразу;
/// .GFX — кадры ролика и спрайты, отрендеренные JPEXS в кэш, с проигрыванием),
/// открытие в JPEXS, выгрузка спрайтов PNG-последовательностями, извлечение.
/// </summary>
internal sealed class UiBrowserWindow : Window
{
    private readonly UiArchive _archive;
    private readonly ListBox _list = new();
    private readonly TextBox _filter = new() { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _onlyGfx = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

    // предпросмотр
    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _previewInfo = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox _sprites = new() { MinWidth = 260, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _frame = new() { Minimum = 1, Maximum = 1, Value = 1, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center, MinWidth = 180 };
    private readonly TextBlock _frameLabel = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 70, Margin = new Thickness(6, 0, 6, 0) };
    private readonly ToggleButton _play = new() { Content = "▶", Padding = new Thickness(10, 2, 10, 2), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _render = new() { Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
    private readonly DispatcherTimer _timer = new();
    private List<string> _frames = new();
    private double _fps = 30;
    private Pak2Entry? _shown;
    private bool _busy;

    public UiBrowserWindow(GameInstall install)
    {
        _archive = UiArchive.Open(install);

        Title = Loc.T("UI_TITLE");
        Width = 1180;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (Application.Current.MainWindow is Window main)
        {
            Background = main.Background;
            Foreground = main.Foreground;
        }

        var root = new Grid { Margin = new Thickness(12) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // --- левая колонка: фильтр, список, кнопки
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(new TextBlock { Text = Loc.T("UI_FILTER"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        top.Children.Add(_filter);
        _onlyGfx.Content = Loc.T("UI_ONLY_GFX");
        top.Children.Add(_onlyGfx);
        Grid.SetRow(top, 0); Grid.SetColumn(top, 0);
        root.Children.Add(top);

        _list.FontFamily = new FontFamily("Consolas");
        _list.SelectionMode = SelectionMode.Extended;
        Grid.SetRow(_list, 1); Grid.SetColumn(_list, 0);
        root.Children.Add(_list);

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(MakeButton(Loc.T("UI_OPEN_JPEXS"), OpenInJpexs));
        buttons.Children.Add(MakeButton(Loc.T("UI_EXPORT_SPRITES"), () => ExportItems("sprite")));
        buttons.Children.Add(MakeButton(Loc.T("UI_EXPORT_ALL"), () => ExportItems("sprite,frame,image,shape,script,font,text")));
        buttons.Children.Add(MakeButton(Loc.T("UI_EXTRACT"), ExtractSelected));
        buttons.Children.Add(MakeButton(Loc.T("UI_EXTRACT_ALL"), ExtractAll));
        Grid.SetRow(buttons, 2); Grid.SetColumn(buttons, 0);
        root.Children.Add(buttons);

        var bottom = new StackPanel();
        bottom.Children.Add(new TextBlock { Text = Loc.T("UI_HINT"), TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0) });
        bottom.Children.Add(_status);
        Grid.SetRow(bottom, 3); Grid.SetColumn(bottom, 0); Grid.SetColumnSpan(bottom, 3);
        root.Children.Add(bottom);

        // --- правая колонка: предпросмотр
        var previewHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x13)),
            CornerRadius = new CornerRadius(5),
            Child = _image,
            Padding = new Thickness(6),
        };
        Grid.SetRow(previewHost, 1); Grid.SetColumn(previewHost, 2);
        root.Children.Add(previewHost);

        var controls = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var line1 = new StackPanel { Orientation = Orientation.Horizontal };
        _render.Content = Loc.T("UI_RENDER");
        _render.Click += async (_, _) => await RenderShown(force: true);
        line1.Children.Add(_render);
        line1.Children.Add(new TextBlock { Text = Loc.T("UI_SPRITE"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        line1.Children.Add(_sprites);
        controls.Children.Add(line1);
        var line2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        line2.Children.Add(_play);
        line2.Children.Add(_frameLabel);
        line2.Children.Add(_frame);
        controls.Children.Add(line2);
        controls.Children.Add(_previewInfo);
        Grid.SetRow(controls, 2); Grid.SetColumn(controls, 2);
        root.Children.Add(controls);

        var head = new TextBlock { Text = Loc.T("UI_PREVIEW"), Opacity = 0.7, Margin = new Thickness(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(head, 0); Grid.SetColumn(head, 2);
        root.Children.Add(head);

        Content = root;

        _filter.TextChanged += (_, _) => Refill();
        _onlyGfx.Click += (_, _) => Refill();
        _list.SelectionChanged += async (_, _) => await ShowSelected();
        _list.MouseDoubleClick += (_, _) => OpenInJpexs();
        _sprites.SelectionChanged += (_, _) => LoadSpriteFrames();
        _frame.ValueChanged += (_, _) => ShowFrame((int)_frame.Value);
        _play.Checked += (_, _) => { _timer.Interval = TimeSpan.FromSeconds(1 / Math.Max(1, _fps)); _timer.Start(); _play.Content = "■"; };
        _play.Unchecked += (_, _) => { _timer.Stop(); _play.Content = "▶"; };
        _timer.Tick += (_, _) =>
        {
            if (_frames.Count == 0) return;
            _frame.Value = _frame.Value >= _frames.Count ? 1 : _frame.Value + 1;
        };
        Closed += (_, _) => _timer.Stop();
        Refill();

        string? jpexs = Jpexs.FindGui();
        _status.Text = jpexs is null ? Loc.T("UI_NO_JPEXS") : Loc.F("UI_JPEXS_AT", jpexs);
    }

    private static Button MakeButton(string text, Action action)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 6) };
        b.Click += (_, _) => action();
        return b;
    }

    private void Refill()
    {
        _list.Items.Clear();
        foreach (var e in _archive.Find(_filter.Text.Trim()))
        {
            if (_onlyGfx.IsChecked == true && !e.Name.EndsWith(".GFX", StringComparison.OrdinalIgnoreCase))
                continue;
            _list.Items.Add(new ListBoxItem { Content = $"{e.Name,-52} {e.Length / 1024.0,8:0.0} KB", Tag = e });
        }
    }

    private List<Pak2Entry> Selected()
        => _list.SelectedItems.Cast<ListBoxItem>().Select(i => (Pak2Entry)i.Tag).ToList();

    // ------------------------------------------------------------ предпросмотр
    private async System.Threading.Tasks.Task ShowSelected()
    {
        var entry = Selected().FirstOrDefault();
        if (entry is null || entry == _shown) return;
        _shown = entry;
        _play.IsChecked = false;
        _frames = new List<string>();
        _sprites.Items.Clear();
        _frame.Maximum = 1;
        _image.Source = null;
        _previewInfo.Text = "";

        if (entry.Name.EndsWith(".DDS", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = _archive.Read(entry);
            var img = WicDecoder.TryDecode(bytes, out string? error);
            if (img is null) { _previewInfo.Text = error ?? "?"; return; }
            _image.Source = MeshPreview.ToBitmap(img);
            _previewInfo.Text = $"{UiArchive.Leaf(entry.Name)}  {img.Width}×{img.Height}";
            return;
        }
        if (!entry.Name.EndsWith(".GFX", StringComparison.OrdinalIgnoreCase))
        {
            _previewInfo.Text = UiArchive.Leaf(entry.Name);
            return;
        }

        var header = UiArchive.ReadMovieHeader(_archive.Read(entry));
        if (header is { } h)
        {
            _fps = h.fps > 0 ? h.fps : 30;
            _previewInfo.Text = $"{UiArchive.Leaf(entry.Name)}  {h.width}×{h.height}, {h.fps:0.##} fps, {h.frames} {Loc.T("UI_FRAMES_SHORT")}";
        }
        await RenderShown(force: false);
    }

    /// <summary>Кадры ролика и спрайты — через ffdec-cli в кэш; потом список спрайтов.</summary>
    private async System.Threading.Tasks.Task RenderShown(bool force)
    {
        var entry = _shown;
        if (entry is null || _busy || !entry.Name.EndsWith(".GFX", StringComparison.OrdinalIgnoreCase)) return;
        string renderDir = UiArchive.RenderDirOf(entry);
        bool cached = Directory.Exists(renderDir) && Directory.EnumerateFiles(renderDir, "*.png", SearchOption.AllDirectories).Any();
        if (!cached || force)
        {
            if (Jpexs.FindCli() is null) { _status.Text = Loc.T("UI_NO_JPEXS"); return; }
            _busy = true;
            _render.IsEnabled = false;
            try
            {
                string path = _archive.EnsureCached(entry);
                if (Directory.Exists(renderDir)) Directory.Delete(renderDir, true);
                _status.Text = Loc.F("UI_EXPORTING", UiArchive.Leaf(entry.Name));
                var (code, output) = await System.Threading.Tasks.Task.Run(() => Jpexs.ExportItems(path, renderDir, "frame,sprite",
                    line => Dispatcher.BeginInvoke(new Action(() => _status.Text = $"{UiArchive.Leaf(entry.Name)}: {line}"))));
                if (code != 0)
                {
                    _status.Text = Loc.F("UI_FAILED", output.Length > 400 ? output[^400..] : output);
                    return;
                }
                _status.Text = Loc.F("UI_RENDERED", UiArchive.Leaf(entry.Name));
            }
            finally
            {
                _busy = false;
                _render.IsEnabled = true;
            }
        }
        if (_shown != entry) return;
        FillSprites(renderDir);
    }

    private void FillSprites(string renderDir)
    {
        _sprites.Items.Clear();
        string framesDir = Path.Combine(renderDir, "frames");
        if (Directory.Exists(framesDir))
            _sprites.Items.Add(new ComboBoxItem { Content = Loc.T("UI_WHOLE_MOVIE"), Tag = framesDir });
        else if (Directory.EnumerateFiles(renderDir, "*.png").Any())
            _sprites.Items.Add(new ComboBoxItem { Content = Loc.T("UI_WHOLE_MOVIE"), Tag = renderDir });
        string spritesDir = Path.Combine(renderDir, "sprites");
        if (Directory.Exists(spritesDir))
            foreach (var dir in Directory.EnumerateDirectories(spritesDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                int n = Directory.EnumerateFiles(dir, "*.png").Count();
                _sprites.Items.Add(new ComboBoxItem { Content = $"{Path.GetFileName(dir)}  ({n})", Tag = dir });
            }
        if (_sprites.Items.Count > 0)
            _sprites.SelectedIndex = 0;
    }

    private void LoadSpriteFrames()
    {
        _play.IsChecked = false;
        if (_sprites.SelectedItem is not ComboBoxItem item || item.Tag is not string dir) return;
        _frames = Directory.EnumerateFiles(dir, "*.png")
            .OrderBy(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out int n) ? n : int.MaxValue)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _frame.Maximum = Math.Max(1, _frames.Count);
        _frame.Value = 1;
        ShowFrame(1);
        if (_frames.Count > 1) _play.IsChecked = true;
    }

    private void ShowFrame(int index)
    {
        if (_frames.Count == 0) { _image.Source = null; return; }
        index = Math.Clamp(index, 1, _frames.Count);
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(_frames[index - 1]);
            bmp.EndInit();
            bmp.Freeze();
            _image.Source = bmp;
            _frameLabel.Text = $"{index} / {_frames.Count}";
        }
        catch (Exception ex)
        {
            _previewInfo.Text = ex.Message;
        }
    }

    // ------------------------------------------------------------ действия
    private void OpenInJpexs()
    {
        var entry = Selected().FirstOrDefault();
        if (entry is null) return;
        string path = _archive.EnsureCached(entry);
        var proc = Jpexs.Open(path);
        _status.Text = proc is null ? Loc.T("UI_NO_JPEXS") : $"{UiArchive.Leaf(entry.Name)} -> JPEXS";
    }

    private async void ExportItems(string items)
    {
        var entries = Selected().Where(e => e.Name.EndsWith(".GFX", StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Count == 0 || _busy) return;
        if (Jpexs.FindCli() is null) { _status.Text = Loc.T("UI_NO_JPEXS"); return; }
        var dialog = new OpenFolderDialog { Title = Loc.T("DLG_PICK_OUT") };
        if (dialog.ShowDialog(this) != true) return;

        _busy = true;
        try
        {
            int files = 0;
            string lastDir = dialog.FolderName;
            foreach (var entry in entries)
            {
                string path = _archive.EnsureCached(entry);
                string outDir = Path.Combine(dialog.FolderName, Path.GetFileNameWithoutExtension(UiArchive.Leaf(entry.Name)));
                lastDir = outDir;
                _status.Text = Loc.F("UI_EXPORTING", UiArchive.Leaf(entry.Name));
                var (code, output) = await System.Threading.Tasks.Task.Run(() => Jpexs.ExportItems(path, outDir, items,
                    line => Dispatcher.BeginInvoke(new Action(() => _status.Text = $"{UiArchive.Leaf(entry.Name)}: {line}"))));
                if (code != 0)
                {
                    _status.Text = Loc.F("UI_FAILED", output.Length > 400 ? output[^400..] : output);
                    return;
                }
                files += Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).Count();
            }
            _status.Text = Loc.F("UI_EXPORTED", files, dialog.FolderName);
            try { Process.Start(new ProcessStartInfo("explorer.exe", entries.Count == 1 ? lastDir : dialog.FolderName) { UseShellExecute = true }); } catch { }
        }
        finally
        {
            _busy = false;
        }
    }

    private void ExtractSelected()
    {
        var entries = Selected();
        if (entries.Count == 0) return;
        var dialog = new OpenFolderDialog { Title = Loc.T("DLG_PICK_OUT") };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var e in entries)
            _archive.Extract(e, dialog.FolderName);
        _status.Text = Loc.F("UI_EXPORTED", entries.Count, dialog.FolderName);
    }

    private void ExtractAll()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("DLG_PICK_OUT") };
        if (dialog.ShowDialog(this) != true) return;
        int n = 0;
        foreach (var e in _archive.Entries)
        {
            _archive.Extract(e, dialog.FolderName, keepFolders: true);
            n++;
        }
        _status.Text = Loc.F("UI_EXPORTED", n, dialog.FolderName);
    }
}
