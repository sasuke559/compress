using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Compress.Controls;
using Compress.Core;

namespace Compress.Pages;

/// <summary>
/// Batch-convert videos to another container or codec. Repacks without re-encoding when the codecs already fit
/// (e.g. OBS MKV to MP4), and can turn clips into GIFs or extract the sound as MP3 / WAV.
/// </summary>
public partial class ConverterPage : UserControl, IToolPage
{
    readonly ObservableCollection<QueueItem> _items = [];
    AppState _state = null!;
    CancellationTokenSource? _jobCts;
    string? _jobOutputPath, _lastOutput;
    bool _restoring = true;

    public ConverterPage()
    {
        InitializeComponent();
        QueueList.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => UpdateSummary();
    }

    public void Initialize(AppState state)
    {
        _state = state;
        SaveMode.Initialize(state);
        BuildFormatTiles();
        RestoreOptions();
        state.EngineChanged += (_, _) => UpdateSummary();
    }

    // =====================================================================
    // IToolPage
    // =====================================================================

    public bool IsBusy => _jobCts is not null;
    public string? BusyOutputPath => _jobOutputPath;

    public Task LoadVideoAsync(string path) => AddFilesAsync([path]);

    public Task LoadVideosAsync(IReadOnlyList<string> paths) => AddFilesAsync(paths);

    public void OnHidden() => Player.Pause();

    public void Shutdown()
    {
        _jobCts?.Cancel();
        Player.Close();
    }

    public bool HandleKey(KeyEventArgs e)
    {
        if (EditorView.Visibility != Visibility.Visible || Keyboard.Modifiers != ModifierKeys.None) return false;
        switch (e.Key)
        {
            case Key.Space:
                Player.TogglePlay();
                return true;
            case Key.Delete when QueueList.SelectedItem is QueueItem { CanRemove: true } item && !IsBusy:
                Remove(item);
                return true;
            default:
                return false;
        }
    }

    // =====================================================================
    // Queue
    // =====================================================================

    void AddVideos_Click(object sender, RoutedEventArgs e)
    {
        var files = Ui.PickVideos(this);
        if (files.Length > 0) _ = AddFilesAsync(files);
    }

    async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (_state.Tools is null) return;
        var added = new List<QueueItem>();
        foreach (var path in paths.Where(Ui.IsVideo))
        {
            if (_items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase) && i.Status != QueueStatus.Done)) continue;
            var item = new QueueItem(path);
            _items.Add(item);
            added.Add(item);
        }
        if (added.Count == 0) return;

        EmptyView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Visible;
        if (!IsBusy) ShowPanel(SettingsPanel);
        QueueList.SelectedItem ??= added[0];

        await Task.WhenAll(added.Select(async item =>
        {
            try
            {
                item.Info = await VideoEngine.ProbeAsync(_state.Tools, item.Path);
                item.Status = QueueStatus.Ready;
            }
            catch (Exception ex)
            {
                item.Status = QueueStatus.Failed;
                item.StatusText = "Can't read file";
                item.Message = ex.Message;
            }
        }));
        UpdateSummary();
        UpdatePreview();
    }

    void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is QueueItem { CanRemove: true } item) Remove(item);
    }

    void Remove(QueueItem item)
    {
        if (Player.CurrentPath == item.Path) Player.Close();
        _items.Remove(item);
        if (_items.Count == 0)
        {
            EditorView.Visibility = Visibility.Collapsed;
            EmptyView.Visibility = Visibility.Visible;
        }
        else QueueList.SelectedItem ??= _items[0];
    }

    void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items.Where(i => i.Status is QueueStatus.Done or QueueStatus.Skipped or QueueStatus.Failed).ToList())
            Remove(item);
    }

    void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    // =====================================================================
    // Options
    // =====================================================================

    IEnumerable<RadioButton> FormatTiles => FormatGrid.Children.OfType<RadioButton>();

    void BuildFormatTiles()
    {
        foreach (var f in VideoEngine.ConvertFormats)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = f.Name, Style = (Style)FindResource("TileMain") });
            content.Children.Add(new TextBlock { Text = f.Description, Style = (Style)FindResource("TileSub"), TextTrimming = TextTrimming.CharacterEllipsis });
            var tile = new RadioButton { Style = (Style)FindResource("OptionTile"), GroupName = "ConvertFormat", Tag = f.Format, Content = content };
            tile.Checked += Options_Changed;
            FormatGrid.Children.Add(tile);
        }
    }

    void RestoreOptions()
    {
        var s = _state.Settings;
        (FormatTiles.FirstOrDefault(t => (ConvertFormat)t.Tag == s.ConvertFormat) ?? FormatTiles.First()).IsChecked = true;
        (s.ConvertQuality switch { QualityMode.High => QualityHigh, QualityMode.Medium => QualitySmall, _ => QualityOriginal }).IsChecked = true;
        CopySwitch.IsChecked = s.ConvertPreferCopy;
        _restoring = false;
        UpdateSummary();
    }

    ConvertFormat Target => (ConvertFormat?)FormatTiles.FirstOrDefault(t => t.IsChecked == true)?.Tag ?? ConvertFormat.Mp4;

    QualityMode Quality => QualityHigh.IsChecked == true ? QualityMode.High : QualitySmall.IsChecked == true ? QualityMode.Medium : QualityMode.Source;

    ConvertOptions Options => new()
    {
        Format = Target,
        Quality = Quality,
        PreferCopy = CopySwitch.IsChecked == true,
        UseHardware = _state.Settings.UseHardware,
    };

    void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (_restoring || _state is null) return;
        var s = _state.Settings;
        s.ConvertFormat = Target;
        s.ConvertQuality = Quality;
        s.ConvertPreferCopy = CopySwitch.IsChecked == true;
        s.Save();
        UpdateSummary();
    }

    /// <summary>Output file name: the same name with the new extension, or "_converted" when the extension stays.</summary>
    string OutputPathFor(QueueItem item)
    {
        var ext = VideoEngine.FormatInfo(Target).Extension;
        bool sameExt = string.Equals(Path.GetExtension(item.Path), ext, StringComparison.OrdinalIgnoreCase);
        return _state.Settings.BuildOutputPath(item.Path, sameExt ? "_converted" : "", ext);
    }

    /// <summary>A repack into the very same container would just copy the file.</summary>
    bool AlreadyInFormat(VideoInfo info, ConvertOptions o) =>
        string.Equals(Path.GetExtension(info.Path), VideoEngine.FormatInfo(o.Format).Extension, StringComparison.OrdinalIgnoreCase)
        && o.Format != ConvertFormat.Mp4Hevc
        && VideoEngine.IsLosslessRemux(info, o);

    void UpdateSummary()
    {
        if (_state is null) return;
        var options = Options;
        var format = VideoEngine.FormatInfo(options.Format);
        QueueCount.Text = _items.Count.ToString(CultureInfo.InvariantCulture);

        long total = 0;
        foreach (var item in _items.Where(i => i.Status == QueueStatus.Ready && i.Info is not null))
        {
            var info = item.Info!;
            if (format.AudioOnly && !info.HasAudio)
            {
                item.StatusText = "No sound";
                continue;
            }
            if (AlreadyInFormat(info, options))
            {
                item.StatusText = $"Already {format.Name}";
                continue;
            }
            try
            {
                var plan = VideoEngine.CreateConvertPlan(info, options, _state.Encoders, "x" + format.Extension, Path.GetTempPath());
                total += plan.EstimatedBytes;
                item.StatusText = format.AudioOnly || options.Format == ConvertFormat.Gif ? "Ready"
                    : VideoEngine.IsLosslessRemux(info, options) ? "Ready · lossless" : "Ready · re-encode";
            }
            catch (InvalidOperationException ex)
            {
                item.StatusText = ex.Message;
            }
        }

        int todo = _items.Count(i => i.Status == QueueStatus.Ready && i.StatusText.StartsWith("Ready", StringComparison.Ordinal));
        int ready = _items.Count(i => i.Status == QueueStatus.Ready);
        SummaryCount.Text = todo == _items.Count ? $"{todo}" : $"{todo} of {_items.Count}";
        SummarySize.Text = todo > 0 ? "≈ " + Format.Size(total) : "—";
        string verb = format.AudioOnly ? "Extract sound from" : "Convert";
        ConvertButtonText.Text = todo == 1 ? $"{verb} 1 video" : $"{verb} {todo} videos";
        ConvertButton.IsEnabled = todo > 0 && !IsBusy;

        QualitySection.Visibility = format.HasQuality ? Visibility.Visible : Visibility.Collapsed;
        CopySection.Visibility = format.AudioOnly || options.Format == ConvertFormat.Gif ? Visibility.Collapsed : Visibility.Visible;
        FormatNote.Text = options.Format switch
        {
            ConvertFormat.Gif => "GIFs get big quickly. Best for short clips up to about 15 seconds.",
            ConvertFormat.WebM => "VP9 is encoded on the CPU, so this takes longer than MP4.",
            ConvertFormat.Mp4Hevc => "H.265 is about 40% smaller than H.264 at the same quality, but older devices may not play it.",
            ConvertFormat.Mkv => "MKV keeps every audio track, e.g. game and mic recorded separately by OBS.",
            _ when ready > todo && ready > 0 => "Videos marked \"Already\" are in this format and will be skipped.",
            _ => "",
        };
        FormatNote.Visibility = FormatNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearFinishedButton.Visibility = _items.Any(i => i.Status is QueueStatus.Done or QueueStatus.Skipped or QueueStatus.Failed)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // =====================================================================
    // Preview
    // =====================================================================

    void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutPreview();

    QueueItem? PreviewItem => QueueList?.SelectedItem as QueueItem;

    void UpdatePreview()
    {
        if (_state is null || PreviewItem is not { Info: { } info } item) return;
        if (Player.CurrentPath != item.Path) Player.Open(item.Path, TimeSpan.Zero, info.Duration);
        PreviewBadge.Text = $"{Path.GetExtension(item.Path).TrimStart('.').ToUpperInvariant()} · {Format.Codec(info.VideoCodec)}"
            + (info.HasAudio ? $" · {Format.Codec(info.AudioCodec)}" + (info.AudioTracks > 1 ? $" ×{info.AudioTracks}" : "") : " · no sound");
        LayoutPreview();
    }

    void LayoutPreview()
    {
        if (PreviewItem is not { Info: { } info }) return;
        var fit = Ui.Fit(PreviewHost.ActualWidth, PreviewHost.ActualHeight, Ui.Aspect(info));
        if (fit.IsEmpty) return;
        Player.Width = fit.Width;
        Player.Height = fit.Height;
    }

    // =====================================================================
    // Processing
    // =====================================================================

    async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Tools is null || IsBusy) return;
        var todo = _items.Where(i => i.Status == QueueStatus.Ready && i.Info is not null).ToList();
        if (todo.Count == 0) return;

        var options = Options;
        var format = VideoEngine.FormatInfo(options.Format);
        Player.Pause();
        _jobCts = new CancellationTokenSource();
        AddMoreButton.IsEnabled = false;
        ShowPanel(ProgressPanel);
        ProgressPanel.Start();
        bool cancelled = false;

        // An MP3, WAV or GIF never replaces the video it came from.
        bool replace = _state.Settings.ReplaceOriginal && Target is not (ConvertFormat.Mp3 or ConvertFormat.Wav or ConvertFormat.Gif);
        for (int i = 0; i < todo.Count && !cancelled; i++)
        {
            var item = todo[i];
            if (!_items.Contains(item)) continue; // removed from the queue while waiting
            var info = item.Info!;
            ProgressPanel.Title = todo.Count == 1 ? "Converting" : $"Converting {i + 1} of {todo.Count}";

            if (format.AudioOnly && !info.HasAudio)
            {
                item.Status = QueueStatus.Skipped;
                item.StatusText = "Skipped · no sound";
                continue;
            }
            if (AlreadyInFormat(info, options))
            {
                item.Status = QueueStatus.Skipped;
                item.StatusText = $"Skipped · already {format.Name}";
                continue;
            }

            EncodePlan plan;
            try
            {
                plan = VideoEngine.CreateConvertPlan(info, options, _state.Encoders, OutputPathFor(item),
                    Path.Combine(Path.GetTempPath(), "compress-" + Guid.NewGuid().ToString("N")));
            }
            catch (InvalidOperationException ex)
            {
                MarkFailed(item, ex.Message);
                continue;
            }

            QueueList.SelectedItem = item;
            QueueList.ScrollIntoView(item);
            item.Status = QueueStatus.Processing;
            item.Progress = 0;
            item.StatusText = "0%";
            _jobOutputPath = plan.OutputPath;
            ProgressPanel.BeginStage($"{item.FileName} → {format.Name}\n{plan.EncoderLabel}", plan.Passes.Count);

            var progress = new Progress<EncodeProgress>(p =>
            {
                if (_jobCts is null || item.Status != QueueStatus.Processing) return;
                item.Progress = p.Fraction;
                item.StatusText = $"{Math.Floor(p.Fraction * 100):0}%";
                ProgressPanel.Report(p);
            });

            try
            {
                await VideoEngine.RunAsync(_state.Tools, plan, progress, _jobCts.Token);
                item.OutputPath = await FinishOutputAsync(item, plan.OutputPath, replace);
                item.Status = QueueStatus.Done;
                item.StatusText = $"Done · {Format.Size(new FileInfo(item.OutputPath).Length)}";
                _lastOutput = item.OutputPath;
            }
            catch (OperationCanceledException)
            {
                item.Status = QueueStatus.Ready;
                item.Progress = 0;
                cancelled = true;
            }
            catch (Exception ex)
            {
                MarkFailed(item, ex.Message + (plan.IsHardware ? "\n\nTip: turn off GPU acceleration on the Compress page and try again." : ""));
            }
        }

        ProgressPanel.Stop();
        _jobCts.Dispose();
        _jobCts = null;
        _jobOutputPath = null;
        AddMoreButton.IsEnabled = true;
        UpdateSummary();

        if (cancelled)
        {
            ShowPanel(SettingsPanel);
            return;
        }
        ShowDone(todo, format);
        Ui.NotifyFinished(this);
    }

    /// <summary>With "replace original" the result takes the original's place (original to the Recycle Bin).</summary>
    async Task<string> FinishOutputAsync(QueueItem item, string output, bool replace)
    {
        if (!replace) return output;
        if (Player.CurrentPath == item.Path) Player.Close();
        var original = item.Path;
        var (final, note) = await OutputFiles.ReplaceOriginalAsync(original, output);
        PreviewAudio.Forget(original);
        PreviewProxy.Forget(original);
        if (note is null) item.Path = final;
        else item.Message = note;
        return final;
    }

    static void MarkFailed(QueueItem item, string message)
    {
        item.Status = QueueStatus.Failed;
        item.StatusText = "Failed";
        item.Message = message;
    }

    void ShowDone(List<QueueItem> processed, ConvertFormatInfo format)
    {
        int done = processed.Count(i => i.Status == QueueStatus.Done);
        int skipped = processed.Count(i => i.Status == QueueStatus.Skipped);
        int failed = processed.Count(i => i.Status == QueueStatus.Failed);

        DoneTitle.Text = failed == 0 ? "All done" : done > 0 ? "Finished with errors" : "Conversion failed";
        var parts = new List<string> { done == 1 ? $"1 video converted to {format.Name}" : $"{done} videos converted to {format.Name}" };
        if (skipped > 0) parts.Add($"{skipped} skipped");
        if (failed > 0) parts.Add($"{failed} failed (hover over it in the queue for details)");
        DoneText.Text = string.Join(" · ", parts) + ".";
        ShowPanel(DonePanel);
    }

    void Progress_CancelRequested(object? sender, EventArgs e) => _jobCts?.Cancel();

    void ShowInFolder_Click(object sender, RoutedEventArgs e) => Ui.ShowInExplorer(_lastOutput);

    void ConvertMore_Click(object sender, RoutedEventArgs e)
    {
        ClearFinished_Click(sender, e);
        ShowPanel(SettingsPanel);
        if (_items.Count == 0) AddVideos_Click(sender, e);
    }

    void BackToSettings_Click(object sender, RoutedEventArgs e) => ShowPanel(SettingsPanel);

    void ShowPanel(FrameworkElement panel) => Ui.ShowOnly(panel, SettingsPanel, ProgressPanel, DonePanel);
}
