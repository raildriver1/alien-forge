using System.Collections.ObjectModel;
using AlienForge.Core;

namespace AlienForge.App;

/// <summary>
/// Tree item wrapper that builds its children only when the node is expanded.
/// </summary>
/// <remarks>
/// A WPF TreeView asks whether an item has children in order to draw the expander,
/// and answering that honestly would walk the whole archive. So every node that
/// might have children starts with a single placeholder, which is swapped for the
/// real list the first time the user opens it.
/// </remarks>
public sealed class AssetNodeView
{
    private static readonly AssetNodeView Placeholder =
        new(new AssetNode(AssetKind.Note, "..."), loaded: true);

    private bool _loaded;

    public AssetNodeView(AssetNode node, bool loaded = false)
    {
        Node = node;
        Children = new ObservableCollection<AssetNodeView>();
        _loaded = loaded || !node.HasChildren;
        if (!_loaded)
            Children.Add(Placeholder);
    }

    public AssetNode Node { get; }
    public ObservableCollection<AssetNodeView> Children { get; }

    public string Name => Node.Name;
    public string? Detail => Node.Detail;
    public AssetKind Kind => Node.Kind;

    public string Label => Node.Detail is null ? Node.Name : $"{Node.Name}";
    public string Suffix => Node.Detail is null ? string.Empty : Node.Detail;

    /// <summary>Short tag shown before the name so kinds are scannable.</summary>
    public string Tag => Node.Kind switch
    {
        AssetKind.Level => "УР",
        AssetKind.Folder => "ПАПКА",
        AssetKind.Model => "МОДЕЛЬ",
        AssetKind.Component => "КОМП",
        AssetKind.Lod => "LOD",
        AssetKind.Submesh => "МЕШ",
        AssetKind.Material => "МАТ",
        AssetKind.Texture => "ТЕКС",
        AssetKind.Shader => "ШЕЙД",
        _ => "",
    };

    public void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        Children.Clear();
        foreach (var child in Node.Children)
            Children.Add(new AssetNodeView(child));
    }
}
