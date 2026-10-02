using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Compress.Core;

namespace Compress.Controls;

/// <summary>
/// Draws one caption line over the 9:16 preview the way the export burns it in: heavy white capitals with a black outline
/// and shadow, the word being spoken in lime, a short pop when a new line appears.
/// </summary>
public sealed class CaptionView : FrameworkElement
{
    static readonly Brush White = Brushes.White;
    static readonly Brush Lime = new SolidColorBrush(Color.FromRgb(0x84, 0xCC, 0x16));
    static readonly Brush ShadowBrush = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
    static readonly Typeface Face = new(new FontFamily(Subtitles.FontName), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);

    SubLine? _line;
    int _word = -1;
    SubtitlePosition _position = SubtitlePosition.Bottom;
    readonly ScaleTransform _pop = new(1, 1);

    public CaptionView()
    {
        RenderTransform = _pop;
    }

    /// <summary>Shows <paramref name="line"/> with word <paramref name="word"/> highlighted; null hides the caption.</summary>
    public void Show(SubLine? line, int word, SubtitlePosition position)
    {
        if (line == _line && word == _word && position == _position) return;
        bool newLine = line is not null && line != _line;
        _line = line;
        _word = word;
        _position = position;
        InvalidateVisual();

        if (newLine)
        {
            _pop.CenterX = ActualWidth / 2;
            _pop.CenterY = Subtitles.CenterY(position) * ActualWidth / ShortsLayout.Width;
            var anim = new DoubleAnimation(1.12, 1, TimeSpan.FromMilliseconds(90));
            _pop.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            _pop.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_line is null || ActualWidth <= 0) return;
        double k = ActualWidth / ShortsLayout.Width;

        var words = _line.Words.Select(w => Subtitles.Display(w.Text)).ToList();
        var text = string.Join(" ", words);
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, Subtitles.FontSize * k, White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = (ShortsLayout.Width - 140) * k,
            TextAlignment = TextAlignment.Center,
        };
        int at = 0;
        for (int i = 0; i < words.Count; i++)
        {
            if (i == _word) ft.SetForegroundBrush(Lime, at, words[i].Length);
            at += words[i].Length + 1;
        }

        var origin = new Point(70 * k, Subtitles.CenterY(_position) * k - ft.Height / 2);
        var geometry = ft.BuildGeometry(origin);
        dc.PushTransform(new TranslateTransform(0, 4 * k));
        dc.DrawGeometry(ShadowBrush, new Pen(ShadowBrush, 14 * k) { LineJoin = PenLineJoin.Round }, geometry);
        dc.Pop();
        dc.DrawGeometry(null, new Pen(Brushes.Black, 14 * k) { LineJoin = PenLineJoin.Round }, geometry);
        dc.DrawText(ft, origin);
    }
}
