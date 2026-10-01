using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Compress.Controls;

/// <summary>
/// Start screen of every page: a dashed drop card with an upload icon, a title, the select button and the
/// supported formats. The whole card is clickable and lights up under the mouse.
/// </summary>
public sealed class DropZone : Grid
{
    readonly Rectangle _dash;
    readonly Border _iconCircle;
    readonly TranslateTransform _iconLift = new();
    readonly TextBlock _title;
    readonly Button _button;

    /// <summary>Raised by a click on the card or its button: open the file picker.</summary>
    public event RoutedEventHandler? Pick;

    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    public string ButtonText
    {
        get => (string)_button.Content;
        set => _button.Content = value;
    }

    public DropZone()
    {
        Width = 520;
        Height = 330;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;

        _dash = new Rectangle
        {
            RadiusX = 22, RadiusY = 22, StrokeThickness = 1.5, StrokeDashArray = [6, 5],
            Fill = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)),
        };
        _dash.SetResourceReference(Shape.StrokeProperty, "Line2");
        Children.Add(_dash);

        var icon = new TextBlock { Text = "\uE898", FontSize = 30 };
        icon.SetResourceReference(StyleProperty, "Icon");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        _iconCircle = new Border
        {
            Width = 76, Height = 76, CornerRadius = new CornerRadius(38), Child = icon,
            HorizontalAlignment = HorizontalAlignment.Center, RenderTransform = _iconLift,
        };
        _iconCircle.SetResourceReference(Border.BackgroundProperty, "AccentSoft");

        _title = new TextBlock
        {
            FontWeight = FontWeights.Bold, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 22, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)),
        };
        _title.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");

        var or = new TextBlock { Text = "or", FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 14) };
        or.SetResourceReference(TextBlock.ForegroundProperty, "Text3");

        _button = new Button { Content = "Select Video", HorizontalAlignment = HorizontalAlignment.Center };
        _button.SetResourceReference(StyleProperty, "PillAccentButton");
        _button.Click += (s, e) => Pick?.Invoke(this, e);

        var formats = new TextBlock
        {
            Text = "MP4 · MOV · MKV · AVI · WebM · and more", FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0),
        };
        formats.SetResourceReference(TextBlock.ForegroundProperty, "Text3");

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(_iconCircle);
        content.Children.Add(_title);
        content.Children.Add(or);
        content.Children.Add(_button);
        content.Children.Add(formats);
        Children.Add(content);

        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(false);
        // A click anywhere on the card picks a file (the button handles its own click).
        MouseLeftButtonUp += (_, e) =>
        {
            if (!e.Handled) Pick?.Invoke(this, e);
        };
    }

    void SetHover(bool on)
    {
        _dash.SetResourceReference(Shape.StrokeProperty, on ? "Accent" : "Line2");
        _dash.Fill = new SolidColorBrush(on ? Color.FromRgb(0x12, 0x14, 0x0F) : Color.FromRgb(0x10, 0x10, 0x10));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _iconLift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(on ? -4 : 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }
}
