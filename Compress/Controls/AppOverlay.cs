using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Compress.Core;
using Path = System.Windows.Shapes.Path;

namespace Compress.Controls;

/// <summary>
/// Draws a look-alike of the TikTok / Reels / Shorts interface over the 9:16 preview, so it is obvious what the
/// buttons and caption will cover. Preview only; never exported. Icons are Material Design paths (Apache 2.0),
/// not the apps' own artwork.
/// </summary>
static class AppOverlay
{
    // 24×24 icon outlines.
    const string Heart = "M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z";
    const string Comment = "M21.99 4c0-1.1-.89-2-1.99-2H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h14l4 4-.01-18z";
    const string Bookmark = "M17 3H7c-1.1 0-1.99.9-1.99 2L5 21l7-3 7 3V5c0-1.1-.9-2-2-2z";
    const string Share = "M14 9V5l7 7-7 7v-4.1c-5 0-8.5 1.6-11 5.1 1-5 4-10 11-11z";
    const string Send = "M2.01 21L23 12 2.01 3 2 10l15 2-15 2z";
    const string ThumbUp = "M1 21h4V9H1v12zm22-11c0-1.1-.9-2-2-2h-6.31l.95-4.57.03-.32c0-.41-.17-.79-.44-1.06L14.17 1 7.59 7.59C7.22 7.95 7 8.45 7 9v10c0 1.1.9 2 2 2h9c.83 0 1.54-.5 1.84-1.22l3.02-7.05c.09-.23.14-.47.14-.73v-2z";
    const string Remix = "M17.65 6.35A7.958 7.958 0 0 0 12 4c-4.42 0-7.99 3.58-7.99 8s3.57 8 7.99 8c3.73 0 6.84-2.55 7.73-6h-2.08A5.99 5.99 0 0 1 12 18c-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z";
    const string Music = "M12 3v10.55c-.59-.34-1.27-.55-2-.55-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4V7h4V3h-6z";
    const string Search = "M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z";
    const string Camera = "M9 2L7.17 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2h-3.17L15 2H9zm3 15c-2.76 0-5-2.24-5-5s2.24-5 5-5 5 2.24 5 5-2.24 5-5 5z";
    const string Home = "M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z";
    const string People = "M16 11c1.66 0 2.99-1.34 2.99-3S17.66 5 16 5c-1.66 0-3 1.34-3 3s1.34 3 3 3zm-8 0c1.66 0 2.99-1.34 2.99-3S9.66 5 8 5C6.34 5 5 6.34 5 8s1.34 3 3 3zm0 2c-2.33 0-7 1.17-7 3.5V19h14v-2.5c0-2.33-4.67-3.5-7-3.5zm8 0c-.29 0-.62.02-.97.05 1.16.84 1.97 1.97 1.97 3.45V19h6v-2.5c0-2.33-4.67-3.5-7-3.5z";
    const string Inbox = "M20 2H4c-1.1 0-2 .9-2 2v18l4-4h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2z";
    const string Person = "M12 12c2.21 0 4-1.79 4-4s-1.79-4-4-4-4 1.79-4 4 1.79 4 4 4zm0 2c-2.67 0-8 1.34-8 4v2h16v-2c0-2.66-5.33-4-8-4z";
    const string Play = "M10 16.5l6-4.5-6-4.5v9zM12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z";
    const string Subscriptions = "M20 8H4V6h16v2zm-2-6H6v2h12V2zm4 10v8c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2v-8c0-1.1.9-2 2-2h16c1.1 0 2 .9 2 2zm-6 4l-6-3.27v6.53L16 16z";
    const string Dots = "M12 8c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm0 2c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm0 6c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2z";

    static readonly Brush White = Frozen(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    static readonly Brush Dim = Frozen(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
    static readonly Brush NavBg = Frozen(Color.FromArgb(0xE6, 0x05, 0x05, 0x05));
    static readonly Brush Red = Frozen(Color.FromRgb(0xFE, 0x2C, 0x55));
    static readonly Brush WarnFill = Frozen(Color.FromArgb(0x33, 0xF8, 0x71, 0x71));
    static readonly Brush WarnStroke = Frozen(Color.FromArgb(0xCC, 0xF8, 0x71, 0x71));

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <param name="k">Screen pixels per output pixel.</param>
    /// <param name="covered">Zones that hide part of a HUD element; they are tinted red.</param>
    public static void Draw(Canvas canvas, AppUi ui, double k, IEnumerable<UiZone> covered)
    {
        canvas.Children.Clear();
        var d = new Drawer(canvas, k);

        foreach (var zone in covered.Distinct())
            d.Fill(zone.Bounds, WarnFill, WarnStroke);

        switch (ui.Platform)
        {
            case ShortsPlatform.Reels: DrawReels(d); break;
            case ShortsPlatform.Shorts: DrawShorts(d); break;
            default: DrawTikTok(d); break;
        }
    }

    static void DrawTikTok(Drawer d)
    {
        d.Text("LIVE", 40, 95, 30, bold: true, brush: Dim);
        d.Text("Following", 340, 95, 34, brush: Dim);
        d.Text("For You", 560, 95, 36, bold: true);
        d.Fill(new Rect(590, 128, 70, 5), White);
        d.Icon(Search, 1010, 95, 56);

        const double x = 990;
        d.Avatar(x, 900, 46, plus: true);
        d.Icon(Heart, x, 1060, 84);
        d.Text("128K", x, 1120, 28, bold: true, center: true);
        d.Icon(Comment, x, 1215, 72);
        d.Text("1,024", x, 1272, 28, bold: true, center: true);
        d.Icon(Bookmark, x, 1360, 72);
        d.Text("8,562", x, 1417, 28, bold: true, center: true);
        d.Icon(Share, x, 1500, 76);
        d.Text("2,310", x, 1557, 28, bold: true, center: true);
        d.Disc(x, 1680, 44);

        d.Text("@yourname", 40, 1590, 38, bold: true);
        d.Text("Your caption #gaming #fyp", 40, 1648, 32);
        d.Icon(Music, 58, 1708, 34);
        d.Text("original sound - yourname", 86, 1708, 30);

        d.Nav([(Home, "Home"), (People, "Friends"), (null, ""), (Inbox, "Inbox"), (Person, "Profile")]);
        d.PlusButton(540, 1845);
    }

    static void DrawReels(Drawer d)
    {
        d.Text("Reels", 40, 95, 48, bold: true);
        d.Icon(Camera, 1010, 95, 58);

        const double x = 1000;
        d.Icon(Heart, x, 1130, 76);
        d.Text("45.2K", x, 1186, 27, bold: true, center: true);
        d.Icon(Comment, x, 1270, 68);
        d.Text("312", x, 1324, 27, bold: true, center: true);
        d.Icon(Send, x, 1410, 66);
        d.Text("1,204", x, 1464, 27, bold: true, center: true);
        d.Icon(Dots, x, 1550, 60);
        d.Thumb(x, 1650, 58);

        d.Avatar(70, 1560, 30, plus: false);
        d.Text("yourname", 115, 1560, 32, bold: true);
        d.Pill("Follow", 300, 1560, 30, filled: false);
        d.Text("Your caption #gaming", 40, 1625, 30);
        d.Icon(Music, 58, 1680, 30);
        d.Text("yourname · Original audio", 84, 1680, 28);

        d.Nav([(Home, ""), (Search, ""), (null, ""), (Play, ""), (Person, "")]);
        d.PlusButton(540, 1855, outline: true);
    }

    static void DrawShorts(Drawer d)
    {
        d.Icon(Search, 900, 95, 58);
        d.Icon(Dots, 1020, 95, 58);

        const double x = 1000;
        d.Icon(ThumbUp, x, 980, 70);
        d.Text("52K", x, 1036, 27, bold: true, center: true);
        d.Icon(ThumbUp, x, 1120, 70, flip: true);
        d.Text("Dislike", x, 1176, 25, bold: true, center: true);
        d.Icon(Comment, x, 1260, 68);
        d.Text("418", x, 1316, 27, bold: true, center: true);
        d.Icon(Share, x, 1400, 70);
        d.Text("Share", x, 1456, 25, bold: true, center: true);
        d.Icon(Remix, x, 1540, 66);
        d.Text("Remix", x, 1596, 25, bold: true, center: true);
        d.Thumb(x, 1680, 58);

        d.Avatar(70, 1560, 30, plus: false);
        d.Text("@yourname", 115, 1560, 32, bold: true);
        d.Pill("Subscribe", 345, 1560, 30, filled: true);
        d.Text("Your caption #gaming #shorts", 40, 1625, 30);
        d.Icon(Music, 58, 1680, 30);
        d.Text("Original sound", 84, 1680, 28);

        d.Nav([(Home, "Home"), (Play, "Shorts"), (null, ""), (Subscriptions, "Subscriptions"), (Person, "You")]);
        d.PlusButton(540, 1845, outline: true);
    }

    /// <summary>Places shapes given in output pixels (1080×1920) onto the scaled preview canvas.</summary>
    sealed class Drawer(Canvas canvas, double k)
    {
        readonly DropShadowEffect _shadow = new() { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.6, Color = Colors.Black };

        void Add(UIElement e, double x, double y)
        {
            Canvas.SetLeft(e, x * k);
            Canvas.SetTop(e, y * k);
            canvas.Children.Add(e);
        }

        public void Fill(Rect r, Brush fill, Brush? stroke = null)
        {
            var rect = new Rectangle { Width = r.Width * k, Height = r.Height * k, Fill = fill, Stroke = stroke, StrokeThickness = 1, StrokeDashArray = stroke is null ? null : [4, 3] };
            Add(rect, r.X, r.Y);
        }

        /// <summary>Icon centered on (cx, cy).</summary>
        public void Icon(string data, double cx, double cy, double size, bool flip = false, Brush? brush = null)
        {
            var path = new Path
            {
                Data = Geometry.Parse(data),
                Fill = brush ?? White,
                Stretch = Stretch.Uniform,
                Width = size * k,
                Height = size * k,
                Effect = _shadow,
            };
            if (flip)
            {
                path.RenderTransformOrigin = new Point(0.5, 0.5);
                path.RenderTransform = new ScaleTransform(1, -1);
            }
            Add(path, cx - size / 2, cy - size / 2);
        }

        /// <summary>Text with its left edge at x (or centered on x) and vertically centered on y.</summary>
        public void Text(string text, double x, double y, double size, bool bold = false, bool center = false, Brush? brush = null)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = Math.Max(1, size * k),
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = brush ?? White,
                Effect = _shadow,
            };
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = block.DesiredSize.Width / k, h = block.DesiredSize.Height / k;
            Add(block, center ? x - w / 2 : x, y - h / 2);
        }

        public void Avatar(double cx, double cy, double r, bool plus)
        {
            var circle = new Ellipse { Width = 2 * r * k, Height = 2 * r * k, Fill = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3A)), Stroke = White, StrokeThickness = Math.Max(1, 3 * k) };
            Add(circle, cx - r, cy - r);
            Icon(Person, cx, cy, r * 1.3, brush: Dim);
            if (!plus) return;
            const double pr = 16;
            Add(new Ellipse { Width = 2 * pr * k, Height = 2 * pr * k, Fill = Red }, cx - pr, cy + r - pr);
            Add(new Rectangle { Width = 14 * k, Height = 3 * k, Fill = Brushes.White }, cx - 7, cy + r - 1.5);
            Add(new Rectangle { Width = 3 * k, Height = 14 * k, Fill = Brushes.White }, cx - 1.5, cy + r - 7);
        }

        /// <summary>Spinning record that shows the sound.</summary>
        public void Disc(double cx, double cy, double r)
        {
            Add(new Ellipse { Width = 2 * r * k, Height = 2 * r * k, Fill = Frozen(Color.FromRgb(0x22, 0x22, 0x22)), Stroke = Frozen(Color.FromRgb(0x44, 0x44, 0x44)), StrokeThickness = Math.Max(1, 8 * k) }, cx - r, cy - r);
            double inner = r * 0.45;
            Add(new Ellipse { Width = 2 * inner * k, Height = 2 * inner * k, Fill = Dim }, cx - inner, cy - inner);
        }

        /// <summary>Square sound thumbnail (Reels, Shorts).</summary>
        public void Thumb(double cx, double cy, double size)
        {
            Add(new Rectangle { Width = size * k, Height = size * k, RadiusX = 10 * k, RadiusY = 10 * k, Fill = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3A)), Stroke = White, StrokeThickness = Math.Max(1, 3 * k) }, cx - size / 2, cy - size / 2);
            Icon(Music, cx, cy, size * 0.55, brush: Dim);
        }

        public void Pill(string text, double x, double cy, double size, bool filled)
        {
            var block = new TextBlock { Text = text, FontSize = Math.Max(1, size * k), FontWeight = FontWeights.SemiBold, Foreground = filled ? Brushes.Black : White };
            var border = new Border
            {
                Child = block,
                CornerRadius = new CornerRadius(filled ? 40 * k : 10 * k),
                Background = filled ? White : Brushes.Transparent,
                BorderBrush = White,
                BorderThickness = new Thickness(Math.Max(1, 2 * k)),
                Padding = new Thickness(18 * k, 4 * k, 18 * k, 6 * k),
            };
            border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Add(border, x, cy - border.DesiredSize.Height / k / 2);
        }

        /// <summary>Bottom navigation bar with five items; a null icon leaves room for the create button.</summary>
        public void Nav((string? Icon, string Label)[] items)
        {
            Fill(new Rect(0, 1790, ShortsLayout.Width, 130), NavBg);
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].Icon is not { } icon) continue;
                double cx = ShortsLayout.Width * (i + 0.5) / items.Length;
                bool labeled = items[i].Label.Length > 0;
                Icon(icon, cx, labeled ? 1835 : 1850, 54, brush: i == 0 ? White : Dim);
                if (labeled) Text(items[i].Label, cx, 1885, 24, center: true, brush: i == 0 ? White : Dim);
            }
        }

        public void PlusButton(double cx, double cy, bool outline = false)
        {
            const double w = 100, h = 64;
            if (outline)
            {
                Add(new Rectangle { Width = 64 * k, Height = 64 * k, RadiusX = 16 * k, RadiusY = 16 * k, Stroke = White, StrokeThickness = Math.Max(1, 4 * k) }, cx - 32, cy - 32);
            }
            else
            {
                // TikTok's create button: white key with cyan and red edges.
                Add(new Rectangle { Width = w * k, Height = h * k, RadiusX = 16 * k, RadiusY = 16 * k, Fill = Frozen(Color.FromRgb(0x25, 0xF4, 0xEE)) }, cx - w / 2 - 6, cy - h / 2);
                Add(new Rectangle { Width = w * k, Height = h * k, RadiusX = 16 * k, RadiusY = 16 * k, Fill = Red }, cx - w / 2 + 6, cy - h / 2);
                Add(new Rectangle { Width = (w - 12) * k, Height = h * k, RadiusX = 16 * k, RadiusY = 16 * k, Fill = Brushes.White }, cx - w / 2 + 6, cy - h / 2);
            }
            var bar = outline ? White : Brushes.Black;
            Add(new Rectangle { Width = 30 * k, Height = 5 * k, Fill = bar }, cx - 15, cy - 2.5);
            Add(new Rectangle { Width = 5 * k, Height = 30 * k, Fill = bar }, cx - 2.5, cy - 15);
        }
    }
}
