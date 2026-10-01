namespace Compress.Core;

/// <summary>Engine and settings shared by all pages.</summary>
public sealed class AppState
{
    public AppSettings Settings { get; } = AppSettings.Load();
    public FfmpegTools? Tools { get; set; }
    public EncoderSupport Encoders { get; set; } = EncoderSupport.None;
    public bool DetectingEncoders { get; set; }

    public event EventHandler? EngineChanged;

    public void NotifyEngineChanged() => EngineChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>What the main window needs from each tool page.</summary>
public interface IToolPage
{
    bool IsBusy { get; }
    /// <summary>File currently being written, so it can be removed if the app quits mid-job.</summary>
    string? BusyOutputPath { get; }
    Task LoadVideoAsync(string path);
    /// <summary>Pages that work on one video take the first file; queue pages take all of them.</summary>
    Task LoadVideosAsync(IReadOnlyList<string> paths) => LoadVideoAsync(paths[0]);
    bool HandleKey(System.Windows.Input.KeyEventArgs e);
    void OnHidden();
    void Shutdown();
}
