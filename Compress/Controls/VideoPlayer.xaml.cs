using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Compress.Core;

namespace Compress.Controls;

/// <summary>
/// Rounded video card with play/pause, seek bar and volume. Playback can be confined to a range
/// (the trim selection), which is also highlighted on the seek bar.
/// </summary>
public partial class VideoPlayer : UserControl
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    bool _isPlaying, _seekDragging, _updatingSeek, _mouseOver, _muted;
    TimeSpan _pendingPosition, _fallbackDuration;
    TimeSpan _rangeStart;
    TimeSpan? _rangeEnd;
    bool _showRange;

    // Clips with game and mic on separate tracks: Windows plays only one, so every track runs as its own
    // player alongside the picture (which is muted then), each with its own volume.
    readonly List<MediaPlayer> _tracks = [];
    readonly List<double> _trackVolumes = [];
    int _openGeneration;
    string? _path;

    /// <summary>
    /// Returns a file to play instead of a video (an upright copy of a sideways-stored phone video), or null to play it
    /// as it is. Set once FFmpeg is ready.
    /// </summary>
    public static Func<string, Task<string?>>? PreviewSourceProvider { get; set; }

    bool UseTracks => _tracks.Count > 0;

    /// <summary>Returns one WAV per audio track of a video, or null when it has just one. Set once FFmpeg is ready.</summary>
    public static Func<string, Task<IReadOnlyList<string>?>>? TrackAudioProvider { get; set; }

    /// <summary>Raised when the separate track audio is ready (or gone); <see cref="AudioTrackCount"/> tells how many.</summary>
    public event EventHandler? AudioTracksChanged;

    /// <summary>Number of separately playing audio tracks (0 = the video's own sound).</summary>
    public int AudioTrackCount => _tracks.Count;

    /// <summary>Volume of one audio track in the preview: 0 = muted, 1 = unchanged, up to 2 (limited by the player).</summary>
    public void SetTrackVolume(int track, double volume)
    {
        while (_trackVolumes.Count <= track) _trackVolumes.Add(1);
        _trackVolumes[track] = Math.Max(0, volume);
        ApplyVolume();
    }

    public event EventHandler? MediaOpened;
    /// <summary>Raised while playing and after every seek.</summary>
    public event EventHandler? PositionChanged;

    public VideoPlayer()
    {
        InitializeComponent();

        _timer.Tick += (_, _) => UpdatePosition();
        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _seekDragging = true));
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _seekDragging = false;
            SeekTo(SeekSlider.Value);
        }));
        SeekSlider.ValueChanged += (_, e) =>
        {
            if (_updatingSeek) return;
            if (_seekDragging) UpdateTimeText(TimeSpan.FromSeconds(e.NewValue));
            else SeekTo(e.NewValue);
        };
        ApplyVolume();
    }

    public ImageSource? Poster
    {
        get => PosterImage.Source;
        set => PosterImage.Source = value;
    }

    /// <summary>How the picture fills the card: Uniform (default), Fill = stretched, UniformToFill = cropped.</summary>
    public Stretch VideoStretch
    {
        get => Media.Stretch;
        set
        {
            Media.Stretch = value;
            PosterImage.Stretch = value;
        }
    }

    /// <summary>Content drawn over the video and under the controls, e.g. draggable crop boxes. Hides the big play button.</summary>
    public UIElement? Overlay
    {
        get => OverlayHost.Content as UIElement;
        set
        {
            OverlayHost.Content = value;
            UpdatePlayState();
        }
    }

    /// <summary>Height of the control bar when <see cref="ControlsBelow"/> is set.</summary>
    public const double DockedControlsHeight = 74;

    /// <summary>Puts the controls in a bar under the video instead of fading them over it, so nothing covers the picture.</summary>
    public bool ControlsBelow
    {
        get => Grid.GetRow(Controls) == 1;
        set
        {
            Grid.SetRow(Controls, value ? 1 : 0);
            ControlsRow.Height = value ? new GridLength(DockedControlsHeight) : new GridLength(0);
            Controls.Background = value ? new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)) : (Brush)Resources["ControlsGradient"];
            ControlsPanel.Margin = value ? new Thickness(14, 6, 14, 8) : new Thickness(14, 36, 14, 10);
            FadeControls();
        }
    }

    /// <summary>Poster and video together, for live previews through a VisualBrush.</summary>
    public FrameworkElement VideoSurface => Surface;

    /// <summary>The video that was opened (the preview may play an upright copy of it, see <see cref="PreviewSourceProvider"/>).</summary>
    public string? CurrentPath => _path;
    public bool IsPlaying => _isPlaying;
    public bool CanPlay => Media.Source is not null && Controls.IsEnabled;
    public TimeSpan Duration => TimeSpan.FromSeconds(SeekSlider.Maximum);

    public TimeSpan Position
    {
        get => Media.Position;
        set => SeekTo(value.TotalSeconds);
    }

    public void Open(string path, TimeSpan position, TimeSpan fallbackDuration)
    {
        Pause();
        _pendingPosition = position;
        _fallbackDuration = fallbackDuration;
        PreviewUnavailable.Visibility = Visibility.Collapsed;
        StopTracks();
        _path = path;
        Media.Source = null;
        _ = OpenSourceAsync(path, ++_openGeneration);
    }

    async Task OpenSourceAsync(string path, int generation)
    {
        string source = path;
        if (PreviewSourceProvider is { } provider)
        {
            // Only sideways phone videos take a moment here (an upright copy is made); others return right away.
            var preparing = Task.Delay(300).ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                if (generation == _openGeneration && Media.Source is null) Preparing.Visibility = Visibility.Visible;
            }));
            try
            {
                source = await provider(path) ?? path;
            }
            catch { /* play the original */ }
        }
        if (generation != _openGeneration) return;
        Preparing.Visibility = Visibility.Collapsed;
        Media.Source = new Uri(source);
        // Play+Pause forces the first frame to render.
        Media.Play();
        Media.Pause();
        _ = LoadTracksAsync(path, generation);
    }

    async Task LoadTracksAsync(string path, int generation)
    {
        if (TrackAudioProvider is not { } provider) return;
        IReadOnlyList<string>? files;
        try
        {
            files = await provider(path);
        }
        catch
        {
            return;
        }
        if (files is null || generation != _openGeneration) return;
        foreach (var file in files)
        {
            var player = new MediaPlayer();
            player.Open(new Uri(file));
            player.Position = Media.Position;
            if (_isPlaying) player.Play();
            _tracks.Add(player);
        }
        ApplyVolume();
        AudioTracksChanged?.Invoke(this, EventArgs.Empty);
    }

    void StopTracks()
    {
        if (_tracks.Count == 0) return;
        foreach (var player in _tracks) player.Close();
        _tracks.Clear();
        ApplyVolume();
        AudioTracksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Volume and mute go to the separate tracks when there are any; the video's own (single) track is silenced then.
    /// Track volumes above 100% are only louder as far as the player allows (export applies them exactly).
    /// </summary>
    void ApplyVolume()
    {
        if (Media is null || VolumeSlider is null) return;
        Media.IsMuted = _muted || UseTracks;
        Media.Volume = VolumeSlider.Value;
        for (int i = 0; i < _tracks.Count; i++)
        {
            double track = i < _trackVolumes.Count ? _trackVolumes[i] : 1;
            _tracks[i].IsMuted = _muted || track <= 0;
            _tracks[i].Volume = Math.Min(1, VolumeSlider.Value * track);
        }
    }

    /// <summary>Keeps the separate tracks on the picture's clock.</summary>
    void SyncTracks(bool force = false)
    {
        foreach (var player in _tracks)
            if (force || Math.Abs((player.Position - Media.Position).TotalMilliseconds) > 120)
                player.Position = Media.Position;
    }

    public void Close()
    {
        Pause();
        _openGeneration++;
        _path = null;
        Preparing.Visibility = Visibility.Collapsed;
        StopTracks();
        Media.Close();
        Media.Source = null;
        PosterImage.Source = null;
    }

    /// <summary>Confines playback to [start, end] and optionally highlights that range on the seek bar.</summary>
    public void SetRange(TimeSpan start, TimeSpan? end, bool highlight)
    {
        _rangeStart = start;
        _rangeEnd = end;
        _showRange = highlight;
        UpdateRangeHighlight();
    }

    public void TogglePlay()
    {
        if (!CanPlay) return;
        if (_isPlaying) Pause();
        else Play();
    }

    public void Play()
    {
        if (!CanPlay) return;
        // Jump into the range when starting from outside it.
        if (Media.Position < _rangeStart || (_rangeEnd is { } end && Media.Position >= end - TimeSpan.FromMilliseconds(100)))
            Media.Position = _rangeStart;
        Media.Play();
        SyncTracks(force: true);
        foreach (var player in _tracks) player.Play();
        _isPlaying = true;
        _timer.Start();
        UpdatePlayState();
    }

    public void Pause()
    {
        Media.Pause();
        foreach (var player in _tracks) player.Pause();
        _isPlaying = false;
        _timer.Stop();
        UpdatePlayState();
    }

    public void SeekBy(double seconds) => SeekTo(Media.Position.TotalSeconds + seconds);

    public void StepFrames(int frames, double fps)
    {
        Pause();
        SeekBy(frames / (fps > 0 ? fps : 30));
    }

    void SeekTo(double seconds)
    {
        if (Media.Source is null) return;
        Media.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, SeekSlider.Maximum));
        SyncTracks(force: true);
        _updatingSeek = true;
        SeekSlider.Value = Media.Position.TotalSeconds;
        _updatingSeek = false;
        UpdateTimeText(Media.Position);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    void UpdatePosition()
    {
        if (_seekDragging) return;
        // Stop at the range end so the preview matches the export.
        if (_isPlaying && _rangeEnd is { } end && Media.Position >= end)
        {
            Pause();
            Media.Position = end;
        }
        if (_isPlaying) SyncTracks();
        _updatingSeek = true;
        SeekSlider.Value = Media.Position.TotalSeconds;
        _updatingSeek = false;
        UpdateTimeText(Media.Position);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    void UpdateTimeText(TimeSpan position) =>
        TimeText.Text = $"{Format.Time(position)} / {Format.Time(TimeSpan.FromSeconds(SeekSlider.Maximum))}";

    void UpdatePlayState()
    {
        PlayButton.Content = _isPlaying ? "\uE769" : "\uE768";
        BigPlay.Visibility = _isPlaying || OverlayHost.Content is not null ? Visibility.Collapsed : Visibility.Visible;
        FadeControls();
    }

    void FadeControls()
    {
        double target = !_isPlaying || _mouseOver || ControlsBelow ? 1 : 0;
        Controls.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(200)));
    }

    void UpdateRangeHighlight()
    {
        double max = SeekSlider.Maximum;
        if (!_showRange || max <= 0)
        {
            RangeHighlight.Visibility = Visibility.Collapsed;
            return;
        }
        const double thumb = 12;
        double usable = Math.Max(0, SeekSlider.ActualWidth - thumb);
        double x1 = Math.Clamp(_rangeStart.TotalSeconds / max, 0, 1) * usable;
        double x2 = Math.Clamp((_rangeEnd?.TotalSeconds ?? max) / max, 0, 1) * usable;
        RangeHighlight.Margin = new Thickness(thumb / 2 + x1, 0, 0, 0);
        RangeHighlight.Width = Math.Max(2, x2 - x1);
        RangeHighlight.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ events

    void Media_MediaOpened(object sender, RoutedEventArgs e)
    {
        double seconds = Media.NaturalDuration.HasTimeSpan
            ? Media.NaturalDuration.TimeSpan.TotalSeconds
            : _fallbackDuration.TotalSeconds;
        _updatingSeek = true;
        SeekSlider.Maximum = Math.Max(0.1, seconds);
        _updatingSeek = false;
        Controls.IsEnabled = true;
        SeekTo(_pendingPosition.TotalSeconds);
        UpdateRangeHighlight();
        MediaOpened?.Invoke(this, EventArgs.Empty);
    }

    void Media_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        Pause();
        PreviewUnavailable.Visibility = Visibility.Visible;
        Controls.IsEnabled = false;
    }

    void Media_MediaEnded(object sender, RoutedEventArgs e)
    {
        Pause();
        SeekTo(_rangeStart.TotalSeconds);
    }

    void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlay();

    void Surface_Click(object sender, MouseButtonEventArgs e) => TogglePlay();

    void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        ApplyVolume();
        MuteButton.Content = _muted ? "\uE74F" : "\uE767";
    }

    void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Media is null || VolumeSlider is null) return;
        ApplyVolume();
        if (_muted && e.NewValue > 0) MuteButton_Click(sender, e);
    }

    void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        _mouseOver = true;
        FadeControls();
    }

    void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        _mouseOver = false;
        FadeControls();
    }

    void Card_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ClipHost.Clip = new RectangleGeometry(new Rect(e.NewSize), 16, 16);

    void SeekSlider_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateRangeHighlight();
}
