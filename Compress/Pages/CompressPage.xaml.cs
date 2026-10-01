using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Compress.Controls;
using Compress.Core;

namespace Compress.Pages;

public partial class CompressPage : UserControl, IToolPage
{
    /// <summary>Space reserved below the video for file info and the trim bar.</summary>
    const double CaptionHeight = 150;

    AppState _state = null!;
    bool _loadingVideo, _restoringOptions;
    VideoInfo? _video;
    string? _resultPath, _jobOutputPath, _posterFile;
    CancellationTokenSource? _jobCts;
    TimeSpan _trimStart;
    TimeSpan? _trimEnd;

    public CompressPage()
    {
        InitializeComponent();
    }

    public void Initialize(AppState state)
    {
        _state = state;
        TargetBox.Text = state.Settings.LastTargetMb.ToString("0.##", CultureInfo.InvariantCulture);
        SaveMode.Initialize(state);
        RestoreOptions();
        state.EngineChanged += (_, _) =>
        {
            if (!state.DetectingEncoders)
                HardwareSwitch.IsChecked = state.Settings.UseHardware && state.Encoders.Any;
            UpdateHardwareUi();
            UpdateEstimate();
        };
    }

    // =====================================================================
    // IToolPage
    // =====================================================================

    public bool IsBusy => _jobCts is not null;
    public string? BusyOutputPath => _jobOutputPath;

    public void OnHidden() => Player.Pause();

    public void Shutdown()
    {
        _jobCts?.Cancel();
        Player.Close();
        Ui.TryDelete(_posterFile);
    }

    public bool HandleKey(KeyEventArgs e)
    {
        if (EditorView.Visibility != Visibility.Visible || Keyboard.Modifiers != ModifierKeys.None) return false;
        switch (e.Key)
        {
            case Key.Space: Player.TogglePlay(); return true;
            case Key.I: SetTrimStart(); return true;
            case Key.O: SetTrimEnd(); return true;
            case Key.Left or Key.Right when Keyboard.FocusedElement is not RadioButton:
                Player.SeekBy(e.Key == Key.Left ? -5 : 5);
                return true;
            default: return false;
        }
    }

    // =====================================================================
    // Loading a video
    // =====================================================================

    void SelectVideo_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || _loadingVideo) return;
        if (Ui.PickVideo(this) is { } file) _ = LoadVideoAsync(file);
    }

    public async Task LoadVideoAsync(string path)
    {
        if (IsBusy || _loadingVideo || _state.Tools is null) return;

        _loadingVideo = true;
        EmptyError.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            var info = await VideoEngine.ProbeAsync(_state.Tools, path);
            _video = info;
            _resultPath = null;
            _trimStart = TimeSpan.Zero;
            _trimEnd = null;

            FileTitle.Text = $"{Path.GetFileName(path)} ({Format.Size(info.SizeBytes)})";
            FileMeta.Text = string.Join("  ·  ", new[]
            {
                $"{info.DisplayWidth} × {info.DisplayHeight}",
                $"{Format.Fps(info.Fps)} fps",
                Format.Codec(info.VideoCodec) + (info.IsHdr ? " HDR" : ""),
                info.VideoBitRate > 0 ? (info.VideoBitRate / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " Mbit/s" : null,
                Format.Time(info.Duration),
                info.HasAudio ? null : "No audio",
            }.Where(s => s is not null));

            RemoveAudioSwitch.IsEnabled = info.HasAudio;
            CompareBar.Visibility = Visibility.Collapsed;
            ShowPanel(SettingsPanel);
            EmptyView.Visibility = Visibility.Collapsed;
            EditorView.Visibility = Visibility.Visible;
            _ = Dispatcher.BeginInvoke(LayoutVideo, DispatcherPriority.Loaded);

            Player.Open(path, TimeSpan.Zero, info.Duration);
            UpdateTrimUi();
            UpdateEstimate();
            _ = LoadPosterAsync(info);
        }
        catch (Exception ex)
        {
            if (EditorView.Visibility == Visibility.Visible) ShowError("Could not open video", ex.Message);
            else
            {
                EmptyError.Text = ex.Message;
                EmptyError.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _loadingVideo = false;
            Mouse.OverrideCursor = null;
        }
    }

    async Task LoadPosterAsync(VideoInfo info)
    {
        Player.Poster = null;
        Ui.TryDelete(_posterFile);
        _posterFile = null;
        var file = await VideoEngine.ExtractPosterAsync(_state.Tools!, info);
        if (file is null) return;
        if (_video != info)
        {
            Ui.TryDelete(file);
            return;
        }
        _posterFile = file;
        Player.Poster = Ui.LoadBitmap(file);
    }

    void VideoArea_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutVideo();

    void LayoutVideo()
    {
        var size = Ui.Fit(VideoArea.ActualWidth, VideoArea.ActualHeight - CaptionHeight, _video is null ? 16.0 / 9 : Ui.Aspect(_video));
        if (size.IsEmpty) return;
        Player.Width = size.Width;
        Player.Height = size.Height;
    }

    // =====================================================================
    // Original / result toggle
    // =====================================================================

    bool ShowingResult => _resultPath is not null && Player.CurrentPath == _resultPath;

    void Compare_Checked(object sender, RoutedEventArgs e)
    {
        if (_video is null || _resultPath is null) return;
        bool showResult = CompareResult.IsChecked == true;
        var path = showResult ? _resultPath : _video.Path;
        if (Player.CurrentPath == path) return;

        // The result starts at the trim start, so translate the position between both files.
        var clipLength = (_trimEnd ?? _video.Duration) - _trimStart;
        var position = showResult ? Player.Position - _trimStart : Player.Position + _trimStart;
        if (showResult && (position < TimeSpan.Zero || position >= clipLength - TimeSpan.FromMilliseconds(200)))
            position = TimeSpan.Zero;
        Player.Open(path, position < TimeSpan.Zero ? TimeSpan.Zero : position, showResult ? clipLength : _video.Duration);
        UpdatePlayerRange();
    }

    // =====================================================================
    // Trim
    // =====================================================================

    void SetTrimStart_Click(object sender, RoutedEventArgs e) => SetTrimStart();

    void SetTrimEnd_Click(object sender, RoutedEventArgs e) => SetTrimEnd();

    void SetTrimStart()
    {
        if (_video is null || IsBusy || ShowingResult) return;
        var pos = Player.Position;
        if (pos >= _video.Duration - TimeSpan.FromSeconds(0.2)) return;
        _trimStart = pos;
        if (_trimEnd is { } end && end <= pos + TimeSpan.FromSeconds(0.2)) _trimEnd = null;
        TrimChanged();
    }

    void SetTrimEnd()
    {
        if (_video is null || IsBusy || ShowingResult) return;
        var pos = Player.Position;
        if (pos <= _trimStart + TimeSpan.FromSeconds(0.2)) return;
        _trimEnd = pos >= _video.Duration - TimeSpan.FromMilliseconds(50) ? null : pos;
        TrimChanged();
    }

    void TrimReset_Click(object sender, RoutedEventArgs e)
    {
        _trimStart = TimeSpan.Zero;
        _trimEnd = null;
        TrimChanged();
    }

    void TrimChanged()
    {
        UpdateTrimUi();
        UpdateEstimate();
    }

    bool IsTrimmed => _trimStart > TimeSpan.Zero || _trimEnd is not null;

    void UpdateTrimUi()
    {
        if (_video is null) return;
        var end = _trimEnd ?? _video.Duration;
        TrimStartText.Text = Format.Precise(_trimStart);
        TrimEndText.Text = Format.Precise(end);
        TrimLengthText.Text = $"{(end - _trimStart).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";
        TrimResetButton.Visibility = IsTrimmed ? Visibility.Visible : Visibility.Collapsed;
        UpdatePlayerRange();
    }

    void UpdatePlayerRange()
    {
        if (ShowingResult) Player.SetRange(TimeSpan.Zero, null, highlight: false);
        else Player.SetRange(_trimStart, _trimEnd, highlight: IsTrimmed);
    }

    // =====================================================================
    // Options & estimate
    // =====================================================================

    void RestoreOptions()
    {
        var s = _state.Settings;
        _restoringOptions = true;
        (s.Mode switch
        {
            QualityMode.High => HighRadio,
            QualityMode.Low => LowRadio,
            QualityMode.TargetSize => TargetRadio,
            _ => MediumRadio,
        }).IsChecked = true;
        (s.Codec == CodecChoice.H265 ? CodecH265 : CodecH264).IsChecked = true;
        (s.MaxShortSide switch { 1080 => Res1080, 720 => Res720, 480 => Res480, _ => ResAuto }).IsChecked = true;
        (s.MaxFps switch { 60 => Fps60, 30 => Fps30, _ => FpsOriginal }).IsChecked = true;
        RemoveAudioSwitch.IsChecked = s.RemoveAudio;
        AdvancedToggle.IsChecked = s.Codec != CodecChoice.H264 || s.MaxShortSide is not null || s.MaxFps is not null || s.RemoveAudio;
        _restoringOptions = false;
    }

    void SaveOptions()
    {
        var o = ReadOptions();
        var s = _state.Settings;
        s.Mode = o.Mode;
        s.Codec = o.Codec;
        s.MaxShortSide = o.MaxShortSide;
        s.MaxFps = o.MaxFps;
        s.RemoveAudio = RemoveAudioSwitch.IsChecked == true;
        s.Save();
    }

    void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _restoringOptions) return;
        SaveOptions();
        UpdateHardwareUi();
        UpdateEstimate();
    }

    void HardwareSwitch_Click(object sender, RoutedEventArgs e)
    {
        _state.Settings.UseHardware = HardwareSwitch.IsChecked == true;
        _state.Settings.Save();
        Options_Changed(sender, e);
        _state.NotifyEngineChanged();
    }

    void UpdateHardwareUi()
    {
        if (_state.DetectingEncoders)
        {
            HardwareHint.Text = "Detecting…";
            HardwareSwitch.IsEnabled = false;
            return;
        }
        var vendor = _state.Encoders.VendorFor(CodecH265.IsChecked == true ? CodecChoice.H265 : CodecChoice.H264);
        HardwareSwitch.IsEnabled = vendor != HwVendor.None;
        HardwareHint.Text = vendor != HwVendor.None
            ? $"{Format.Vendor(vendor)} · much faster. Turn off for the smallest files."
            : _state.Encoders.Any ? "Not available for this codec." : "No supported GPU encoder found.";
    }

    void TargetBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (Format.TryParseNumber(TargetBox.Text, out var mb) && mb > 0)
        {
            _state.Settings.LastTargetMb = mb;
            _state.Settings.Save();
        }
        UpdateEstimate();
    }

    void TargetChip_Click(object sender, RoutedEventArgs e)
    {
        TargetBox.Text = (string)((Button)sender).Tag;
        TargetRadio.IsChecked = true;
    }

    CompressOptions ReadOptions() => new()
    {
        Mode = HighRadio.IsChecked == true ? QualityMode.High
            : LowRadio.IsChecked == true ? QualityMode.Low
            : TargetRadio.IsChecked == true ? QualityMode.TargetSize
            : QualityMode.Medium,
        TargetMb = Format.TryParseNumber(TargetBox.Text, out var mb) ? mb : 0,
        Codec = CodecH265.IsChecked == true ? CodecChoice.H265 : CodecChoice.H264,
        MaxShortSide = Res1080.IsChecked == true ? 1080 : Res720.IsChecked == true ? 720 : Res480.IsChecked == true ? 480 : null,
        MaxFps = Fps60.IsChecked == true ? 60 : Fps30.IsChecked == true ? 30 : null,
        RemoveAudio = RemoveAudioSwitch.IsChecked == true && RemoveAudioSwitch.IsEnabled,
        UseHardware = HardwareSwitch.IsChecked == true && HardwareSwitch.IsEnabled,
        TrimStart = _trimStart,
        TrimEnd = _trimEnd,
    };

    /// <summary>Size of the selected part of the original, used as the "before" reference.</summary>
    long SourceClipBytes(VideoInfo v)
    {
        var end = _trimEnd ?? v.Duration;
        double share = v.Duration.TotalSeconds > 0 ? (end - _trimStart).TotalSeconds / v.Duration.TotalSeconds : 1;
        return (long)(v.SizeBytes * Math.Clamp(share, 0, 1));
    }

    void UpdateEstimate()
    {
        if (_video is null) return;
        try
        {
            var plan = VideoEngine.CreatePlan(_video, ReadOptions(), _state.Encoders, "out.mp4", Path.GetTempPath());
            long reference = SourceClipBytes(_video);
            double ratio = 1 - (double)plan.EstimatedBytes / Math.Max(1, reference);
            EstimateText.Text = $"≈ {Format.Size(plan.EstimatedBytes)}";
            EstimateSub.Text = $"{plan.Width}×{plan.Height} · {Format.Fps(plan.Fps)} fps";
            if (ratio > 0.01) EstimateText.Text += $"  (−{ratio * 100:0}%)";

            PlanNote.Text = plan.Note ?? "";
            PlanNote.Foreground = (Brush)FindResource("Text2");
            PlanNote.Visibility = plan.Note is null ? Visibility.Collapsed : Visibility.Visible;
            if (TargetRadio.IsChecked == true && plan.EstimatedBytes >= reference)
            {
                PlanNote.Text = "The target is larger than the original. Pick a smaller size.";
                PlanNote.Foreground = (Brush)FindResource("Warning");
                PlanNote.Visibility = Visibility.Visible;
            }
            CompressButton.IsEnabled = true;
        }
        catch (InvalidOperationException ex)
        {
            EstimateText.Text = "—";
            EstimateSub.Text = "—";
            PlanNote.Text = ex.Message;
            PlanNote.Foreground = (Brush)FindResource("Danger");
            PlanNote.Visibility = Visibility.Visible;
            CompressButton.IsEnabled = false;
        }
    }

    // =====================================================================
    // Compression
    // =====================================================================

    async void CompressButton_Click(object sender, RoutedEventArgs e)
    {
        if (_video is null || _state.Tools is null || IsBusy) return;

        var video = _video;
        var options = ReadOptions();
        long reference = SourceClipBytes(video);
        EncodePlan plan;
        try
        {
            plan = NewPlan(video, options);
        }
        catch (InvalidOperationException ex)
        {
            ShowError("Can't compress with these settings", ex.Message);
            return;
        }

        Player.Pause();
        if (ShowingResult) Player.Open(video.Path, TimeSpan.Zero, video.Duration);
        CompareBar.Visibility = Visibility.Collapsed;
        TrimBar.IsEnabled = false;
        bool replace = _state.Settings.ReplaceOriginal;
        string? reopen = null;
        _jobCts = new CancellationTokenSource();
        ShowPanel(ProgressPanel);
        ProgressPanel.Start();

        try
        {
            string? retryNote = null;
            await RunPlanAsync(plan, "");

            // Safety net: some sources are already so efficient that a quality preset still grows them.
            // Re-encode with a hard bitrate budget so the result is always smaller than the original.
            if (options.Mode != QualityMode.TargetSize && new FileInfo(plan.OutputPath).Length >= reference * 0.97
                && SizeLimitedRetry(video, options, reference) is { } retry)
            {
                File.Delete(plan.OutputPath);
                plan = NewPlan(video, retry);
                retryNote = "The original was already very efficient, so a size limit was applied automatically.";
                await RunPlanAsync(plan, "Re-encoding with a size limit · ");
            }

            ProgressPanel.Stop();

            string resultPath = plan.OutputPath;
            if (replace)
            {
                // The player holds the original open; release it, swap the files, then show the new video.
                Player.Close();
                string? note;
                (resultPath, note) = await OutputFiles.ReplaceOriginalAsync(video.Path, plan.OutputPath);
                PreviewAudio.Forget(video.Path);
                PreviewProxy.Forget(video.Path);
                retryNote = string.Join(" ", new[] { retryNote, note }.Where(n => n is not null));
                reopen = note is null ? resultPath : video.Path;
            }
            ShowResult(reference, plan, resultPath, ProgressPanel.Elapsed, retryNote, replaced: reopen is not null);
        }
        catch (OperationCanceledException)
        {
            ShowPanel(SettingsPanel);
        }
        catch (Exception ex)
        {
            string message = ex.Message;
            if (plan.IsHardware)
                message += "\n\nTip: turn off GPU acceleration under Advanced settings and try again.";
            ShowError("Compression failed", message);
        }
        finally
        {
            ProgressPanel.Stop();
            _jobCts.Dispose();
            _jobCts = null;
            _jobOutputPath = null;
            TrimBar.IsEnabled = true;
        }

        if (reopen is not null)
        {
            // The original is gone (Recycle Bin), so the editor continues with the new file; no before/after toggle.
            var result = _resultPath;
            await LoadVideoAsync(reopen);
            _resultPath = result;
            ShowPanel(DonePanel);
        }
    }

    /// <summary>
    /// Options for the second try with a hard size budget, or null when that budget can't be met
    /// (e.g. a very short clip); then the first result is kept.
    /// </summary>
    CompressOptions? SizeLimitedRetry(VideoInfo video, CompressOptions options, long reference)
    {
        double ratio = options.Mode switch { QualityMode.High => 0.8, QualityMode.Low => 0.4, _ => 0.6 };
        var retry = new CompressOptions
        {
            Mode = QualityMode.TargetSize,
            TargetMb = reference * ratio / 1024 / 1024,
            Codec = options.Codec,
            MaxShortSide = options.MaxShortSide ?? (options.Mode == QualityMode.Low ? 720 : null),
            MaxFps = options.MaxFps,
            RemoveAudio = options.RemoveAudio,
            UseHardware = options.UseHardware,
            TrimStart = options.TrimStart,
            TrimEnd = options.TrimEnd,
        };
        try
        {
            VideoEngine.CreatePlan(video, retry, _state.Encoders, "check.mp4", Path.GetTempPath());
            return retry;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    EncodePlan NewPlan(VideoInfo video, CompressOptions options)
    {
        var output = _state.Settings.BuildOutputPath(video.Path);
        var workDir = Path.Combine(Path.GetTempPath(), "compress-" + Guid.NewGuid().ToString("N"));
        return VideoEngine.CreatePlan(video, options, _state.Encoders, output, workDir);
    }

    async Task RunPlanAsync(EncodePlan plan, string stagePrefix)
    {
        _jobOutputPath = plan.OutputPath;
        ProgressPanel.BeginStage($"{stagePrefix}{plan.EncoderLabel} · {plan.Width}×{plan.Height} · {Format.Fps(plan.Fps)} fps", plan.Passes.Count);
        var progress = new Progress<EncodeProgress>(p =>
        {
            if (_jobCts is not null) ProgressPanel.Report(p);
        });
        await VideoEngine.RunAsync(_state.Tools!, plan, progress, _jobCts!.Token);
    }

    void Progress_CancelRequested(object? sender, EventArgs e) => _jobCts?.Cancel();

    void ShowResult(long before, EncodePlan plan, string resultPath, TimeSpan elapsed, string? retryNote, bool replaced)
    {
        _resultPath = resultPath;
        long after = new FileInfo(resultPath).Length;
        double ratio = 1 - (double)after / Math.Max(1, before);

        DoneFileText.Text = resultPath != plan.OutputPath
            ? $"Replaced {Path.GetFileName(resultPath)}. The original is in the Recycle Bin."
            : $"Saved as {Path.GetFileName(resultPath)}";
        BeforeText.Text = Format.Size(before) + (IsTrimmed ? " *" : "");
        AfterText.Text = Format.Size(after);
        DoneDetails.Text = $"{plan.Width}×{plan.Height} · {Format.Fps(plan.Fps)} fps · {plan.EncoderLabel} · took {Format.Time(elapsed)}"
            + (IsTrimmed ? $"\n* share of the original for the trimmed {plan.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s" : "");

        ReductionText.Text = ratio >= 0 ? $"−{ratio * 100:0}%" : $"+{-ratio * 100:0}%";
        ReductionText.Foreground = (Brush)FindResource(ratio >= 0 ? "Accent" : "Warning");
        ReductionLabel.Text = ratio >= 0 ? "smaller" : "larger";

        var notes = new[] { retryNote, plan.Note }.Where(n => !string.IsNullOrEmpty(n)).ToList();
        DoneWarning.Text = string.Join(" ", notes);
        DoneWarning.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        CopyFileIcon.Text = "\uE8C8";
        CopyFileText.Text = "Copy file (paste into Discord)";
        ShowPanel(DonePanel);
        if (!replaced)
        {
            CompareBar.Visibility = Visibility.Visible;
            CompareResult.IsChecked = false;
            CompareResult.IsChecked = true;
        }
        Ui.NotifyFinished(this);
    }

    void ShowInFolder_Click(object sender, RoutedEventArgs e) => Ui.ShowInExplorer(_resultPath);

    void CopyFile_Click(object sender, RoutedEventArgs e)
    {
        bool ok = Ui.CopyFileToClipboard(_resultPath);
        CopyFileIcon.Text = ok ? "\uE73E" : "\uE783";
        CopyFileText.Text = ok ? "Copied. Paste it with Ctrl+V" : "Could not copy the file";
    }

    void BackToSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(SettingsPanel);
        UpdateEstimate();
    }

    // =====================================================================
    // Panels
    // =====================================================================

    void ShowPanel(FrameworkElement panel) => Ui.ShowOnly(panel, SettingsPanel, ProgressPanel, DonePanel, ErrorPanel);

    void ShowError(string title, string message)
    {
        ErrorTitle.Text = title;
        ErrorText.Text = message;
        ShowPanel(ErrorPanel);
    }
}
