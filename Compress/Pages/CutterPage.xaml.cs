using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Compress.Controls;
using Compress.Core;

namespace Compress.Pages;

/// <summary>Cut a video to a shorter length: timeline with thumbnails, draggable start/end handles, lossless or precise export.</summary>
public partial class CutterPage : UserControl, IToolPage
{
    static readonly TimeSpan MinLength = TimeSpan.FromSeconds(0.1);
    const double HandleWidth = 16;

    AppState _state = null!;
    bool _loadingVideo, _scrubbing;
    VideoInfo? _video;
    string? _resultPath, _jobOutputPath, _posterFile;
    CancellationTokenSource? _jobCts;
    TimeSpan _start, _end;
    int _filmstripGeneration;

    // Zoom: the timeline shows [_viewStart, _viewStart + _viewLength] seconds of the video.
    double _viewStart, _viewLength;
    bool _draggingHandle, _navDragging;
    double _navGrab;

    // Filmstrip thumbnails: level k splits the video into _thumbBase * 2^k slots; deeper levels load while zooming in.
    readonly Dictionary<(int Level, int Index), Image> _thumbs = new();
    readonly HashSet<(int Level, int Index)> _thumbsPending = new();
    readonly DispatcherTimer _thumbDebounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly SemaphoreSlim _thumbGate = new(4);
    int _thumbBase = 12;

    // Lossless cuts start at the keyframe before the chosen start; looked up shortly after the selection settles.
    readonly DispatcherTimer _keyframeDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    CancellationTokenSource? _keyframeCts;
    TimeSpan? _keyframeStart;
    TimeSpan _keyframeFor = TimeSpan.MinValue;

    /// <summary>Asks the main window to open a file on the Compress page.</summary>
    public event Action<string>? SendToCompressRequested;

    public CutterPage()
    {
        InitializeComponent();
        _keyframeDebounce.Tick += async (_, _) =>
        {
            _keyframeDebounce.Stop();
            await UpdateKeyframeAsync();
        };
        _thumbDebounce.Tick += (_, _) =>
        {
            _thumbDebounce.Stop();
            LoadVisibleThumbs();
        };
        Ui.MakeDropdown(ZoomPopup, ZoomMenuButton, FillZoomMenu);
    }

    public void Initialize(AppState state)
    {
        _state = state;
        state.EngineChanged += (_, _) => UpdateEstimate();
        SaveMode.Initialize(state);
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
        if (EditorView.Visibility != Visibility.Visible || Keyboard.Modifiers != ModifierKeys.None || _video is null) return false;
        switch (e.Key)
        {
            case Key.Space: Player.TogglePlay(); return true;
            case Key.I: SetStart(Player.Position, seek: false); return true;
            case Key.O: SetEnd(Player.Position, seek: false); return true;
            case Key.Left or Key.Right when Keyboard.FocusedElement is not RadioButton:
                Player.SeekBy(e.Key == Key.Left ? -1 : 1);
                return true;
            case Key.OemComma: Player.StepFrames(-1, _video.Fps); return true;
            case Key.OemPeriod: Player.StepFrames(1, _video.Fps); return true;
            case Key.OemPlus or Key.Add: ZoomBy(2, PlayheadOrCenterX()); return true;
            case Key.OemMinus or Key.Subtract: ZoomBy(0.5, PlayheadOrCenterX()); return true;
            case Key.D0 or Key.NumPad0: SetView(0, _video.Duration.TotalSeconds); return true;
            default: return false;
        }
    }

    // =====================================================================
    // Loading
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
            _start = TimeSpan.Zero;
            _end = info.Duration;
            _keyframeStart = null;
            _keyframeFor = TimeSpan.MinValue;
            _viewStart = 0;
            _viewLength = info.Duration.TotalSeconds;

            FileTitle.Text = $"{Path.GetFileName(path)}  ·  {Format.Size(info.SizeBytes)}";
            FileMeta.Text = $"{info.DisplayWidth} × {info.DisplayHeight}  ·  {Format.Fps(info.Fps)} fps  ·  {Format.Codec(info.VideoCodec)}  ·  {Format.Clock(info.Duration)}";
            RemoveAudioSwitch.IsEnabled = info.HasAudio;
            BuildTrackRows(info);

            ShowPanel(SettingsPanel);
            EmptyView.Visibility = Visibility.Collapsed;
            EditorView.Visibility = Visibility.Visible;

            Player.Open(path, TimeSpan.Zero, info.Duration);
            SelectionChanged();
            _ = Dispatcher.BeginInvoke(() =>
            {
                LayoutVideo();
                ResetFilmstrip();
                UpdateZoomUi();
            }, DispatcherPriority.Loaded);
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

    /// <summary>Drops all thumbnails and starts filling the timeline for the current video.</summary>
    void ResetFilmstrip()
    {
        if (_video is null) return;
        _filmstripGeneration++;
        _thumbs.Clear();
        _thumbsPending.Clear();
        FilmCanvas.Children.Clear();
        FilmLoading.Visibility = Visibility.Visible;

        double thumbWidth = 64 * Ui.Aspect(_video);
        _thumbBase = Math.Clamp((int)Math.Round(Math.Max(FilmHost.ActualWidth, 400) / thumbWidth), 4, 24);
        LoadVisibleThumbs();
    }

    /// <summary>Thumbnail level for the current zoom: one level deeper each time the zoom doubles.</summary>
    int ThumbLevel => Math.Clamp((int)Math.Floor(Math.Log2(Zoom + 1e-9)), 0, 14);

    double SlotSeconds(int level) => _video!.Duration.TotalSeconds / ((long)_thumbBase << level);

    bool ThumbWanted((int Level, int Index) key)
    {
        if (_video is null || key.Level != ThumbLevel) return false;
        double slot = SlotSeconds(key.Level);
        return (key.Index + 2) * slot >= _viewStart && (key.Index - 1) * slot <= _viewStart + ViewLength;
    }

    /// <summary>Loads the thumbnails the timeline currently shows (plus one on each side), four at a time.</summary>
    void LoadVisibleThumbs()
    {
        if (_video is null || _state.Tools is null || TimelineWidth <= 0) return;
        int level = ThumbLevel;
        double slot = SlotSeconds(level);
        int first = Math.Max(0, (int)Math.Floor(_viewStart / slot) - 1);
        int last = (int)Math.Min(((long)_thumbBase << level) - 1, Math.Ceiling((_viewStart + ViewLength) / slot));

        // Keep memory in check after a lot of zooming around: forget other deep levels.
        if (_thumbs.Count > 400)
            foreach (var key in _thumbs.Keys.Where(k => k.Level != level && k.Level != 0).ToList())
            {
                FilmCanvas.Children.Remove(_thumbs[key]);
                _thumbs.Remove(key);
            }

        for (int i = first; i <= last; i++)
        {
            var key = (level, i);
            if (_thumbs.ContainsKey(key) || !_thumbsPending.Add(key)) continue;
            _ = LoadThumbAsync(_video, key, _filmstripGeneration);
        }
    }

    async Task LoadThumbAsync(VideoInfo info, (int Level, int Index) key, int generation)
    {
        await _thumbGate.WaitAsync();
        try
        {
            if (generation != _filmstripGeneration || !ThumbWanted(key)) return;
            double at = (key.Index + 0.5) * SlotSeconds(key.Level);
            var file = await VideoEngine.ExtractFrameAsync(_state.Tools!, info.Path, at, "scale=-2:96");
            if (file is null) return;
            var bmp = Ui.LoadBitmap(file);
            Ui.TryDelete(file);
            if (generation != _filmstripGeneration) return;

            var img = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
            Panel.SetZIndex(img, key.Level);
            _thumbs[key] = img;
            FilmCanvas.Children.Add(img);
            PositionThumb(img, key);
            FilmLoading.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _thumbGate.Release();
            if (generation == _filmstripGeneration) _thumbsPending.Remove(key);
        }
    }

    /// <summary>Places a thumbnail over its slot. Coarser levels stay visible underneath until the finer ones load.</summary>
    void PositionThumb(Image img, (int Level, int Index) key)
    {
        double slot = SlotSeconds(key.Level), w = TimelineWidth, h = FilmHost.ActualHeight;
        double x1 = ToX(TimeSpan.FromSeconds(key.Index * slot)), x2 = ToX(TimeSpan.FromSeconds((key.Index + 1) * slot));
        int level = ThumbLevel;
        bool visible = key.Level <= level && key.Level >= level - 3 && x2 >= 0 && x1 <= w;
        img.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        // Clamp to the visible area so very coarse thumbnails don't become huge.
        double left = Math.Max(x1, -1), right = Math.Min(x2, w + 1);
        Canvas.SetLeft(img, left);
        img.Width = Math.Max(1, right - left + 0.5);
        img.Height = h;
        img.Clip = new RectangleGeometry(new Rect(0, 0, img.Width, h));
    }

    // =====================================================================
    // Layout
    // =====================================================================

    void PlayerHost_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutVideo();

    void LayoutVideo()
    {
        var size = Ui.Fit(PlayerHost.ActualWidth, PlayerHost.ActualHeight, _video is null ? 16.0 / 9 : Ui.Aspect(_video));
        if (size.IsEmpty) return;
        Player.Width = size.Width;
        Player.Height = size.Height;
    }

    void FilmHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        FilmClip.Clip = new RectangleGeometry(new Rect(e.NewSize), 10, 10);
        // Handles may stick out by their width; everything further outside the zoomed view is hidden.
        Overlay.Clip = new RectangleGeometry(new Rect(-HandleWidth, -20, e.NewSize.Width + 2 * HandleWidth, 120));
        UpdateTimelineVisuals();
    }

    double TimelineWidth => FilmHost.ActualWidth;

    double Duration => _video?.Duration.TotalSeconds ?? 0;

    double ViewLength => _viewLength > 0 ? _viewLength : Duration;

    double Zoom => ViewLength > 0 ? Duration / ViewLength : 1;

    bool IsZoomed => Zoom > 1.001;

    /// <summary>Shortest visible stretch: about 20 frames, at least half a second.</summary>
    double MinViewLength => Math.Min(Duration, Math.Max(0.5, 20 / (_video?.Fps > 0 ? _video.Fps : 30)));

    double ToX(TimeSpan t) =>
        _video is null || ViewLength <= 0 ? 0 : (t.TotalSeconds - _viewStart) / ViewLength * TimelineWidth;

    /// <summary>Time under an x position. Past the edges of a zoomed view it keeps going (so dragging there scrolls).</summary>
    TimeSpan ToTime(double x) =>
        _video is null || TimelineWidth <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Clamp(_viewStart + x / TimelineWidth * ViewLength, 0, Duration));

    void UpdateTimelineVisuals()
    {
        if (_video is null) return;
        double w = TimelineWidth, x1 = ToX(_start), x2 = ToX(_end);
        double c1 = Math.Clamp(x1, 0, w), c2 = Math.Clamp(x2, 0, w);

        Canvas.SetLeft(DimLeft, 0);
        DimLeft.Width = c1;
        Canvas.SetLeft(DimRight, c2);
        DimRight.Width = Math.Max(0, w - c2);
        Canvas.SetLeft(Selection, Math.Clamp(x1, -4, w + 4));
        Selection.Width = Math.Max(0, Math.Clamp(x2, -4, w + 4) - Math.Clamp(x1, -4, w + 4));
        Canvas.SetLeft(StartHandle, Math.Clamp(x1, -4 * HandleWidth, w + 4 * HandleWidth) - HandleWidth);
        Canvas.SetLeft(EndHandle, Math.Clamp(x2, -4 * HandleWidth, w + 4 * HandleWidth));
        if (KeyframeMarker.Visibility == Visibility.Visible && _keyframeStart is { } kf)
            Canvas.SetLeft(KeyframeMarker, ToX(kf) - 1);
        foreach (var (key, img) in _thumbs) PositionThumb(img, key);
        UpdatePlayhead();
        UpdateZoomNav();
    }

    // ---------------------------------------------------------------- zoom

    /// <summary>Shows [start, start + length] seconds on the timeline (clamped to the video).</summary>
    void SetView(double start, double length)
    {
        if (_video is null) return;
        length = Math.Clamp(length, MinViewLength, Duration);
        _viewStart = Math.Clamp(start, 0, Duration - length);
        _viewLength = length;
        UpdateTimelineVisuals();
        UpdateZoomUi();
        _thumbDebounce.Stop();
        _thumbDebounce.Start();
    }

    /// <summary>Zooms by a factor while keeping the time under <paramref name="anchorX"/> in place.</summary>
    void ZoomBy(double factor, double anchorX)
    {
        if (_video is null || TimelineWidth <= 0) return;
        double rel = Math.Clamp(anchorX / TimelineWidth, 0, 1);
        double anchor = _viewStart + rel * ViewLength;
        double length = Math.Clamp(ViewLength / factor, MinViewLength, Duration);
        SetView(anchor - rel * length, length);
    }

    /// <summary>Zoom buttons and keys zoom around the playhead when it is on screen, otherwise around the middle.</summary>
    double PlayheadOrCenterX()
    {
        double x = ToX(Player.Position);
        return x >= 0 && x <= TimelineWidth ? x : TimelineWidth / 2;
    }

    /// <summary>Scrolls a zoomed timeline just far enough to show <paramref name="t"/>.</summary>
    void ScrollIntoView(TimeSpan t)
    {
        if (!IsZoomed) return;
        double s = t.TotalSeconds;
        if (s < _viewStart) SetView(s, ViewLength);
        else if (s > _viewStart + ViewLength) SetView(s - ViewLength, ViewLength);
    }

    void Timeline_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_video is null) return;
        e.Handled = true;
        double notches = e.Delta / 120.0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || sender == ZoomNav)
            SetView(_viewStart - notches * ViewLength * 0.15, ViewLength);
        else
            ZoomBy(Math.Pow(1.3, notches), e.GetPosition(FilmHost).X);
    }

    void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(2, PlayheadOrCenterX());

    void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(0.5, PlayheadOrCenterX());

    void ZoomFit_Click(object sender, RoutedEventArgs e) => SetView(0, Duration);

    static readonly int[] ZoomLevels = [1, 2, 4, 8, 16, 32, 64, 128, 256];

    /// <summary>Fills the zoom level menu (1×, 2×, 4×, … up to what the video allows).</summary>
    bool FillZoomMenu()
    {
        if (_video is null) return false;
        double max = MinViewLength > 0 ? Duration / MinViewLength : 1;
        ZoomOptions.Children.Clear();
        foreach (int level in ZoomLevels.Where(l => l == 1 || l <= max * 1.01))
        {
            var option = new Button
            {
                Style = (Style)FindResource("ZoomOption"),
                Tag = Math.Abs(Zoom - level) < 0.05 * level ? "current" : null,
                Content = new DockPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{level}×", MinWidth = 44 },
                        new TextBlock
                        {
                            Text = level == 1 ? "whole video" : Format.Clock(TimeSpan.FromSeconds(Duration / level)),
                            Foreground = (Brush)FindResource("Text3"), FontWeight = FontWeights.Normal, Margin = new Thickness(10, 0, 0, 0),
                            HorizontalAlignment = HorizontalAlignment.Right,
                        },
                    },
                },
            };
            option.Click += (_, _) =>
            {
                ZoomPopup.IsOpen = false;
                if (level == 1) SetView(0, Duration);
                else ZoomBy(level / Zoom, PlayheadOrCenterX());
            };
            ZoomOptions.Children.Add(option);
        }
        return true;
    }

    void UpdateZoomUi()
    {
        double zoom = Zoom;
        ZoomText.Text = (zoom < 10 ? zoom.ToString("0.#", CultureInfo.InvariantCulture) : zoom.ToString("0", CultureInfo.InvariantCulture)) + "×";
        ZoomFitButton.IsEnabled = IsZoomed;
        ZoomFitButton.Opacity = IsZoomed ? 1 : 0.4;
        ZoomNav.Visibility = IsZoomed ? Visibility.Visible : Visibility.Hidden;
        UpdateZoomNav();
    }

    void ZoomNav_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateZoomNav();

    /// <summary>The overview bar: the whole video, with the visible part as a thumb and the selection in green.</summary>
    void UpdateZoomNav()
    {
        double w = ZoomNav.ActualWidth;
        if (_video is null || w <= 0 || Duration <= 0) return;
        double thumbW = Math.Max(8, ViewLength / Duration * w);
        Canvas.SetLeft(ZoomNavThumb, Math.Min(_viewStart / Duration * w, w - thumbW));
        ZoomNavThumb.Width = thumbW;
        Canvas.SetLeft(ZoomNavSelection, _start.TotalSeconds / Duration * w);
        ZoomNavSelection.Width = Math.Max(2, (_end - _start).TotalSeconds / Duration * w);
    }

    void ZoomNav_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_video is null || !IsZoomed) return;
        double w = ZoomNav.ActualWidth, x = e.GetPosition(ZoomNav).X;
        double thumbLeft = _viewStart / Duration * w, thumbW = ViewLength / Duration * w;
        // Grab the thumb where it was clicked; a click beside it centers the view there.
        _navGrab = x >= thumbLeft && x <= thumbLeft + Math.Max(8, thumbW) ? x - thumbLeft : thumbW / 2;
        _navDragging = true;
        ZoomNav.CaptureMouse();
        ZoomNav_MouseMove(sender, e);
        e.Handled = true;
    }

    void ZoomNav_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_navDragging || ZoomNav.ActualWidth <= 0) return;
        SetView((e.GetPosition(ZoomNav).X - _navGrab) / ZoomNav.ActualWidth * Duration, ViewLength);
    }

    void ZoomNav_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _navDragging = false;
        ZoomNav.ReleaseMouseCapture();
    }

    bool KeyframeKnown => _keyframeStart is not null && _keyframeFor == _start;

    /// <summary>Start of the exported clip: the keyframe for a lossless cut, otherwise the chosen start.</summary>
    TimeSpan EffectiveStart => FastRadio.IsChecked == true && KeyframeKnown ? _keyframeStart!.Value : _start;

    async Task UpdateKeyframeAsync()
    {
        if (_video is null || FastRadio.IsChecked != true || _start <= TimeSpan.Zero)
        {
            ShowKeyframeNote();
            return;
        }
        if (_keyframeFor == _start) return;

        _keyframeCts?.Cancel();
        var cts = _keyframeCts = new CancellationTokenSource();
        var start = _start;
        TimeSpan? keyframe;
        try
        {
            keyframe = await VideoEngine.FindKeyframeAtOrBeforeAsync(_state.Tools!, _video.Path, start, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested || start != _start) return;
        _keyframeStart = keyframe;
        _keyframeFor = start;
        ShowKeyframeNote();
        UpdateEstimate();
    }

    void ShowKeyframeNote()
    {
        bool relevant = FastRadio.IsChecked == true && _start > TimeSpan.Zero && KeyframeKnown;
        var early = relevant ? _start - _keyframeStart!.Value : TimeSpan.Zero;
        if (!relevant)
        {
            KeyframeNote.Visibility = Visibility.Collapsed;
            KeyframeMarker.Visibility = Visibility.Collapsed;
            return;
        }

        KeyframeNote.Visibility = Visibility.Visible;
        if (early.TotalSeconds < 0.05)
        {
            KeyframeNote.Text = "Your start is on a keyframe, so the lossless cut is exact.";
            KeyframeNote.Foreground = (Brush)FindResource("Accent");
            KeyframeMarker.Visibility = Visibility.Collapsed;
        }
        else
        {
            KeyframeNote.Text = $"Starts at {Format.Clock(_keyframeStart!.Value)}, {early.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s before your start " +
                                "(nearest keyframe, orange line). Choose Precise for an exact start.";
            KeyframeNote.Foreground = (Brush)FindResource(early.TotalSeconds > 0.5 ? "Warning" : "Text2");
            KeyframeMarker.Visibility = Visibility.Visible;
            Canvas.SetLeft(KeyframeMarker, ToX(_keyframeStart.Value) - 1);
        }
    }

    void UpdatePlayhead()
    {
        if (_video is null) return;
        double x = ToX(Player.Position);
        Playhead.Visibility = x >= -1 && x <= TimelineWidth + 1 ? Visibility.Visible : Visibility.Hidden;
        Canvas.SetLeft(Playhead, x - Playhead.ActualWidth / 2);
    }

    void Player_PositionChanged(object? sender, EventArgs e)
    {
        // A zoomed timeline pages along with playback and keyboard seeking (not while dragging, which scrolls on its own).
        if (IsZoomed && !_scrubbing && !_draggingHandle && !_navDragging)
        {
            double s = Player.Position.TotalSeconds;
            if (s > _viewStart + ViewLength) SetView(s - ViewLength * 0.1, ViewLength);
            else if (s < _viewStart) SetView(s - ViewLength * 0.9, ViewLength);
        }
        UpdatePlayhead();
    }

    // =====================================================================
    // Selection
    // =====================================================================

    void SetStart(TimeSpan t, bool seek)
    {
        if (_video is null || IsBusy) return;
        _start = t < TimeSpan.Zero ? TimeSpan.Zero : t > _end - MinLength ? _end - MinLength : t;
        if (_start < TimeSpan.Zero) _start = TimeSpan.Zero;
        if (seek) Player.Position = _start;
        SelectionChanged();
    }

    void SetEnd(TimeSpan t, bool seek)
    {
        if (_video is null || IsBusy) return;
        _end = t > _video.Duration ? _video.Duration : t < _start + MinLength ? _start + MinLength : t;
        if (seek) Player.Position = _end;
        SelectionChanged();
    }

    bool IsFullLength => _video is null || (_start <= TimeSpan.Zero && _end >= _video.Duration);

    void SelectionChanged()
    {
        if (_video is null) return;
        StartBox.Text = Format.Clock(_start);
        EndBox.Text = Format.Clock(_end);
        var length = _end - _start;
        LengthText.Text = $"{length.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s";
        SummaryLength.Text = Format.Clock(length);
        ResetButton.Visibility = IsFullLength ? Visibility.Collapsed : Visibility.Visible;
        Player.SetRange(_start, _end >= _video.Duration ? null : _end, highlight: !IsFullLength);
        UpdateTimelineVisuals();
        ShowKeyframeNote();
        _keyframeDebounce.Stop();
        _keyframeDebounce.Start();
        UpdateEstimate();
    }

    void SetStart_Click(object sender, RoutedEventArgs e) => SetStart(Player.Position, seek: false);

    void SetEnd_Click(object sender, RoutedEventArgs e) => SetEnd(Player.Position, seek: false);

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_video is null) return;
        _start = TimeSpan.Zero;
        _end = _video.Duration;
        SelectionChanged();
    }

    void PlaySelection_Click(object sender, RoutedEventArgs e)
    {
        Player.Position = _start;
        Player.Play();
    }

    void Nudge_Click(object sender, RoutedEventArgs e)
    {
        if (_video is null) return;
        var parts = ((string)((Button)sender).Tag).Split(':');
        var frame = TimeSpan.FromSeconds(int.Parse(parts[1], CultureInfo.InvariantCulture) / (_video.Fps > 0 ? _video.Fps : 30));
        if (parts[0] == "start") SetStart(_start + frame, seek: true);
        else SetEnd(_end + frame, seek: true);
    }

    void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitTimeBox((TextBox)sender);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    void TimeBox_LostFocus(object sender, RoutedEventArgs e) => CommitTimeBox((TextBox)sender);

    void CommitTimeBox(TextBox box)
    {
        if (_video is null) return;
        if (!Format.TryParseTime(box.Text, out var t))
        {
            SelectionChanged(); // restore the previous value
            return;
        }
        if (box == StartBox) SetStart(t, seek: true);
        else SetEnd(t, seek: true);
    }

    // ---------------------------------------------------------------- timeline mouse

    void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_video is null) return;
        _scrubbing = true;
        Timeline.CaptureMouse();
        Player.Pause();
        Player.Position = ToTime(e.GetPosition(FilmHost).X);
    }

    void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_scrubbing) return;
        var t = ToTime(e.GetPosition(FilmHost).X);
        Player.Position = t;
        ScrollIntoView(t);
    }

    void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _scrubbing = false;
        Timeline.ReleaseMouseCapture();
    }

    void Handle_DragStarted(object sender, DragStartedEventArgs e)
    {
        _draggingHandle = true;
        Player.Pause();
    }

    void Handle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var t = ToTime(Mouse.GetPosition(FilmHost).X);
        if (sender == StartHandle) SetStart(t, seek: true);
        else SetEnd(t, seek: true);
        ScrollIntoView(sender == StartHandle ? _start : _end);
    }

    void Handle_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _draggingHandle = false;
        UpdateEstimate();
    }

    // =====================================================================
    // Audio tracks
    // =====================================================================

    readonly List<double> _trackVolumes = [];
    readonly List<bool> _trackMuted = [];
    readonly List<(Button Mute, Slider Volume, TextBlock Percent)> _trackControls = [];

    /// <summary>Volume per track for the export, or null when the video has fewer than two tracks.</summary>
    IReadOnlyList<double>? TrackVolumes => _video is { AudioTracks: >= 2 } && _trackVolumes.Count >= 2
        ? _trackVolumes.Select((v, i) => _trackMuted[i] ? 0 : v).ToList()
        : null;

    /// <summary>A readable name per track: the title stored in the file, NVIDIA's fixed order, or just a number.</summary>
    static string TrackName(VideoInfo info, int index)
    {
        var title = index < info.AudioTrackTitles.Count ? info.AudioTrackTitles[index].Trim() : "";
        if (title.Length > 0 && !title.Equals("SoundHandle", StringComparison.OrdinalIgnoreCase)) return title;
        // The NVIDIA app's "separate tracks" option writes game/PC sound first, then the microphone.
        if (info.AudioTracks == 2 && Path.GetFileName(info.Path).Contains(".DVR", StringComparison.OrdinalIgnoreCase))
            return index == 0 ? "Game & PC" : "Microphone";
        return $"Track {index + 1}";
    }

    void BuildTrackRows(VideoInfo info)
    {
        TrackRows.Children.Clear();
        _trackControls.Clear();
        _trackVolumes.Clear();
        _trackMuted.Clear();
        if (info.AudioTracks < 2)
        {
            UpdateTracksUi();
            return;
        }

        // Volumes are remembered for the next clip (same recording setup); mute is per clip.
        var saved = _state.Settings.TrackVolumes;
        for (int i = 0; i < info.AudioTracks; i++)
        {
            int track = i;
            _trackVolumes.Add(i < saved.Count ? Math.Clamp(saved[i], 0, 2) : 1);
            _trackMuted.Add(false);

            var mute = new Button { Style = (Style)FindResource("RowIconButton"), Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = TrackName(info, i), FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var volume = new Slider
            {
                Style = (Style)FindResource("MediaSlider"), Minimum = 0, Maximum = 2, Value = _trackVolumes[i],
                SmallChange = 0.05, LargeChange = 0.1, Margin = new Thickness(0, 4, 0, 0),
                ToolTip = "Volume of this track (double-click: 100%)",
            };
            var percent = new TextBlock
            {
                FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Text2"),
                MinWidth = 42, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };

            mute.Click += (_, _) =>
            {
                _trackMuted[track] = !_trackMuted[track];
                TrackChanged(track);
            };
            volume.ValueChanged += (_, e) =>
            {
                // Snap to 100% near the middle so "unchanged" is easy to hit.
                double v = Math.Abs(e.NewValue - 1) < 0.04 ? 1 : Math.Round(e.NewValue, 2);
                _trackVolumes[track] = v;
                if (v > 0) _trackMuted[track] = false;
                TrackChanged(track);
            };
            volume.MouseDoubleClick += (_, _) => volume.Value = 1;

            var text = new StackPanel { Margin = new Thickness(8, 0, 10, 0) };
            text.Children.Add(name);
            text.Children.Add(volume);
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 1);
            Grid.SetColumn(percent, 2);
            row.Children.Add(mute);
            row.Children.Add(text);
            row.Children.Add(percent);
            TrackRows.Children.Add(row);
            _trackControls.Add((mute, volume, percent));
            UpdateTrackRow(i);
        }
        UpdateTracksUi();
    }

    void TrackChanged(int track)
    {
        UpdateTrackRow(track);
        UpdateTracksUi();
        _state.Settings.TrackVolumes = [.. _trackVolumes];
        _state.Settings.Save();
        UpdateEstimate();
    }

    void UpdateTrackRow(int track)
    {
        var (mute, _, percent) = _trackControls[track];
        bool muted = _trackMuted[track] || _trackVolumes[track] <= 0;
        mute.Content = muted ? "\uE74F" : "\uE767";
        mute.Foreground = (Brush)FindResource(muted ? "Danger" : "Text2");
        mute.ToolTip = muted ? "Unmute" : "Mute";
        percent.Text = muted ? "Off" : $"{_trackVolumes[track] * 100:0}%";
        percent.Opacity = muted ? 0.6 : 1;
        Player.SetTrackVolume(track, muted ? 0 : _trackVolumes[track]);
    }

    void UpdateTracksUi()
    {
        bool removeAudio = RemoveAudioSwitch.IsChecked == true;
        TracksPanel.Visibility = _trackControls.Count >= 2 && !removeAudio ? Visibility.Visible : Visibility.Collapsed;
        TracksNote.Visibility = TrackVolumes is { } v && v.All(x => x <= 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    // =====================================================================
    // Export
    // =====================================================================

    void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateTracksUi();
        ShowKeyframeNote();
        _keyframeDebounce.Stop();
        _keyframeDebounce.Start();
        UpdateEstimate();
    }

    TimeSpan? EndOrNull => _video is not null && _end >= _video.Duration ? null : _end;

    EncodePlan CreatePlan(VideoInfo video, string workDir)
    {
        bool removeAudio = RemoveAudioSwitch.IsChecked == true && RemoveAudioSwitch.IsEnabled;
        if (FastRadio.IsChecked == true)
        {
            var output = _state.Settings.BuildOutputPath(video.Path, "_cut", VideoEngine.CopyCutExtension(video.Path));
            return VideoEngine.CreateCopyCutPlan(video, _start, EndOrNull, removeAudio, output, workDir, TrackVolumes);
        }

        var codec = video.VideoCodec == "hevc" ? CodecChoice.H265 : CodecChoice.H264;
        var options = new CompressOptions
        {
            Mode = QualityMode.Source,
            Codec = codec,
            RemoveAudio = removeAudio,
            UseHardware = _state.Settings.UseHardware && _state.Encoders.VendorFor(codec) != HwVendor.None,
            TrimStart = _start,
            TrimEnd = EndOrNull,
            TrackVolumes = TrackVolumes,
        };
        return VideoEngine.CreatePlan(video, options, _state.Encoders, _state.Settings.BuildOutputPath(video.Path, "_cut", ".mp4"), workDir);
    }

    void UpdateEstimate()
    {
        if (_video is null) return;
        try
        {
            var plan = CreatePlan(_video, Path.GetTempPath());
            long estimate = plan.EstimatedBytes;
            if (FastRadio.IsChecked == true && _video.Duration > TimeSpan.Zero)
                estimate = (long)(_video.SizeBytes * ((_end - EffectiveStart).TotalSeconds / _video.Duration.TotalSeconds));
            SummarySize.Text = "≈ " + Format.Size(estimate);
            CutButton.IsEnabled = true;
        }
        catch (InvalidOperationException)
        {
            SummarySize.Text = "—";
            CutButton.IsEnabled = false;
        }
    }

    async void CutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_video is null || _state.Tools is null || IsBusy) return;

        var video = _video;
        EncodePlan plan;
        try
        {
            plan = CreatePlan(video, Path.Combine(Path.GetTempPath(), "compress-" + Guid.NewGuid().ToString("N")));
        }
        catch (InvalidOperationException ex)
        {
            ShowError("Can't cut with these settings", ex.Message);
            return;
        }

        Player.Pause();
        Timeline.IsEnabled = false;
        bool replace = _state.Settings.ReplaceOriginal;
        string? reopen = null;
        _jobCts = new CancellationTokenSource();
        _jobOutputPath = plan.OutputPath;
        ShowPanel(ProgressPanel);
        ProgressPanel.Start();
        ProgressPanel.BeginStage($"{plan.EncoderLabel} · {Format.Clock(plan.Duration)}", plan.Passes.Count);

        try
        {
            var progress = new Progress<EncodeProgress>(p =>
            {
                if (_jobCts is not null) ProgressPanel.Report(p);
            });
            await VideoEngine.RunAsync(_state.Tools, plan, progress, _jobCts.Token);
            ProgressPanel.Stop();
            Usage.Export("cutter");

            string resultPath = plan.OutputPath;
            string? note = null;
            if (replace)
            {
                // The player holds the original open; release it, swap the files, then edit the new clip.
                Player.Close();
                (resultPath, note) = await OutputFiles.ReplaceOriginalAsync(video.Path, plan.OutputPath);
                PreviewAudio.Forget(video.Path);
                PreviewProxy.Forget(video.Path);
                reopen = note is null ? resultPath : video.Path;
            }
            await ShowResultAsync(plan, resultPath, note, ProgressPanel.Elapsed);
        }
        catch (OperationCanceledException)
        {
            ShowPanel(SettingsPanel);
        }
        catch (Exception ex)
        {
            string message = ex.Message;
            if (plan.IsHardware) message += "\n\nTip: turn off GPU acceleration on the Compress page and try again.";
            else if (FastRadio.IsChecked == true) message += "\n\nTip: try Precise mode, which works with every format.";
            ShowError("Cut failed", message);
        }
        finally
        {
            ProgressPanel.Stop();
            _jobCts.Dispose();
            _jobCts = null;
            _jobOutputPath = null;
            Timeline.IsEnabled = true;
        }

        if (reopen is not null)
        {
            var result = _resultPath;
            await LoadVideoAsync(reopen);
            _resultPath = result;
            ShowPanel(DonePanel);
        }
    }

    async Task ShowResultAsync(EncodePlan plan, string resultPath, string? note, TimeSpan elapsed)
    {
        _resultPath = resultPath;
        var size = new FileInfo(resultPath).Length;
        var length = plan.Duration;
        try
        {
            length = (await VideoEngine.ProbeAsync(_state.Tools!, resultPath)).Duration;
        }
        catch { /* fall back to the planned length */ }

        DoneFileText.Text = note is not null ? $"Saved as {Path.GetFileName(resultPath)}. {note}"
            : resultPath != plan.OutputPath ? $"Replaced {Path.GetFileName(resultPath)}. The original is in the Recycle Bin."
            : $"Saved as {Path.GetFileName(resultPath)}";
        DoneLength.Text = $"{length.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s";
        DoneSize.Text = Format.Size(size);
        DoneDetails.Text = $"{plan.EncoderLabel} · {plan.Width}×{plan.Height} · took {Format.Time(elapsed)}";
        CopyFileIcon.Text = "\uE8C8";
        CopyFileText.Text = "Copy file (paste into Discord)";
        ShowPanel(DonePanel);
        Ui.NotifyFinished(this);
    }

    void ShowInFolder_Click(object sender, RoutedEventArgs e) => Ui.ShowInExplorer(_resultPath);

    void CopyFile_Click(object sender, RoutedEventArgs e)
    {
        bool ok = Ui.CopyFileToClipboard(_resultPath);
        CopyFileIcon.Text = ok ? "\uE73E" : "\uE783";
        CopyFileText.Text = ok ? "Copied. Paste it with Ctrl+V" : "Could not copy the file";
    }

    void SendToCompress_Click(object sender, RoutedEventArgs e)
    {
        if (_resultPath is not null && File.Exists(_resultPath)) SendToCompressRequested?.Invoke(_resultPath);
    }

    void Progress_CancelRequested(object? sender, EventArgs e) => _jobCts?.Cancel();

    void BackToSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(SettingsPanel);
        UpdateEstimate();
    }

    void ShowPanel(FrameworkElement panel) => Ui.ShowOnly(panel, SettingsPanel, ProgressPanel, DonePanel, ErrorPanel);

    void ShowError(string title, string message)
    {
        ErrorTitle.Text = title;
        ErrorText.Text = message;
        ShowPanel(ErrorPanel);
    }
}
