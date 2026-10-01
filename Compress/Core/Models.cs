using System.Globalization;

namespace Compress.Core;

/// <summary>Source = near-lossless re-encode, used by the cutter's precise mode.</summary>
public enum QualityMode { Medium, High, Low, TargetSize, Source }

public enum CodecChoice { H264, H265 }

public enum HwVendor { None, Nvidia, Intel, Amd }

public enum ScaleMode { Stretch, Fit, Crop }

public sealed class VideoInfo
{
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public TimeSpan Duration { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Rotation { get; init; }
    public double Fps { get; init; }
    public string VideoCodec { get; init; } = "";
    public string AudioCodec { get; init; } = "";
    /// <summary>Number of audio tracks (OBS often records game and mic separately).</summary>
    public int AudioTracks { get; init; }
    /// <summary>Title of each audio track as stored in the file ("" when it has none).</summary>
    public IReadOnlyList<string> AudioTrackTitles { get; init; } = [];
    public long BitRate { get; init; }
    public long VideoBitRate { get; init; }
    public long AudioBitRate { get; init; }
    public bool HasAudio { get; init; }
    public int AudioChannels { get; init; }
    public bool IsHdr { get; init; }

    bool IsRotated => Math.Abs(Rotation) % 180 == 90;
    public int DisplayWidth => IsRotated ? Height : Width;
    public int DisplayHeight => IsRotated ? Width : Height;
}

public sealed class CompressOptions
{
    public QualityMode Mode { get; init; }
    public double TargetMb { get; init; }
    public CodecChoice Codec { get; init; }
    public int? MaxShortSide { get; init; }
    public double? MaxFps { get; init; }
    public bool RemoveAudio { get; init; }
    public bool UseHardware { get; init; }
    public TimeSpan TrimStart { get; init; }
    /// <summary>null = until the end of the video.</summary>
    public TimeSpan? TrimEnd { get; init; }
    /// <summary>Exact output size; overrides MaxShortSide.</summary>
    public (int Width, int Height)? ResizeTo { get; init; }
    public ScaleMode ResizeMode { get; init; }
    /// <summary>Volume per audio track (1 = unchanged, 0 = muted, up to 2); null = all tracks as they are.</summary>
    public IReadOnlyList<double>? TrackVolumes { get; init; }
}

public readonly record struct EncodeProgress(double Fraction, double Speed, int Pass, int PassCount);

public sealed class FfmpegException(string message) : Exception(message);

public static class Format
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Size(long bytes)
    {
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0", Inv) + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.00", Inv) + " MB";
        return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00", Inv) + " GB";
    }

    public static string Time(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>m:ss.f, e.g. 1:05.2</summary>
    public static string Precise(TimeSpan t) =>
        $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";

    /// <summary>m:ss.ff, e.g. 1:05.25 (hundredths, used by the cutter).</summary>
    public static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 10:00}";
    }

    /// <summary>Parses "1:05.25", "0:01:05", "65.5" or "65,5" into a time.</summary>
    public static bool TryParseTime(string text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length is < 1 or > 3) return false;
        double seconds = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, Inv, out var n) || n < 0) return false;
            seconds = seconds * 60 + n;
        }
        time = TimeSpan.FromSeconds(seconds);
        return true;
    }

    public static string Fps(double fps) =>
        Math.Abs(fps - Math.Round(fps)) < 0.05 ? Math.Round(fps).ToString(Inv) : fps.ToString("0.##", Inv);

    public static string Codec(string name) => name.ToLowerInvariant() switch
    {
        "h264" => "H.264",
        "hevc" => "H.265",
        "av1" => "AV1",
        "vp9" => "VP9",
        "vp8" => "VP8",
        "prores" => "ProRes",
        "mpeg4" => "MPEG-4",
        "mpeg2video" => "MPEG-2",
        "" => "Unknown",
        var other => other.ToUpperInvariant(),
    };

    public static string Vendor(HwVendor v) => v switch
    {
        HwVendor.Nvidia => "NVIDIA NVENC",
        HwVendor.Intel => "Intel Quick Sync",
        HwVendor.Amd => "AMD AMF",
        _ => "CPU",
    };

    public static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, Inv, out value);
}
