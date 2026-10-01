using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Compress.Controls;

public enum RegionHandle { Move, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// Selectable rectangle on a Canvas with a move area and resize handles. Drags are reported as the total offset
/// from where the drag began, so the owner can apply clamping and snapping without the box drifting from the mouse.
/// </summary>
public sealed class RegionBox : Grid
{
    const double LabelHeight = 19;

    readonly Border _frame;
    readonly Border _chip;
    readonly TextBlock _label;
    readonly List<Thumb> _handles = [];
    readonly SolidColorBrush _fill;
    readonly Brush _color;
    static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    Point _dragOrigin;
    readonly bool _pinnedHandles;
    bool _selected, _hover, _warning;

    public event Action<RegionBox, RegionHandle>? DragStarted;
    /// <summary>Handle and total mouse offset (in canvas pixels) since the drag started.</summary>
    public event Action<RegionBox, RegionHandle, Vector>? Dragging;
    public event Action<RegionBox>? DragCompleted;

    /// <summary>The model object this box edits.</summary>
    public object? Item { get; }

    /// <summary>Only show the outline while hovered or selected (used on the preview, where the picture matters).</summary>
    public bool Subtle { get; }

    /// <param name="pinnedHandles">Show the resize handles even when not selected (the gameplay crop is always editable).</param>
    public RegionBox(object? item, Color color, string label, bool edgeHandles, bool cornerHandles = true, bool subtle = false, bool pinnedHandles = false)
    {
        Item = item;
        _pinnedHandles = pinnedHandles;
        Subtle = subtle;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _color = brush;
        _fill = new SolidColorBrush(Color.FromArgb(subtle ? (byte)0 : (byte)0x1F, color.R, color.G, color.B));

        _frame = new Border { BorderBrush = brush, BorderThickness = new Thickness(1.5), Background = _fill, IsHitTestVisible = false };
        Children.Add(_frame);

        var move = new Thumb { Style = (Style)Application.Current.FindResource("RegionMoveThumb") };
        Hook(move, RegionHandle.Move);
        Children.Add(move);

        _label = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A)) };
        _chip = new Border
        {
            Background = brush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 2),
            Child = _label,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        Children.Add(_chip);

        if (cornerHandles)
        {
            AddHandle(RegionHandle.TopLeft, HorizontalAlignment.Left, VerticalAlignment.Top, Cursors.SizeNWSE);
            AddHandle(RegionHandle.TopRight, HorizontalAlignment.Right, VerticalAlignment.Top, Cursors.SizeNESW);
            AddHandle(RegionHandle.BottomLeft, HorizontalAlignment.Left, VerticalAlignment.Bottom, Cursors.SizeNESW);
            AddHandle(RegionHandle.BottomRight, HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE);
        }
        if (edgeHandles)
        {
            AddHandle(RegionHandle.Left, HorizontalAlignment.Left, VerticalAlignment.Center, Cursors.SizeWE);
            AddHandle(RegionHandle.Right, HorizontalAlignment.Right, VerticalAlignment.Center, Cursors.SizeWE);
            AddHandle(RegionHandle.Top, HorizontalAlignment.Center, VerticalAlignment.Top, Cursors.SizeNS);
            AddHandle(RegionHandle.Bottom, HorizontalAlignment.Center, VerticalAlignment.Bottom, Cursors.SizeNS);
        }

        MouseEnter += (_, _) => { _hover = true; UpdateLook(); };
        MouseLeave += (_, _) => { _hover = false; UpdateLook(); };
        UpdateLook();
    }

    public string Label
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            _selected = value;
            UpdateLook();
        }
    }

    /// <summary>Marks the region as covered (e.g. by app buttons): red outline that stays visible.</summary>
    public bool IsWarning
    {
        get => _warning;
        set
        {
            if (_warning == value) return;
            _warning = value;
            UpdateLook();
        }
    }

    /// <summary>Hidden boxes stay in place but cannot be seen or grabbed.</summary>
    public bool IsShown
    {
        get => Visibility == Visibility.Visible;
        set => Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <param name="containerWidth">Width of the canvas, so the name tag can flip inside near the right edge.</param>
    public void Place(Rect r, double containerWidth = double.PositiveInfinity)
    {
        Canvas.SetLeft(this, r.X);
        Canvas.SetTop(this, r.Y);
        Width = Math.Max(1, r.Width);
        Height = Math.Max(1, r.Height);
        // Keep the name tag readable: above the box, or inside it when the box touches the top edge;
        // right-aligned when it would run off the right edge.
        _chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool alignRight = r.X + _chip.DesiredSize.Width > containerWidth;
        bool inside = r.Y < LabelHeight + 2;
        _chip.HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        double side = inside ? 3 : -1.5, top = inside ? 3 : -LabelHeight - 1;
        _chip.Margin = alignRight ? new Thickness(0, top, side, 0) : new Thickness(side, top, 0, 0);
    }

    void UpdateLook()
    {
        bool active = _selected || _hover;
        _frame.BorderThickness = new Thickness(_selected ? 2 : 1.5);
        _frame.BorderBrush = _warning ? WarningBrush : _color;
        _frame.Opacity = Subtle && !active && !_warning ? 0 : 1;
        _chip.Visibility = !Subtle || active ? Visibility.Visible : Visibility.Collapsed;
        foreach (var h in _handles) h.Visibility = _selected || _pinnedHandles ? Visibility.Visible : Visibility.Collapsed;
        Panel.SetZIndex(this, _selected ? 10 : 0);
    }

    void AddHandle(RegionHandle handle, HorizontalAlignment h, VerticalAlignment v, Cursor cursor)
    {
        const double half = 5.5;
        var thumb = new Thumb
        {
            Style = (Style)Application.Current.FindResource("RegionHandleThumb"),
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Cursor = cursor,
            Margin = new Thickness(
                h == HorizontalAlignment.Left ? -half : 0, v == VerticalAlignment.Top ? -half : 0,
                h == HorizontalAlignment.Right ? -half : 0, v == VerticalAlignment.Bottom ? -half : 0),
        };
        Hook(thumb, handle);
        _handles.Add(thumb);
        Children.Add(thumb);
    }

    void Hook(Thumb thumb, RegionHandle handle)
    {
        thumb.DragStarted += (_, _) =>
        {
            _dragOrigin = Mouse.GetPosition(Parent as IInputElement);
            DragStarted?.Invoke(this, handle);
        };
        thumb.DragDelta += (_, _) => Dragging?.Invoke(this, handle, Mouse.GetPosition(Parent as IInputElement) - _dragOrigin);
        thumb.DragCompleted += (_, _) => DragCompleted?.Invoke(this);
    }
}
