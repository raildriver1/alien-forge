using System.Windows;
using System.Windows.Controls;
using AlienForge.Core.Export;

namespace AlienForge.App;

/// <summary>Маленькое окно с флажками экспорта уровня. Возвращает null при отмене.</summary>
internal static class LevelExportDialog
{
    public static LevelExporter.Options? Ask(Window owner)
    {
        var textures = new CheckBox { Content = Loc.T("SCENE_OPT_TEXTURES"), IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
        var decals = new CheckBox { Content = Loc.T("SCENE_OPT_DECALS"), IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
        var unlit = new CheckBox { Content = Loc.T("SCENE_OPT_UNLIT"), IsChecked = false, Margin = new Thickness(0, 4, 0, 4) };
        var gray = new CheckBox { Content = Loc.T("SCENE_OPT_GRAY"), IsChecked = false, Margin = new Thickness(0, 4, 0, 4) };
        var gltf = new CheckBox { Content = Loc.T("SCENE_OPT_GLTF"), IsChecked = false, Margin = new Thickness(0, 4, 0, 4) };
        var collision = new CheckBox { Content = Loc.T("SCENE_OPT_COLLISION"), IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
        var godotExtras = new CheckBox { Content = Loc.T("SCENE_OPT_GODOT"), IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
        var allMaps = new CheckBox { Content = Loc.T("SCENE_OPT_ALLMAPS"), IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };

        var ok = new Button { Content = Loc.T("SCENE_RUN"), IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Loc.T("BTN_CANCEL"), IsCancel = true, Padding = new Thickness(14, 4, 14, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(textures);
        panel.Children.Add(decals);
        panel.Children.Add(allMaps);
        panel.Children.Add(collision);
        panel.Children.Add(godotExtras);
        panel.Children.Add(unlit);
        panel.Children.Add(gray);
        panel.Children.Add(gltf);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = Loc.T("SCENE_DLG_TITLE"),
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel,
            Background = owner.Background,
            Foreground = owner.Foreground,
        };
        ok.Click += (_, _) => { window.DialogResult = true; window.Close(); };

        if (window.ShowDialog() != true)
            return null;
        return new LevelExporter.Options
        {
            Textures = textures.IsChecked == true,
            Decals = decals.IsChecked == true,
            Unlit = unlit.IsChecked == true,
            IncludeUnsupported = gray.IsChecked == true,
            Gltf = gltf.IsChecked == true,
            AllMaps = allMaps.IsChecked == true,
            GodotCollision = collision.IsChecked == true,
            Occluders = godotExtras.IsChecked == true,
            Lights = godotExtras.IsChecked == true,
            Particles = godotExtras.IsChecked == true,
            Fog = godotExtras.IsChecked == true,
            RigidBodies = godotExtras.IsChecked == true,
            Zones = godotExtras.IsChecked == true,
        };
    }
}
