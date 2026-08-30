using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AlienForge.App;

/// <summary>What a map node stands for, which drives its colour and click behaviour.</summary>
public enum MapNodeKind
{
    Model,
    Hub,
    Material,
    Texture,
    Animation,
    More,
}

public sealed class MapNode
{
    public required MapNodeKind Kind { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }

    /// <summary>The underlying object, handed back on click.</summary>
    public object? Payload { get; init; }
}

/// <summary>
/// A radial dependency map: the model sits in the middle and what it is built from
/// fans out around it.
/// </summary>
/// <remarks>
/// Drawn onto a Canvas by hand rather than with a layout panel, because the point of
/// the view is the geometry: distance from the centre shows depth of the dependency,
/// and the direction groups the kinds. Counts are shown on hubs so a model with
/// hundreds of clips stays readable instead of drawing hundreds of boxes.
/// <para>
/// Spacing is derived from the boxes rather than guessed. Every node is measured
/// before anything is positioned, and the arc a group occupies is then made wide
/// enough, or pushed far enough out, that neighbours keep a real gap. Fixed angles
/// cannot do this: fourteen leaves across a 1.25 radian fan at radius 165 leave about
/// fifteen pixels each, while the boxes are nearly two hundred wide.
/// </para>
/// </remarks>
public sealed class AssetMapView : Border
{
    private const double HubDistance = 250;
    private const double LeafDistance = 210;

    /// <summary>Clear space kept between neighbouring boxes, in pixels.</summary>
    private const double NodeGap = 14;

    /// <summary>Widest arc one group may occupy, so groups stay visually separate.</summary>
    private const double MaxGroupSpread = 1.5;

    private const int MaxLeavesPerHub = 14;

    private readonly Canvas _canvas = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _pan = new(0, 0);

    private readonly List<Placed> _nodes = new();
    private readonly List<Edge> _edges = new();

    private Point _centre;
    private Point _dragFrom;
    private bool _panning;
    private bool _movedFar;
    private Placed? _nodeDrag;

    public event Action<MapNode>? NodeClicked;

    /// <summary>A node on the canvas, with where it sits and what it is joined to.</summary>
    private sealed class Placed
    {
        public required Border Box { get; init; }
        public required MapNode Node { get; init; }
        public Point Local;

        /// <summary>Set once the user drags it, so relayout leaves it alone.</summary>
        public bool Pinned;
    }

    private sealed class Edge
    {
        public required Line Line { get; init; }
        public required Placed From { get; init; }
        public required Placed To { get; init; }
    }

    public AssetMapView()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x13));
        CornerRadius = new CornerRadius(5);
        ClipToBounds = true;

        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_pan);
        _canvas.RenderTransform = group;
        _canvas.Background = Brushes.Transparent;
        Child = _canvas;

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseLeave += OnMouseUp;
        MouseWheel += OnWheel;
        SizeChanged += (_, _) => Reposition();
    }

    // ------------------------------------------------------------------ build
    /// <summary>
    /// Lays out one model with its groups. Each group becomes a hub, and the group's
    /// items fan out beyond it.
    /// </summary>
    public void Build(MapNode centre,
        IReadOnlyList<(MapNode hub, IReadOnlyList<MapNode> items)> groups)
    {
        _canvas.Children.Clear();
        _nodes.Clear();
        _edges.Clear();
        _scale.ScaleX = _scale.ScaleY = 1;
        _pan.X = _pan.Y = 0;

        var centreNode = CreateNode(centre);
        centreNode.Local = new Point(0, 0);

        int hubCount = Math.Max(1, groups.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            // Hubs are spread over a full circle, starting to the left so the first
            // group reads as "what it is made of" rather than trailing off the top.
            double angle = Math.PI + i * (2 * Math.PI / hubCount);
            var (hub, items) = groups[i];

            int shown = Math.Min(items.Count, MaxLeavesPerHub);
            bool hasMore = items.Count > shown;
            int leafCount = shown + (hasMore ? 1 : 0);

            // Build the leaves first: their measured height decides how much arc the
            // group needs, and therefore how far out the hub has to sit.
            var leaves = new List<Placed>(leafCount);
            for (int k = 0; k < shown; k++)
                leaves.Add(CreateNode(items[k]));
            if (hasMore)
                leaves.Add(CreateNode(new MapNode
                {
                    Kind = MapNodeKind.More,
                    Title = $"+{items.Count - shown}",
                    Subtitle = null,
                }));

            double needed = leaves.Sum(l => l.Box.DesiredSize.Height + NodeGap);
            double hubDistance = HubDistance;
            double leafDistance = LeafDistance;

            // Tangential room at a radius is radius times angle. If the leaves do not
            // fit in the widest arc allowed, move them outwards until they do.
            if (leafCount > 1)
            {
                double minRadius = needed / MaxGroupSpread;
                if (leafDistance < minRadius)
                    leafDistance = minRadius;
            }

            var hubPlaced = CreateNode(hub);
            hubPlaced.Local = new Point(Math.Cos(angle) * hubDistance,
                Math.Sin(angle) * hubDistance);
            AddEdge(centreNode, hubPlaced, bright: true);

            if (leafCount > 0)
            {
                double spread = leafCount == 1 ? 0 : Math.Min(MaxGroupSpread, needed / leafDistance);
                double start = angle - spread / 2;
                double step = leafCount <= 1 ? 0 : spread / (leafCount - 1);

                for (int k = 0; k < leafCount; k++)
                {
                    double leafAngle = start + k * step;
                    leaves[k].Local = new Point(
                        hubPlaced.Local.X + Math.Cos(leafAngle) * leafDistance,
                        hubPlaced.Local.Y + Math.Sin(leafAngle) * leafDistance);
                    AddEdge(hubPlaced, leaves[k], bright: false);
                }
            }
        }

        // Edges go in behind the boxes: added to the canvas first, drawn first.
        foreach (var edge in _edges)
            _canvas.Children.Add(edge.Line);
        foreach (var placed in _nodes)
            _canvas.Children.Add(placed.Box);

        Reposition();
    }

    public void Clear()
    {
        _canvas.Children.Clear();
        _nodes.Clear();
        _edges.Clear();
    }

    /// <summary>Puts every node back where the layout wants it.</summary>
    public void ResetView()
    {
        _scale.ScaleX = _scale.ScaleY = 1;
        _pan.X = _pan.Y = 0;
        foreach (var placed in _nodes)
            placed.Pinned = false;
        Reposition();
    }

    // ----------------------------------------------------------------- layout
    private void Reposition()
    {
        _centre = new Point(ActualWidth / 2, ActualHeight / 2);

        foreach (var placed in _nodes)
        {
            var size = placed.Box.DesiredSize;
            Canvas.SetLeft(placed.Box, _centre.X + placed.Local.X - size.Width / 2);
            Canvas.SetTop(placed.Box, _centre.Y + placed.Local.Y - size.Height / 2);
        }

        foreach (var edge in _edges)
        {
            edge.Line.X1 = _centre.X + edge.From.Local.X;
            edge.Line.Y1 = _centre.Y + edge.From.Local.Y;
            edge.Line.X2 = _centre.X + edge.To.Local.X;
            edge.Line.Y2 = _centre.Y + edge.To.Local.Y;
        }
    }

    private void AddEdge(Placed from, Placed to, bool bright)
    {
        _edges.Add(new Edge
        {
            Line = new Line
            {
                Stroke = new SolidColorBrush(bright
                    ? Color.FromRgb(0x3E, 0x7A, 0x66)
                    : Color.FromRgb(0x2C, 0x30, 0x35)),
                StrokeThickness = bright ? 1.6 : 1.1,
                IsHitTestVisible = false,
            },
            From = from,
            To = to,
        });
    }

    /// <summary>
    /// Builds a node's box and measures it. Measuring here rather than after
    /// positioning is the whole point: the layout needs the real size.
    /// </summary>
    private Placed CreateNode(MapNode node)
    {
        var (fill, stroke) = Palette(node.Kind);
        bool big = node.Kind == MapNodeKind.Model;

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(new TextBlock
        {
            Text = node.Title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDF, 0xE4)),
            FontSize = big ? 14 : 11.5,
            FontWeight = node.Kind is MapNodeKind.Model or MapNodeKind.Hub
                ? FontWeights.SemiBold
                : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = big ? 260 : 190,
        });
        if (!string.IsNullOrEmpty(node.Subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = node.Subtitle,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x92, 0x9B)),
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = big ? 260 : 190,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        var box = new Border
        {
            Background = new SolidColorBrush(fill),
            BorderBrush = new SolidColorBrush(stroke),
            BorderThickness = new Thickness(big ? 2 : 1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(big ? 16 : 10, big ? 11 : 7, big ? 16 : 10, big ? 11 : 7),
            Child = stack,
            Cursor = Cursors.Hand,
            ToolTip = node.Subtitle is null
                ? node.Title
                : $"{node.Title}\n{node.Subtitle}",
        };

        var placed = new Placed { Box = box, Node = node };

        box.MouseLeftButtonDown += (_, e) =>
        {
            // Grab this node instead of panning the whole map.
            _nodeDrag = placed;
            _dragFrom = e.GetPosition(this);
            _movedFar = false;
            _panning = false;
            CaptureMouse();
            e.Handled = true;
        };

        box.MouseLeftButtonUp += (_, e) =>
        {
            // A drag that ended on a node is not a click on it.
            if (!_movedFar && node.Payload is not null)
            {
                NodeClicked?.Invoke(node);
                e.Handled = true;
            }
        };

        box.MouseEnter += (_, _) => box.BorderBrush = new SolidColorBrush(
            Color.FromRgb(0x5F, 0xD3, 0xA0));
        box.MouseLeave += (_, _) => box.BorderBrush = new SolidColorBrush(stroke);

        box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _nodes.Add(placed);
        return placed;
    }

    private static (Color fill, Color stroke) Palette(MapNodeKind kind) => kind switch
    {
        MapNodeKind.Model => (Color.FromRgb(0x22, 0x3A, 0x32), Color.FromRgb(0x5F, 0xD3, 0xA0)),
        MapNodeKind.Hub => (Color.FromRgb(0x24, 0x28, 0x2C), Color.FromRgb(0x44, 0x5A, 0x52)),
        MapNodeKind.Material => (Color.FromRgb(0x1F, 0x21, 0x24), Color.FromRgb(0x3A, 0x44, 0x52)),
        MapNodeKind.Texture => (Color.FromRgb(0x1F, 0x21, 0x24), Color.FromRgb(0x4A, 0x40, 0x30)),
        MapNodeKind.Animation => (Color.FromRgb(0x1F, 0x21, 0x24), Color.FromRgb(0x3A, 0x36, 0x50)),
        _ => (Color.FromRgb(0x1B, 0x1C, 0x1E), Color.FromRgb(0x33, 0x37, 0x3C)),
    };

    // ------------------------------------------------------------- navigation
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Reached only when the press missed every node, so this pans the map.
        _nodeDrag = null;
        _panning = true;
        _movedFar = false;
        _dragFrom = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !IsMouseCaptured)
            return;

        var now = e.GetPosition(this);
        double dx = now.X - _dragFrom.X, dy = now.Y - _dragFrom.Y;
        if (!_movedFar && Math.Abs(dx) + Math.Abs(dy) < 4)
            return;
        _movedFar = true;
        _dragFrom = now;

        if (_nodeDrag is not null)
        {
            // Screen pixels divided by zoom, or the node would run away from the
            // pointer once the map is scaled.
            double scale = _scale.ScaleX <= 0 ? 1 : _scale.ScaleX;
            _nodeDrag.Local = new Point(_nodeDrag.Local.X + dx / scale,
                _nodeDrag.Local.Y + dy / scale);
            _nodeDrag.Pinned = true;
            Reposition();
        }
        else if (_panning)
        {
            _pan.X += dx;
            _pan.Y += dy;
        }
    }

    private void OnMouseUp(object sender, MouseEventArgs e)
    {
        ReleaseMouseCapture();
        _panning = false;
        _nodeDrag = null;
        // Cleared on the next idle turn so the node's click handler still sees it.
        Dispatcher.BeginInvoke(new Action(() => _movedFar = false));
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        double next = Math.Clamp(_scale.ScaleX * factor, 0.2, 3.0);
        factor = next / _scale.ScaleX;

        // Zoom about the cursor so the thing under the pointer stays put.
        var at = e.GetPosition(this);
        _pan.X = at.X - factor * (at.X - _pan.X);
        _pan.Y = at.Y - factor * (at.Y - _pan.Y);
        _scale.ScaleX = _scale.ScaleY = next;
    }
}
