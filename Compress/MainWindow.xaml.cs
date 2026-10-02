using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Compress.Controls;
using Compress.Core;
using Microsoft.Win32;

namespace Compress;

/// <summary>App shell: sidebar navigation, header, settings, engine setup and drag &amp; drop routing.</summary>
public partial class MainWindow : Window
{
    readonly AppState _state = new();
    string? _ffmpegVersion;

    public MainWindow()
    {
        InitializeComponent();

        CompressView.Initialize(_state);
        CutterView.Initialize(_state);
        ResizeView.Initialize(_state);
        ShortsView.Initialize(_state);
        ConverterView.Initialize(_state);
        CutterView.SendToCompressRequested += OpenInCompress;
        ShortsView.SendToCompressRequested += OpenInCompress;

        SuffixBox.Text = _state.Settings.Suffix;
        MixAudioSwitch.IsChecked = VideoEngine.MixAudioTracks = _state.Settings.MixAudioTracks;
        UsageSwitch.IsChecked = _state.Settings.UsageStats;
        Usage.Start(_state.Settings);
        PreviewAudio.Cleanup(); // leftovers from a session that did not close normally
        PreviewProxy.Cleanup();
        UpdateOutputUi();
        (_state.Settings.LastPage switch { "Cutter" => NavCutter, "Resize" => NavResize, "Shorts" => NavShorts, "Converter" => NavConverter, _ => NavCompress }).IsChecked = true;

        Loaded += async (_, _) => await InitializeEngineAsync();
        Closing += Window_Closing;
        Ui.MakeDropdown(SettingsPopup, SettingsButton);
        FeedbackOverlay.SystemInfoProvider = () => Feedback.SystemInfo(_state, _ffmpegVersion, PageTitle.Text);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.ApplyDarkChrome(this);
    }

    IToolPage[] Pages => [CompressView, CutterView, ResizeView, ShortsView, ConverterView];

    UserControl ActiveView =>
        NavCutter.IsChecked == true ? CutterView
        : NavResize.IsChecked == true ? ResizeView
        : NavShorts.IsChecked == true ? ShortsView
        : NavConverter.IsChecked == true ? ConverterView
        : CompressView;

    void OpenInCompress(string path)
    {
        NavCompress.IsChecked = true;
        _ = CompressView.LoadVideoAsync(path);
    }

    IToolPage ActivePage => (IToolPage)ActiveView;

    // =====================================================================
    // Navigation
    // =====================================================================

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || CompressView is null || CutterView is null || ResizeView is null || ShortsView is null || ConverterView is null) return;
        var show = ActiveView;
        UserControl[] all = [CompressView, CutterView, ResizeView, ShortsView, ConverterView];
        foreach (var page in all)
            if (page != show && page.Visibility == Visibility.Visible) ((IToolPage)page).OnHidden();
        Ui.ShowOnly(show, all);

        string name = show == CutterView ? "Cutter" : show == ResizeView ? "Resize" : show == ShortsView ? "Shorts" : show == ConverterView ? "Converter" : "Compress";
        PageTitle.Text = name;
        Title = name == "Compress" ? "Compress" : $"Compress · {name}";
        _state.Settings.LastPage = name;
        _state.Settings.Save();
    }

    // =====================================================================
    // Engine
    // =====================================================================

    async Task InitializeEngineAsync()
    {
        _state.Tools = FfmpegTools.Locate(_state.Settings.FfmpegDir);
        UpdateEngineUi();
        if (_state.Tools is null) return;
        var tools = _state.Tools;
        VideoPlayer.TrackAudioProvider = path => PreviewAudio.GetAsync(tools, path);
        VideoPlayer.PreviewSourceProvider = path => PreviewProxy.GetAsync(tools, path);

        // "Open with" / files passed on the command line.
        var files = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToList();
        if (files.Count > 0) _ = ActivePage.LoadVideosAsync(files);

        _state.DetectingEncoders = true;
        UpdateEngineUi();
        try
        {
            _state.Encoders = await EncoderSupport.DetectAsync(_state.Tools);
        }
        catch
        {
            _state.Encoders = EncoderSupport.None;
        }
        _state.DetectingEncoders = false;
        UpdateEngineUi();

        try
        {
            var version = _ffmpegVersion = await _state.Tools.GetVersionAsync();
            FfmpegInfoText.Text = $"FFmpeg {version}\n{_state.Tools.Directory}";
        }
        catch { /* purely informational */ }
    }

    void UpdateEngineUi()
    {
        bool ready = _state.Tools is not null;
        SetupView.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        PagesHost.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        EngineBadge.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;

        if (!ready)
        {
            FfmpegInfoText.Text = "FFmpeg not found";
            HardwareInfoText.Text = "";
        }
        else if (_state.DetectingEncoders)
        {
            FfmpegInfoText.Text = $"FFmpeg\n{_state.Tools!.Directory}";
            EngineBadgeText.Text = "Detecting GPU…";
            EngineDot.Fill = (Brush)FindResource("Text3");
            HardwareInfoText.Text = "Checking hardware encoders…";
        }
        else
        {
            var enc = _state.Encoders;
            bool gpu = _state.Settings.UseHardware && enc.H264 != HwVendor.None;
            EngineBadgeText.Text = gpu ? $"GPU · {Format.Vendor(enc.H264)}" : "CPU encoding";
            EngineDot.Fill = (Brush)FindResource(gpu ? "Accent" : "Text3");
            HardwareInfoText.Text = enc.Any
                ? $"Hardware encoding: H.264 → {Format.Vendor(enc.H264)}, H.265 → {Format.Vendor(enc.Hevc)}"
                : "Hardware encoding: not available, using CPU (x264 / x265).";
        }
        _state.NotifyEngineChanged();
    }

    async void DownloadFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        DownloadFfmpegButton.IsEnabled = false;
        FfmpegProgress.Visibility = Visibility.Visible;
        FfmpegProgress.Value = 0;
        SetupError.Visibility = Visibility.Collapsed;
        var defaultText = FfmpegCardText.Text;
        FfmpegCardText.Text = "Downloading FFmpeg…";
        var progress = new Progress<double>(p =>
        {
            FfmpegProgress.Value = p;
            FfmpegCardText.Text = p < 0.95 ? $"Downloading FFmpeg… {p / 0.95 * 100:0}%" : "Installing…";
        });

        try
        {
            var tools = await Task.Run(() => FfmpegTools.DownloadAsync(progress, CancellationToken.None));
            _state.Settings.FfmpegDir = tools.Directory;
            _state.Settings.Save();
            await InitializeEngineAsync();
        }
        catch (Exception ex)
        {
            FfmpegCardText.Text = defaultText;
            ShowSetupError(ex.Message);
        }
        finally
        {
            DownloadFfmpegButton.IsEnabled = true;
            FfmpegProgress.Visibility = Visibility.Collapsed;
        }
    }

    async void LocateFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        var dialog = new OpenFileDialog
        {
            Title = "Locate ffmpeg.exe",
            Filter = "ffmpeg.exe|ffmpeg.exe|Programs (*.exe)|*.exe",
        };
        if (dialog.ShowDialog(this) != true) return;

        var dir = Path.GetDirectoryName(dialog.FileName)!;
        if (!File.Exists(Path.Combine(dir, "ffprobe.exe")))
        {
            ShowSetupError("ffprobe.exe must be in the same folder as ffmpeg.exe.");
            return;
        }
        _state.Settings.FfmpegDir = dir;
        _state.Settings.Save();
        SetupError.Visibility = Visibility.Collapsed;
        await InitializeEngineAsync();
    }

    void ShowSetupError(string message)
    {
        SetupError.Text = message;
        SetupError.Visibility = Visibility.Visible;
    }

    // =====================================================================
    // Settings flyout
    // =====================================================================


    void UpdateOutputUi()
    {
        bool custom = !string.IsNullOrWhiteSpace(_state.Settings.OutputFolder);
        OutputCustomRadio.IsChecked = custom;
        OutputSameRadio.IsChecked = !custom;
        OutputFolderText.Text = custom ? _state.Settings.OutputFolder : "Next to each original video";
    }

    void OutputMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (OutputSameRadio.IsChecked == true)
        {
            _state.Settings.OutputFolder = null;
            _state.Settings.Save();
            UpdateOutputUi();
        }
        else if (string.IsNullOrWhiteSpace(_state.Settings.OutputFolder))
        {
            ChooseOutputFolder_Click(sender, e);
        }
    }

    void ChooseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Save videos to" };
        if (dialog.ShowDialog(this) == true)
        {
            _state.Settings.OutputFolder = dialog.FolderName;
            _state.Settings.Save();
        }
        UpdateOutputUi();
    }

    void UsageSwitch_Click(object sender, RoutedEventArgs e)
    {
        _state.Settings.UsageStats = UsageSwitch.IsChecked == true;
        _state.Settings.Save();
    }

    void MixAudioSwitch_Click(object sender, RoutedEventArgs e)
    {
        VideoEngine.MixAudioTracks = _state.Settings.MixAudioTracks = MixAudioSwitch.IsChecked == true;
        _state.Settings.Save();
        _state.NotifyEngineChanged(); // pages refresh their estimates
    }

    void SuffixBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var suffix = string.Concat(SuffixBox.Text.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        _state.Settings.Suffix = string.IsNullOrWhiteSpace(suffix) ? "_compressed" : suffix;
        SuffixBox.Text = _state.Settings.Suffix;
        _state.Settings.Save();
    }

    // =====================================================================
    // Feedback
    // =====================================================================

    void FeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        FeedbackOverlay.Open();
    }

    /// <summary>Opens the feedback panel as a bug report about an unexpected error (offered by the crash handler).</summary>
    public void ReportError(Exception ex) =>
        FeedbackOverlay.Open(FeedbackKind.Bug, $"Error: {ex.Message.Split('\n')[0].Trim()}",
            "What were you doing when the error appeared?\n\n");

    // =====================================================================
    // Closing
    // =====================================================================

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        var busy = Pages.Where(p => p.IsBusy).ToList();
        if (busy.Count > 0)
        {
            var answer = MessageBox.Show(this, "A video is still being processed. Stop and quit?", "Compress",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            var partial = busy.Select(p => p.BusyOutputPath).ToList();
            VideoEngine.KillAll();
            foreach (var file in partial) DeleteWithRetry(file);
        }
        foreach (var page in Pages) page.Shutdown();
        PreviewAudio.Cleanup();
        PreviewProxy.Cleanup();
    }

    static void DeleteWithRetry(string? path)
    {
        if (path is null) return;
        for (int i = 0; i < 20 && File.Exists(path); i++)
        {
            try { File.Delete(path); }
            catch (IOException) { Thread.Sleep(50); } // ffmpeg may still hold the handle for a moment
            catch (UnauthorizedAccessException) { return; }
        }
    }

    // =====================================================================
    // Drag & drop, keyboard
    // =====================================================================

    static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];

    void Window_DragEnter(object sender, DragEventArgs e)
    {
        bool ok = _state.Tools is not null && !ActivePage.IsBusy && DroppedFiles(e).Length > 0;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropText.Text = ActiveView == CutterView ? "Drop to cut this video"
            : ActiveView == ResizeView ? "Drop to add videos to the queue"
            : ActiveView == ShortsView ? "Drop to make a short"
            : ActiveView == ConverterView ? "Drop to add videos to convert"
            : "Drop to compress this video";
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropOverlay.Visibility == Visibility.Visible ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements; only hide when the cursor left the window.
        var p = e.GetPosition(this);
        if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight)
            DropOverlay.Visibility = Visibility.Collapsed;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var page = ActivePage;
        if (page.IsBusy) return;
        var files = DroppedFiles(e);
        var videos = files.Where(Ui.IsVideo).ToList();
        if (videos.Count > 0) _ = page.LoadVideosAsync(videos);
        else if (files.Length > 0)
            MessageBox.Show(this, $"\"{Path.GetFileName(files[0])}\" is not a supported video file.", "Compress",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || FeedbackOverlay.IsOpen) return;

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.D1: NavCompress.IsChecked = true; e.Handled = true; return;
                case Key.D2: NavCutter.IsChecked = true; e.Handled = true; return;
                case Key.D3: NavResize.IsChecked = true; e.Handled = true; return;
                case Key.D4: NavShorts.IsChecked = true; e.Handled = true; return;
                case Key.D5: NavConverter.IsChecked = true; e.Handled = true; return;
                case Key.O when _state.Tools is not null && !ActivePage.IsBusy:
                    var files = Ui.PickVideos(this, multiple: ActiveView == ResizeView || ActiveView == ConverterView);
                    if (files.Length > 0) _ = ActivePage.LoadVideosAsync(files);
                    e.Handled = true;
                    return;
            }
        }
        if (_state.Tools is not null && ActivePage.HandleKey(e)) e.Handled = true;
    }
}
