using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Compress.Controls;
using Compress.Core;

namespace Compress.Pages;

/// <summary>Batch-resize videos to one exact resolution (stretch, fit or crop), e.g. 1440x1080 gameplay to 1920x1080.</summary>
public partial class ResizePage : UserControl, IToolPage
{
    readonly ObservableCollection<QueueItem> _items = [];
    AppState _state = null!;
    CancellationTokenSource? _jobCts;
    string? _jobOutputPath, _lastOutput;
    bool _restoring = true;

    public ResizePage()
    {
        InitializeComponent();
        QueueList.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => UpdateSummary();
    }

    public void Initialize(AppState state)
    {
        _state = state;
        SaveMode.Initialize(state);
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

        // Read all files in parallel; each row updates on its own.
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
        if (Player.CurrentPath == item.Path || Player.CurrentPath == item.OutputPath) Player.Close();
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

    IEnumerable<RadioButton> ResolutionTiles => ResolutionGrid.Children.OfType<RadioButton>();

    void RestoreOptions()
    {
        var s = _state.Settings;
        string tag = $"{s.ResizeWidth}x{s.ResizeHeight}";
        var tile = ResolutionTiles.FirstOrDefault(r => (string)r.Tag == tag) ?? CustomRadio;
        tile.IsChecked = true;
        CustomWidth.Text = s.ResizeWidth.ToString(CultureInfo.InvariantCulture);
        CustomHeight.Text = s.ResizeHeight.ToString(CultureInfo.InvariantCulture);
        (s.ResizeMode switch { ScaleMode.Fit => FitRadio, ScaleMode.Crop => CropRadio, _ => StretchRadio }).IsChecked = true;
        (s.ResizeQuality switch { QualityMode.High => QualityHigh, QualityMode.Medium => QualityMedium, _ => QualityOriginal }).IsChecked = true;
        SkipSameSwitch.IsChecked = s.ResizeSkipSameSize;
        _restoring = false;
        UpdateSummary();
    }

    (int Width, int Height)? TargetSize()
    {
        if (CustomRadio.IsChecked == true)
        {
            bool okW = int.TryParse(CustomWidth.Text.Trim(), out var w);
            bool okH = int.TryParse(CustomHeight.Text.Trim(), out var h);
            // Encoders need even dimensions, so show what will actually be written.
            return okW && okH && w is >= 16 and <= 8192 && h is >= 16 and <= 8192 ? (w / 2 * 2, h / 2 * 2) : null;
        }
        var tag = (string?)ResolutionTiles.FirstOrDefault(r => r.IsChecked == true)?.Tag;
        if (tag is null) return null;
        var parts = tag.Split('x');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    ScaleMode Mode => FitRadio.IsChecked == true ? ScaleMode.Fit : CropRadio.IsChecked == true ? ScaleMode.Crop : ScaleMode.Stretch;

    QualityMode Quality => QualityHigh.IsChecked == true ? QualityMode.High : QualityMedium.IsChecked == true ? QualityMode.Medium : QualityMode.Source;

    bool AlreadyTargetSize(VideoInfo info, (int Width, int Height) size) =>
        info.DisplayWidth == size.Width && info.DisplayHeight == size.Height;

    void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (_restoring || _state is null) return;
        SaveOptions();
        UpdateSummary();
        UpdatePreview();
    }

    void Custom_TextChanged(object sender, TextChangedEventArgs e) => Options_Changed(sender, e);

    void SaveOptions()
    {
        var s = _state.Settings;
        if (TargetSize() is { } size) (s.ResizeWidth, s.ResizeHeight) = size;
        s.ResizeMode = Mode;
        s.ResizeQuality = Quality;
        s.ResizeSkipSameSize = SkipSameSwitch.IsChecked == true;
        s.Save();
    }

    void UpdateSummary()
    {
        if (_state is null) return;
        QueueCount.Text = _items.Count.ToString(CultureInfo.InvariantCulture);
        var size = TargetSize();
        bool skip = SkipSameSwitch.IsChecked == true;

        // Show which queued videos will be skipped.
        foreach (var item in _items.Where(i => i.Status == QueueStatus.Ready && i.Info is not null))
            item.StatusText = size is { } s && skip && AlreadyTargetSize(item.Info!, s) ? $"Already {s.Width}×{s.Height}" : "Ready";

        int todo = _items.Count(i => i.Status == QueueStatus.Ready);
        SummaryCount.Text = todo == _items.Count ? $"{todo}" : $"{todo} of {_items.Count}";
        SummaryOutput.Text = size is { } t ? $"{t.Width}×{t.Height} · {Mode}" : "—";
        ResizeButtonText.Text = todo == 1 ? "Resize 1 video" : $"Resize {todo} videos";

        SettingsNote.Visibility = size is null ? Visibility.Visible : Visibility.Collapsed;
        SettingsNote.Text = "Enter a width and height between 16 and 8192.";
        ResizeButton.IsEnabled = size is not null && todo > 0 && !IsBusy;
        ClearFinishedButton.Visibility = _items.Any(i => i.Status is QueueStatus.Done or QueueStatus.Skipped or QueueStatus.Failed)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // =====================================================================
    // Live preview
    // =====================================================================

    void PreviewMode_Checked(object sender, RoutedEventArgs e) => UpdatePreview();

    void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutPreview();

    QueueItem? PreviewItem => QueueList?.SelectedItem as QueueItem;

    void UpdatePreview()
    {
        if (_state is null || PreviewItem is not { Info: { } info } item) return;
        var source = item.Status == QueueStatus.Done && item.OutputPath is { } output && File.Exists(output)
                     && (ShowAfter.IsChecked == true || !File.Exists(item.Path))
            ? output
            : item.Path;
        if (Player.CurrentPath != source) Player.Open(source, TimeSpan.Zero, info.Duration);
        LayoutPreview();
    }

    /// <summary>Sizes the preview to the target aspect and stretches the picture exactly like the export will.</summary>
    void LayoutPreview()
    {
        if (PreviewItem is not { Info: { } info }) return;
        bool before = ShowBefore.IsChecked == true;
        var size = TargetSize();
        double aspect = before || size is null ? Ui.Aspect(info) : (double)size.Value.Width / size.Value.Height;

        Player.VideoStretch = before || size is null ? Stretch.Uniform : Mode switch
        {
            ScaleMode.Stretch => Stretch.Fill,
            ScaleMode.Crop => Stretch.UniformToFill,
            _ => Stretch.Uniform,
        };
        PreviewBadge.Text = before || size is null
            ? $"Original · {info.DisplayWidth} × {info.DisplayHeight}"
            : $"Preview · {size.Value.Width} × {size.Value.Height} · {Mode}";

        var fit = Ui.Fit(PreviewHost.ActualWidth, PreviewHost.ActualHeight, aspect);
        if (fit.IsEmpty) return;
        Player.Width = fit.Width;
        Player.Height = fit.Height;
    }

    // =====================================================================
    // Processing
    // =====================================================================

    async void ResizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Tools is null || IsBusy || TargetSize() is not { } size) return;

        var todo = _items.Where(i => i.Status == QueueStatus.Ready && i.Info is not null).ToList();
        if (todo.Count == 0) return;

        Player.Pause();
        _jobCts = new CancellationTokenSource();
        AddMoreButton.IsEnabled = false;
        ShowPanel(ProgressPanel);
        ProgressPanel.Start();

        var codec = CodecChoice.H264;
        var options = new CompressOptions
        {
            Mode = Quality,
            Codec = codec,
            UseHardware = _state.Settings.UseHardware && _state.Encoders.VendorFor(codec) != HwVendor.None,
            ResizeTo = size,
            ResizeMode = Mode,
        };
        bool skipSame = SkipSameSwitch.IsChecked == true;
        bool replace = _state.Settings.ReplaceOriginal;
        bool cancelled = false;

        for (int i = 0; i < todo.Count && !cancelled; i++)
        {
            var item = todo[i];
            if (!_items.Contains(item)) continue; // removed from the queue while waiting
            var info = item.Info!;
            ProgressPanel.Title = todo.Count == 1 ? "Resizing" : $"Resizing {i + 1} of {todo.Count}";

            if (skipSame && AlreadyTargetSize(info, size))
            {
                item.Status = QueueStatus.Skipped;
                item.StatusText = $"Skipped · already {size.Width}×{size.Height}";
                continue;
            }

            EncodePlan plan;
            try
            {
                var output = _state.Settings.BuildOutputPath(item.Path, $"_{size.Width}x{size.Height}", ".mp4");
                plan = VideoEngine.CreatePlan(info, options, _state.Encoders, output,
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
            ProgressPanel.BeginStage($"{item.FileName}\n{plan.EncoderLabel} · {plan.Width}×{plan.Height} · {Mode}", plan.Passes.Count);

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
                Usage.Export("resize");
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
        UpdatePreview();

        if (cancelled)
        {
            ShowPanel(SettingsPanel);
            return;
        }
        ShowDone(todo);
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

    void ShowDone(List<QueueItem> processed)
    {
        int done = processed.Count(i => i.Status == QueueStatus.Done);
        int skipped = processed.Count(i => i.Status == QueueStatus.Skipped);
        int failed = processed.Count(i => i.Status == QueueStatus.Failed);

        DoneTitle.Text = failed == 0 ? "All done" : done > 0 ? "Finished with errors" : "Resize failed";
        var parts = new List<string> { done == 1 ? "1 video resized" : $"{done} videos resized" };
        if (skipped > 0) parts.Add($"{skipped} skipped");
        if (failed > 0) parts.Add($"{failed} failed (hover over it in the queue for details)");
        DoneText.Text = string.Join(" · ", parts) + ".";
        ShowPanel(DonePanel);
    }

    void Progress_CancelRequested(object? sender, EventArgs e) => _jobCts?.Cancel();

    void ShowInFolder_Click(object sender, RoutedEventArgs e) => Ui.ShowInExplorer(_lastOutput);

    void ResizeMore_Click(object sender, RoutedEventArgs e)
    {
        ClearFinished_Click(sender, e);
        ShowPanel(SettingsPanel);
        if (_items.Count == 0) AddVideos_Click(sender, e);
    }

    void BackToSettings_Click(object sender, RoutedEventArgs e) => ShowPanel(SettingsPanel);

    void ShowPanel(FrameworkElement panel) => Ui.ShowOnly(panel, SettingsPanel, ProgressPanel, DonePanel);
}
