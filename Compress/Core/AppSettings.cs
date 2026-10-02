using System.Text.Json;

namespace Compress.Core;

public sealed class AppSettings
{
    static string FilePath => Path.Combine(FfmpegTools.DataDir, "settings.json");

    /// <summary>null = save next to the original video.</summary>
    public string? OutputFolder { get; set; }
    public string Suffix { get; set; } = "_compressed";
    public string? FfmpegDir { get; set; }
    public bool UseHardware { get; set; } = true;
    /// <summary>Mix separate audio tracks (game + mic) into one when exporting.</summary>
    public bool MixAudioTracks { get; set; } = true;
    /// <summary>Exports replace the video they were made from (original goes to the Recycle Bin) instead of being saved next to it.</summary>
    public bool ReplaceOriginal { get; set; }
    /// <summary>Cutter: last volume per audio track (game, mic, ...), reused for the next clip.</summary>
    public List<double> TrackVolumes { get; set; } = [];
    public double LastTargetMb { get; set; } = 10;
    /// <summary>Page shown on start: Compress, Cutter, Resize, Shorts or Converter.</summary>
    public string LastPage { get; set; } = "Compress";

    // Anonymous usage statistics (see Core/Usage.cs)
    public bool UsageStats { get; set; } = true;
    public bool UsageInstallSent { get; set; }
    public string? UsageLastActiveDay { get; set; }

    // Last used compression options, restored on the next start.
    public QualityMode Mode { get; set; } = QualityMode.Medium;
    public CodecChoice Codec { get; set; } = CodecChoice.H264;
    public int? MaxShortSide { get; set; }
    public double? MaxFps { get; set; }
    public bool RemoveAudio { get; set; }

    // Resize page
    public int ResizeWidth { get; set; } = 1920;
    public int ResizeHeight { get; set; } = 1080;
    public ScaleMode ResizeMode { get; set; } = ScaleMode.Stretch;
    public QualityMode ResizeQuality { get; set; } = QualityMode.Source;
    public bool ResizeSkipSameSize { get; set; } = true;

    // Converter page
    public ConvertFormat ConvertFormat { get; set; } = ConvertFormat.Mp4;
    public QualityMode ConvertQuality { get; set; } = QualityMode.Source;
    public bool ConvertPreferCopy { get; set; } = true;

    // Shorts page
    /// <summary>Layout of the last short, restored for the next video (players usually stick to one game).</summary>
    public ShortsLayout? ShortsLayout { get; set; }
    public string? ShortsGame { get; set; }
    public List<ShortsPreset> ShortsPresets { get; set; } = [];
    public bool ShortsLimit60 { get; set; } = true;
    /// <summary>Show the app interface (buttons, caption…) over the preview.</summary>
    public bool ShortsSafeZones { get; set; } = true;
    /// <summary>App the short is made for; decides where its buttons and caption are kept clear.</summary>
    public ShortsPlatform ShortsPlatform { get; set; } = ShortsPlatform.TikTok;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt settings: start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(FfmpegTools.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-critical */ }
    }

    public string BuildOutputPath(string inputPath) =>
        BuildOutputPath(inputPath, string.IsNullOrWhiteSpace(Suffix) ? "_compressed" : Suffix, ".mp4");

    /// <summary>Unique output path in the configured folder (or next to the input).</summary>
    public string BuildOutputPath(string inputPath, string suffix, string extension)
    {
        var dir = !string.IsNullOrWhiteSpace(OutputFolder) && Directory.Exists(OutputFolder)
            ? OutputFolder
            : Path.GetDirectoryName(inputPath)!;
        var name = Path.GetFileNameWithoutExtension(inputPath) + suffix;
        var path = Path.Combine(dir, name + extension);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{name} ({i}){extension}");
        return path;
    }
}
