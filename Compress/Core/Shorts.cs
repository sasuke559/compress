using System.Windows;

namespace Compress.Core;

/// <summary>How much of the vertical video the gameplay fills.</summary>
public enum ShortsFraming { Full, Portrait, Square, Wide }

/// <summary>What fills the space around the gameplay when it does not cover the whole vertical video.</summary>
public enum ShortsBackground { Blur, Black }

/// <summary>A piece of the HUD (health, ammo, minimap…) cut out of the source and placed on the vertical video.</summary>
public sealed class HudElement
{
    public string Name { get; set; } = "Element";

    // Region in the source frame, as fractions of its width and height.
    public double SrcX { get; set; }
    public double SrcY { get; set; }
    public double SrcW { get; set; } = 0.2;
    public double SrcH { get; set; } = 0.1;

    // Top-left corner on the vertical video, as fractions of 1080 × 1920.
    public double OutX { get; set; }
    public double OutY { get; set; }
    /// <summary>Width on the vertical video as a fraction of 1080; the height follows the source aspect ratio. 0 = not placed yet.</summary>
    public double OutW { get; set; }

    public bool Visible { get; set; } = true;

    public HudElement Clone() => (HudElement)MemberwiseClone();
}

/// <summary>Everything that defines a vertical short: gameplay crop, framing, background and HUD elements.</summary>
public sealed class ShortsLayout
{
    public const int Width = 1080, Height = 1920;

    public ShortsFraming Framing { get; set; } = ShortsFraming.Full;
    public ShortsBackground Background { get; set; } = ShortsBackground.Blur;

    // Center of the gameplay crop in the source, as fractions.
    public double CropX { get; set; } = 0.5;
    public double CropY { get; set; } = 0.5;
    /// <summary>Crop height as a fraction of the source height (1 = full height, smaller = zoomed in).</summary>
    public double CropZoom { get; set; } = 1;

    /// <summary>Vertical center of the gameplay frame on the vertical video (only used when it does not fill it).</summary>
    public double FrameY { get; set; } = 0.5;

    public List<HudElement> Elements { get; set; } = [];

    public bool FillsFrame => Framing == ShortsFraming.Full;

    public ShortsLayout Clone()
    {
        var copy = (ShortsLayout)MemberwiseClone();
        copy.Elements = Elements.Select(e => e.Clone()).ToList();
        return copy;
    }

    public static Size FrameSize(ShortsFraming framing) => framing switch
    {
        ShortsFraming.Portrait => new Size(1080, 1350),
        ShortsFraming.Square => new Size(1080, 1080),
        ShortsFraming.Wide => new Size(1080, 608),
        _ => new Size(Width, Height),
    };

    double FrameAspect
    {
        get
        {
            var s = FrameSize(Framing);
            return s.Width / s.Height;
        }
    }

    /// <summary>Where the gameplay sits on the vertical video, in output pixels.</summary>
    public Rect FrameRect()
    {
        var s = FrameSize(Framing);
        double y = Math.Clamp(FrameY * Height - s.Height / 2, 0, Height - s.Height);
        return new Rect(0, Math.Round(y), s.Width, s.Height);
    }

    /// <summary>Largest crop height (fraction of the source height) that still fits the source at the frame's aspect ratio.</summary>
    public double MaxZoom(double sw, double sh) => Math.Min(1, sw / FrameAspect / sh);

    /// <summary>Gameplay crop in source pixels. Always has the frame's aspect ratio and stays inside the source.</summary>
    public Rect CropRect(double sw, double sh)
    {
        double h = Math.Clamp(CropZoom, 0.05, MaxZoom(sw, sh)) * sh, w = h * FrameAspect;
        double x = Math.Clamp(CropX * sw - w / 2, 0, Math.Max(0, sw - w));
        double y = Math.Clamp(CropY * sh - h / 2, 0, Math.Max(0, sh - h));
        return new Rect(x, y, w, h);
    }

    /// <summary>Writes the clamped crop back, so stored values always match what is shown and exported.</summary>
    public void NormalizeCrop(double sw, double sh)
    {
        var r = CropRect(sw, sh);
        CropX = (r.X + r.Width / 2) / sw;
        CropY = (r.Y + r.Height / 2) / sh;
        CropZoom = r.Height / sh;
    }

    public static Rect SourceRect(HudElement e, double sw, double sh) =>
        new(e.SrcX * sw, e.SrcY * sh, e.SrcW * sw, e.SrcH * sh);

    /// <summary>Width / height of an element in pixels, which its placement on the vertical video keeps.</summary>
    public static double SourceAspect(HudElement e, double sw, double sh) =>
        e.SrcW * sw / Math.Max(1e-6, e.SrcH * sh);

    /// <summary>Where an element lands on the vertical video, in output pixels.</summary>
    public static Rect OutputRect(HudElement e, double sw, double sh)
    {
        double w = e.OutW * Width;
        return new Rect(e.OutX * Width, e.OutY * Height, w, w / SourceAspect(e, sw, sh));
    }

    // ---------------------------------------------------------------- auto arrange

    /// <summary>
    /// Places every element outside the gameplay and away from the app interface: HUD from the top of the screen goes
    /// above the gameplay, HUD from the bottom goes below it, and each element keeps its side (left / center / right).
    /// </summary>
    public void AutoArrange(double sw, double sh, AppUi ui)
    {
        var top = Elements.Where(e => e.SrcY + e.SrcH / 2 < 0.5).ToList();
        var bottom = Elements.Except(top).ToList();
        double topSafe = ui.SafeTop * Height, bottomSafe = ui.SafeBottom * Height;

        // Make room where the HUD goes: gameplay moves up when all HUD sits below it, and down when all of it sits above.
        if (!FillsFrame)
        {
            double frameH = FrameSize(Framing).Height;
            double frameTop = (top.Count == 0) == (bottom.Count == 0)
                ? (Height - frameH) / 2
                : top.Count == 0
                    ? (frameH + topSafe < bottomSafe - MinBand * Height ? topSafe : 0)
                    : Math.Max(0, Math.Min(bottomSafe, Height) - frameH);
            FrameY = (frameTop + frameH / 2) / Height;
        }

        var frame = FrameRect();
        // Use the band next to the gameplay when it is big enough, otherwise overlap the edge of the gameplay.
        double minZone = 0.15 * Height;
        double topEnd = frame.Top - topSafe >= MinBand * Height ? frame.Top : Math.Max(frame.Top, topSafe + minZone);
        double bottomStart = bottomSafe - frame.Bottom >= MinBand * Height ? frame.Bottom : Math.Min(frame.Bottom, bottomSafe - minZone);

        // The button column reaches into the lower zone; keep HUD on its left there.
        double buttonsInset = (1 - ui.ButtonsLeft) * Width;
        ArrangeZone(top, topSafe, topEnd, fromTop: true, ui.ButtonsTop * Height < topEnd ? buttonsInset : 0, sw, sh);
        ArrangeZone(bottom, bottomStart, bottomSafe, fromTop: false, buttonsInset, sw, sh);
    }

    /// <summary>Smallest free band (fraction of the height) next to the gameplay that HUD is placed in instead of over the gameplay.</summary>
    const double MinBand = 0.09;

    /// <summary>Typical on-screen HUD size, enlarged so it stays readable on a phone.</summary>
    static double DefaultWidth(HudElement e) => Math.Clamp(e.SrcW * 2.0, 0.2, 0.7);

    /// <summary>Puts a newly added element into the free space below the gameplay.</summary>
    public void PlaceNew(HudElement e, double sw, double sh, AppUi ui)
    {
        e.OutW = DefaultWidth(e);
        var r = OutputRect(e, sw, sh);
        double bottomSafe = ui.SafeBottom * Height, frameBottom = FrameRect().Bottom;
        double bottomStart = bottomSafe - frameBottom >= MinBand * Height ? frameBottom : Math.Min(frameBottom, bottomSafe - 0.15 * Height);
        double y = (bottomStart + bottomSafe) / 2 - r.Height / 2;
        e.OutX = (Width - r.Width) / 2 / Width;
        e.OutY = Math.Clamp(y, 0, Height - r.Height) / Height;
    }

    enum Side { Left, Center, Right }

    static Side SideOf(HudElement e)
    {
        double cx = e.SrcX + e.SrcW / 2;
        return cx < 0.38 ? Side.Left : cx > 0.62 ? Side.Right : Side.Center;
    }

    /// <param name="rightInset">Room kept free on the right for the like / comment / share buttons.</param>
    static void ArrangeZone(List<HudElement> items, double start, double end, bool fromTop, double rightInset, double sw, double sh)
    {
        if (items.Count == 0) return;
        double margin = 0.045 * Width, gap = 0.012 * Height;

        foreach (var e in items) e.OutW = DefaultWidth(e);

        // Rows: a left and a right element share a row, centered elements get their own.
        var lefts = new Queue<HudElement>(items.Where(e => SideOf(e) == Side.Left).OrderBy(e => e.SrcY));
        var rights = new Queue<HudElement>(items.Where(e => SideOf(e) == Side.Right).OrderBy(e => e.SrcY));
        var rows = new List<List<HudElement>>();
        while (lefts.Count > 0 || rights.Count > 0)
        {
            var row = new List<HudElement>();
            if (lefts.TryDequeue(out var l)) row.Add(l);
            if (rights.TryDequeue(out var r)) row.Add(r);
            rows.Add(row);
        }
        foreach (var c in items.Where(e => SideOf(e) == Side.Center)) rows.Add([c]);
        // Closest to the gameplay first: on top that is the lowest HUD, below it the highest.
        rows = (fromTop ? rows.OrderBy(r => r.Max(e => e.SrcY)) : rows.OrderByDescending(r => r.Min(e => e.SrcY))).ToList();

        // Two elements in one row must fit side by side.
        double rowWidth = Width - 2 * margin - rightInset - gap;
        foreach (var row in rows.Where(r => r.Count == 2))
        {
            double w = row.Sum(e => e.OutW * Width);
            if (w > rowWidth)
                foreach (var e in row) e.OutW *= rowWidth / w;
        }

        double RowHeight(List<HudElement> row) => row.Max(e => OutputRect(e, sw, sh).Height);
        double total = rows.Sum(RowHeight) + gap * (rows.Count - 1);
        double available = Math.Max(1, end - start);
        if (total > available)
        {
            double scale = (available - gap * (rows.Count - 1)) / (total - gap * (rows.Count - 1));
            foreach (var e in items) e.OutW *= Math.Max(0.2, scale);
            total = rows.Sum(RowHeight) + gap * (rows.Count - 1);
        }

        // Stack from the edge of the zone that faces away from the gameplay, centered in the zone when there is room.
        double y = fromTop ? start + Math.Max(0, (available - total) / 2) : end - Math.Max(0, (available - total) / 2);
        foreach (var row in fromTop ? rows.AsEnumerable().Reverse() : rows)
        {
            double h = RowHeight(row);
            double rowTop = fromTop ? y : y - h;
            foreach (var e in row)
            {
                var r = OutputRect(e, sw, sh);
                double x = SideOf(e) switch
                {
                    Side.Left => margin,
                    Side.Right => Width - margin - rightInset - r.Width,
                    _ => (Width - r.Width) / 2,
                };
                e.OutX = x / Width;
                e.OutY = (rowTop + (h - r.Height) / 2) / Height;
            }
            y = fromTop ? y + h + gap : y - h - gap;
        }
    }
}

/// <summary>A named layout, either built in for a game or saved by the user.</summary>
public sealed class ShortsPreset
{
    public string Name { get; set; } = "";
    public ShortsLayout Layout { get; set; } = new();
}

/// <summary>
/// Starting points for popular games. HUD positions are for a 16:9 screen (stretched 4:3 maps to the same spots);
/// they are approximate and meant to be fine-tuned, then saved as an own preset.
/// </summary>
public static class GamePresets
{
    static HudElement E(string name, double x, double y, double w, double h) =>
        new() { Name = name, SrcX = x, SrcY = y, SrcW = w, SrcH = h };

    static ShortsPreset P(string name, params HudElement[] elements) =>
        new() { Name = name, Layout = new ShortsLayout { Elements = [.. elements] } };

    /// <summary>Gives each element a fixed spot (x, y, width as fractions of 1080 × 1920) instead of auto-arranging it.</summary>
    static ShortsPreset Placed(ShortsPreset preset, params (double X, double Y, double W)[] spots)
    {
        for (int i = 0; i < spots.Length; i++)
            (preset.Layout.Elements[i].OutX, preset.Layout.Elements[i].OutY, preset.Layout.Elements[i].OutW) = spots[i];
        return preset;
    }

    public static IReadOnlyList<ShortsPreset> BuiltIn { get; } =
    [
        P("Rust",
            E("Vitals", 0.835, 0.855, 0.16, 0.13),
            E("Hotbar", 0.325, 0.885, 0.35, 0.105)),
        // Hand-placed: kill rewards appear right under the crosshair, so the HUD goes up top below the compass.
        Placed(P("Wardogs",
            E("Minimap", 0.018, 0.687, 0.181, 0.276),
            E("Rewards", 0.841, 0.022, 0.151, 0.072),
            E("Weapon", 0.82, 0.838, 0.172, 0.127)),
            (0.037, 0.104, 0.30), (0.663, 0.104, 0.30), (0.663, 0.157, 0.30)),
        P("Battlefield",
            E("Minimap", 0.012, 0.70, 0.15, 0.27),
            E("Ammo", 0.80, 0.83, 0.19, 0.15)),
        P("Warzone",
            E("Minimap", 0.01, 0.02, 0.17, 0.30),
            E("Health", 0.01, 0.87, 0.22, 0.10),
            E("Ammo", 0.82, 0.85, 0.17, 0.13)),
        P("Fortnite",
            E("Minimap", 0.845, 0.02, 0.145, 0.26),
            E("Health", 0.015, 0.86, 0.26, 0.11),
            E("Inventory", 0.68, 0.84, 0.31, 0.14)),
        P("Valorant",
            E("Minimap", 0.01, 0.02, 0.22, 0.38),
            E("Score", 0.36, 0.0, 0.28, 0.08),
            E("Health", 0.28, 0.88, 0.20, 0.10),
            E("Ammo", 0.55, 0.88, 0.17, 0.10)),
        P("CS2",
            E("Radar", 0.0, 0.0, 0.18, 0.30),
            E("Health", 0.0, 0.92, 0.20, 0.08),
            E("Ammo", 0.84, 0.92, 0.16, 0.08)),
    ];

    /// <summary>Copy of a preset adapted to the loaded video, with elements that have no placement yet auto-arranged.</summary>
    public static ShortsLayout Instantiate(ShortsPreset preset, double sw, double sh, AppUi ui)
    {
        var layout = preset.Layout.Clone();
        if (layout.Elements.Any(e => e.OutW <= 0)) layout.AutoArrange(sw, sh, ui);
        layout.NormalizeCrop(sw, sh);
        return layout;
    }
}
