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
/// and the direction groups the kinds. Counts are shown on hubs so a model with 338
/// clips stays readable instead of drawing 338 boxes.
/// </remarks>
public sealed class AssetMapView : Border
{
    private const double HubRadius = 210;
    private const double LeafRadius = 165;
    private const int MaxLeavesPerHub = 14;

    private readonly Canvas _canvas = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _pan = new(0, 0);

    private Point _dragFrom;
    private bool _dragging;

    public event Action<MapNode>? NodeClicked;

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

        MouseLeftButtonDown += OnPanStart;
        MouseMove += OnPanMove;
        MouseLeftButtonUp += OnPanEnd;
        MouseLeave += OnPanEnd;
        MouseWheel += OnWheel;
        SizeChanged += (_, _) => Recentre();
    }

    private Point _centre;

    /// <summary>
    /// Lays out one model with its groups. Each group becomes a hub, and the group's
    /// items fan out beyond it.
    /// </summary>
    public void Build(MapNode centre, IReadOnlyList<(MapNode hub, IReadOnlyList<MapNode> items)> groups)
    {
        _canvas.Children.Clear();
        _scale.ScaleX = _scale.ScaleY = 1;
        _pan.X = _pan.Y = 0;

        // Hubs are spread over a full circle, starting to the left so that the first
        // group reads as "what it is made of" rather than trailing off the top.
        int hubCount = Math.Max(1, groups.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            double angle = Math.PI + i * (2 * Math.PI / hubCount);
            var hubPoint = new Point(Math.Cos(angle) * HubRadius, Math.Sin(angle) * HubRadius);

            AddEdge(new Point(0, 0), hubPoint, bright: true);

            var (hub, items) = groups[i];
            int shown = Math.Min(items.Count, MaxLeavesPerHub);
            if (shown > 0)
            {
                // Fan the leaves around the hub's own direction so they never fold
                // back over the centre node.
                double spread = shown == 1 ? 0 : 1.25;
                double start = angle - spread / 2;
                double step = shown <= 1 ? 0 : spread / (shown - 1);

                for (int k = 0; k < shown; k++)
                {
                    double leafAngle = start + k * step;
                    var leafPoint = new Point(
                        hubPoint.X + Math.Cos(leafAngle) * LeafRadius,
                        hubPoint.Y + Math.Sin(leafAngle) * LeafRadius);
                    AddEdge(hubPoint, leafPoint, bright: false);
                    AddNode(items[k], leafPoint);
                }

                if (items.Count > shown)
                {
                    double leafAngle = start + spread / 2 + 0.32;
                    var morePoint = new Point(
                        hubPoint.X + Math.Cos(leafAngle) * LeafRadius,
                        hubPoint.Y + Math.Sin(leafAngle) * LeafRadius);
                    AddEdge(hubPoint, morePoint, bright: false);
                    AddNode(new MapNode
                    {
                        Kind = MapNodeKind.More,
                        Title = $"+{items.Count - shown}",
                        Subtitle = null,
                    }, morePoint);
                }
            }

            AddNode(hub, hubPoint);
        }

        AddNode(centre, new Point(0, 0));
        Recentre();
    }

    public void Clear() => _canvas.Children.Clear();

    private void Recentre()
    {
        _centre = new Point(ActualWidth / 2, ActualHeight / 2);
        foreach (UIElement child in _canvas.Children)
        {
            if (child is FrameworkElement fe && fe.Tag is Point local)
            {
                Canvas.SetLeft(fe, _centre.X + local.X - fe.DesiredSize.Width / 2);
                Canvas.SetTop(fe, _centre.Y + local.Y - fe.DesiredSize.Height / 2);
            }
            else if (child is Line line && line.Tag is (Point a, Point b))
            {
                line.X1 = _centre.X + a.X;
                line.Y1 = _centre.Y + a.Y;
                line.X2 = _centre.X + b.X;
                line.Y2 = _centre.Y + b.Y;
            }
        }
    }

    private void AddEdge(Point from, Point to, bool bright)
    {
        var line = new Line
        {
            Stroke = new SolidColorBrush(bright
                ? Color.FromRgb(0x3E, 0x7A, 0x66)
                : Color.FromRgb(0x2C, 0x30, 0x35)),
            StrokeThickness = bright ? 1.6 : 1.1,
            Tag = (from, to),
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(line);
    }

    private void AddNode(MapNode node, Point local)
    {
        var (fill, stroke) = Palette(node.Kind);

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(new TextBlock
        {
            Text = node.Title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDF, 0xE4)),
            FontSize = node.Kind == MapNodeKind.Model ? 14 : 11.5,
            FontWeight = node.Kind is MapNodeKind.Model or MapNodeKind.Hub
                ? FontWeights.SemiBold
                : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = node.Kind == MapNodeKind.Model ? 260 : 190,
        });
        if (!string.IsNullOrEmpty(node.Subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = node.Subtitle,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x92, 0x9B)),
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = node.Kind == MapNodeKind.Model ? 260 : 190,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        var box = new Border
        {
            Background = new SolidColorBrush(fill),
            BorderBrush = new SolidColorBrush(stroke),
            BorderThickness = new Thickness(node.Kind == MapNodeKind.Model ? 2 : 1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(node.Kind == MapNodeKind.Model ? 16 : 10,
                node.Kind == MapNodeKind.Model ? 11 : 7,
                node.Kind == MapNodeKind.Model ? 16 : 10,
                node.Kind == MapNodeKind.Model ? 11 : 7),
            Child = stack,
            Tag = local,
            Cursor = node.Payload is null ? Cursors.Arrow : Cursors.Hand,
        };
        box.ToolTip = node.Subtitle is null ? node.Title : $"{node.Title}\n{node.Subtitle}";

        box.MouseLeftButtonUp += (_, e) =>
        {
            // A click that ended a pan should not count as picking a node.
            if (!_dragging && node.Payload is not null)
            {
                NodeClicked?.Invoke(node);
                e.Handled = true;
            }
        };
        box.MouseEnter += (_, _) => box.BorderBrush = new SolidColorBrush(
            Color.FromRgb(0x5F, 0xD3, 0xA0));
        box.MouseLeave += (_, _) => box.BorderBrush = new SolidColorBrush(stroke);

        _canvas.Children.Add(box);
        box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
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
    private void OnPanStart(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(this);
        _dragging = false;
        CaptureMouse();
    }

    private void OnPanMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !IsMouseCaptured)
            return;
        var now = e.GetPosition(this);
        double dx = now.X - _dragFrom.X, dy = now.Y - _dragFrom.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < 4)
            return;
        _dragging = true;
        _pan.X += dx;
        _pan.Y += dy;
        _dragFrom = now;
    }

    private void OnPanEnd(object sender, MouseEventArgs e)
    {
        ReleaseMouseCapture();
        // Cleared on the next input so the node click handler can still see it.
        Dispatcher.BeginInvoke(new Action(() => _dragging = false));
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        double next = Math.Clamp(_scale.ScaleX * factor, 0.25, 3.0);
        factor = next / _scale.ScaleX;

        // Zoom about the cursor so the thing under the pointer stays put.
        var at = e.GetPosition(this);
        _pan.X = at.X - factor * (at.X - _pan.X);
        _pan.Y = at.Y - factor * (at.Y - _pan.Y);
        _scale.ScaleX = _scale.ScaleY = next;
    }

    public void ResetView()
    {
        _scale.ScaleX = _scale.ScaleY = 1;
        _pan.X = _pan.Y = 0;
    }
}
