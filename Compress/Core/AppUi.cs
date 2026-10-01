using System.Windows;

namespace Compress.Core;

public enum ShortsPlatform { TikTok, Reels, Shorts }

/// <summary>An area of the app interface that covers the video.</summary>
public sealed record UiZone(string Name, Rect Bounds);

/// <summary>
/// Where TikTok, Instagram Reels and YouTube Shorts draw their interface over a 1080×1920 video.
/// Positions follow the current apps on a typical phone; they shift a little between devices and app versions.
/// </summary>
public sealed class AppUi
{
    const double W = ShortsLayout.Width, H = ShortsLayout.Height;

    public required ShortsPlatform Platform { get; init; }
    /// <summary>Everything above this (fraction of the height) is header.</summary>
    public required double SafeTop { get; init; }
    /// <summary>Everything below this (fraction of the height) is caption and navigation.</summary>
    public required double SafeBottom { get; init; }
    /// <summary>Left edge of the button column (fraction of the width) …</summary>
    public required double ButtonsLeft { get; init; }
    /// <summary>… and its top (fraction of the height).</summary>
    public required double ButtonsTop { get; init; }
    public required IReadOnlyList<UiZone> Zones { get; init; }

    public static AppUi For(ShortsPlatform platform) => platform switch
    {
        ShortsPlatform.Reels => Reels,
        ShortsPlatform.Shorts => Shorts,
        _ => TikTok,
    };

    static UiZone Z(string name, double x, double y, double w, double h) => new(name, new Rect(x, y, w, h));

    public static AppUi TikTok { get; } = new()
    {
        Platform = ShortsPlatform.TikTok,
        SafeTop = 150 / H, SafeBottom = 1550 / H, ButtonsLeft = 900 / W, ButtonsTop = 830 / H,
        Zones =
        [
            Z("the header", 0, 0, W, 150),
            Z("the like and comment buttons", 900, 830, 180, 900),
            Z("the caption", 0, 1550, 880, 210),
            Z("the navigation bar", 0, 1790, W, 130),
        ],
    };

    public static AppUi Reels { get; } = new()
    {
        Platform = ShortsPlatform.Reels,
        SafeTop = 150 / H, SafeBottom = 1510 / H, ButtonsLeft = 930 / W, ButtonsTop = 1060 / H,
        Zones =
        [
            Z("the header", 0, 0, W, 150),
            Z("the like and comment buttons", 930, 1060, 150, 660),
            Z("the caption", 0, 1510, 900, 230),
            Z("the navigation bar", 0, 1790, W, 130),
        ],
    };

    public static AppUi Shorts { get; } = new()
    {
        Platform = ShortsPlatform.Shorts,
        SafeTop = 150 / H, SafeBottom = 1510 / H, ButtonsLeft = 920 / W, ButtonsTop = 900 / H,
        Zones =
        [
            Z("the header", 0, 0, W, 150),
            Z("the like and comment buttons", 920, 900, 160, 840),
            Z("the caption", 0, 1510, 880, 230),
            Z("the navigation bar", 0, 1790, W, 130),
        ],
    };

    /// <summary>The interface zone that hides the biggest part of <paramref name="r"/> (output pixels), if it hides a noticeable part.</summary>
    public UiZone? Covering(Rect r)
    {
        double area = r.Width * r.Height;
        if (area <= 0) return null;
        UiZone? worst = null;
        double worstShare = 0.08;
        foreach (var zone in Zones)
        {
            var overlap = Rect.Intersect(r, zone.Bounds);
            if (overlap.IsEmpty) continue;
            double share = overlap.Width * overlap.Height / area;
            if (share > worstShare)
            {
                worst = zone;
                worstShare = share;
            }
        }
        return worst;
    }
}
