using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Path = System.IO.Path;
using System.Windows.Threading;
using Compress.Controls;
using Compress.Core;

namespace Compress.Pages;

/// <summary>
/// Turns 16:9 gameplay into a 9:16 short: pick the gameplay crop and the HUD regions on the source,
/// arrange them on a live 1080×1920 preview and export with the GPU.
/// </summary>
public partial class ShortsPage : UserControl, IToolPage
{
    static readonly TimeSpan MinLength = TimeSpan.FromSeconds(0.5);
    static readonly TimeSpan ShortsMaxLength = TimeSpan.FromMinutes(3);
    const double SnapDistance = 7;   // screen pixels
    const double MinSourceSize = 0.015;
    const string BlankGame = "Custom";
    static readonly TimeSpan GroupEditsWithin = TimeSpan.FromMilliseconds(700);

    static readonly Color[] Palette =
    [
        Color.FromRgb(0xF8, 0x71, 0x71), Color.FromRgb(0x60, 0xA5, 0xFA), Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0xC0, 0x84, 0xFC),
        Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0x38, 0xBD, 0xF8), Color.FromRgb(0xFB, 0x92, 0x3C),
    ];
    static readonly Color AccentColor = Color.FromRgb(0x84, 0xCC, 0x16);
    static readonly string[] DefaultNames = ["Health", "Ammo", "Minimap", "Kill feed", "Hotbar", "Compass", "Score"];

    AppState _state = null!;
    VideoInfo? _video;
    ShortsLayout _layout = new();
    HudElement? _selected;
    bool _loadingVideo, _syncingUi;
    string? _resultPath, _jobOutputPath, _posterFile;
    CancellationTokenSource? _jobCts;
    TimeSpan _start, _end;

    // Undo: every drag, wheel burst or button press is one step.
    readonly Stack<ShortsLayout> _undo = new(), _redo = new();
    ShortsLayout? _dragSnapshot;
    bool _dragChanged;
    DateTime _lastGroupedEdit;
    object? _lastGroupedTarget;
    string? _renameSnapshotName;

    // Visuals. The source boxes live on a canvas over the player's video, under its controls.
    readonly Canvas SourceCanvas = new();
    RegionBox? _cropBox, _frameBox;
    readonly List<RegionBox> _sourceBoxes = [], _phoneBoxes = [];
    readonly List<Rectangle> _phoneLayers = [];
    Rectangle? _bgLayer, _bgDim, _mainLayer;
    readonly Dictionary<HudElement, TextBlock> _rowWarnings = [];
    List<UiZone> _covered = [];
    string _overlayKey = "";
    readonly DispatcherTimer _persistTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>Asks the main window to open a file on the Compress page.</summary>
    public event Action<string>? SendToCompressRequested;

    public ShortsPage()
    {
        InitializeComponent();
        _persistTimer.Tick += (_, _) => PersistNow();
        Player.ControlsBelow = true;
        Player.VideoStretch = Stretch.Fill;
        Player.Overlay = SourceCanvas;
        SourceCanvas.SizeChanged += (_, _) => UpdateSourceVisuals();
    }

    public void Initialize(AppState state)
    {
        _state = state;
        state.EngineChanged += (_, _) => UpdateEstimate();
        SafeZoneSwitch.IsChecked = state.Settings.ShortsSafeZones;
        _syncingUi = true;
        (state.Settings.ShortsPlatform switch { ShortsPlatform.Reels => PlatformReels, ShortsPlatform.Shorts => PlatformShorts, _ => PlatformTikTok }).IsChecked = true;
        _syncingUi = false;
        Limit60Switch.IsChecked = state.Settings.ShortsLimit60;
        if (state.Settings.ShortsLayout is { } last) _layout = last.Clone();
        BuildGameTiles();
        UpdateUndoButtons();
        InitSubtitles();
    }

    AppUi Platform => AppUi.For(_state.Settings.ShortsPlatform);

    double SW => _video!.DisplayWidth;
    double SH => _video!.DisplayHeight;

    // =====================================================================
    // IToolPage
    // =====================================================================

    public bool IsBusy => _jobCts is not null;
    public string? BusyOutputPath => _jobOutputPath;

    public void OnHidden() => Player.Pause();

    public void Shutdown()
    {
        _jobCts?.Cancel();
        _subCts?.Cancel();
        if (_persistTimer.IsEnabled) PersistNow();
        Player.Close();
        Ui.TryDelete(_posterFile);
    }

    public bool HandleKey(KeyEventArgs e)
    {
        if (EditorView.Visibility != Visibility.Visible || _video is null || IsBusy) return false;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.Z: Undo(); return true;
                case Key.Y: Redo(); return true;
                default: return false;
            }
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return false;
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
            case Key.Delete when _selected is not null: DeleteElement(_selected); return true;
            case Key.Escape when _selected is not null: Select(null); return true;
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
            if (info.DisplayWidth <= 0 || info.DisplayHeight <= 0) throw new InvalidDataException("The size of this video could not be read.");
            _video = info;
            _resultPath = null;
            _start = TimeSpan.Zero;
            _end = info.Duration;

            FileTitle.Text = $"{Path.GetFileName(path)}  ·  {Format.Size(info.SizeBytes)}";
            FileMeta.Text = $"{info.DisplayWidth} × {info.DisplayHeight}  ·  {Format.Fps(info.Fps)} fps  ·  {Format.Codec(info.VideoCodec)}  ·  {Format.Clock(info.Duration)}";
            RemoveAudioSwitch.IsEnabled = info.HasAudio;
            ResetSubtitles(info);

            // Keep the layout from the last short (same game, same HUD); start from the chosen game otherwise.
            if (_state.Settings.ShortsLayout is null && FindPreset(_state.Settings.ShortsGame) is { } preset)
                _layout = GamePresets.Instantiate(preset, SW, SH, Platform);
            else
                _layout.NormalizeCrop(SW, SH);
            if (_layout.Elements.Any(x => x.OutW <= 0)) _layout.AutoArrange(SW, SH, Platform);
            _selected = null;
            _undo.Clear();
            _redo.Clear();
            UpdateUndoButtons();

            ShowPanel(SettingsPanel);
            EmptyView.Visibility = Visibility.Collapsed;
            EditorView.Visibility = Visibility.Visible;

            Player.Open(path, TimeSpan.Zero, info.Duration);
            RebuildAll();
            SelectionChanged();
            _ = Dispatcher.BeginInvoke(LayoutStage, DispatcherPriority.Loaded);
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

    // =====================================================================
    // Stage layout
    // =====================================================================

    void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutStage();

    /// <summary>
    /// Sizes the source player and the phone preview: side by side on wide screens, stacked on tall ones
    /// (portrait monitors), always giving the 9:16 preview as much room as possible.
    /// </summary>
    void LayoutStage()
    {
        double w = Stage.ActualWidth, h = Stage.ActualHeight;
        if (w <= 0 || h <= 0) return;
        const double header = 30, sourceFooter = 56, phoneFooter = 34, gap = 28;
        double controls = VideoPlayer.DockedControlsHeight;
        double aspect = _video is null ? 16.0 / 9 : SW / SH;
        bool stacked = w / h < 0.9;

        double sourceW, videoH, phoneW, phoneH;
        if (stacked)
        {
            // Source across the full width (at most 40% of the height), the preview gets the rest.
            sourceW = w;
            videoH = Math.Min(sourceW / aspect, h * 0.4 - header - sourceFooter - controls);
            sourceW = videoH * aspect;
            phoneH = h - (videoH + header + sourceFooter + controls) - gap - header - phoneFooter;
            phoneW = phoneH * 9 / 16;
            if (phoneW > w)
            {
                phoneW = w;
                phoneH = phoneW * 16 / 9;
            }
        }
        else
        {
            phoneH = Math.Max(200, h - header - phoneFooter);
            phoneW = phoneH * 9 / 16;
            if (phoneW > w * 0.36)
            {
                phoneW = w * 0.36;
                phoneH = phoneW * 16 / 9;
            }
            sourceW = Math.Max(200, w - phoneW - gap);
            videoH = sourceW / aspect;
            double maxVideoH = Math.Max(100, h - header - sourceFooter - controls);
            if (videoH > maxVideoH)
            {
                videoH = maxVideoH;
                sourceW = videoH * aspect;
            }
        }

        Grid.SetColumn(PhoneColumn, stacked ? 0 : 1);
        Grid.SetRow(PhoneColumn, stacked ? 1 : 0);
        Grid.SetRowSpan(PhoneColumn, stacked ? 1 : 2);
        Grid.SetColumnSpan(PhoneColumn, stacked ? 2 : 1);
        Grid.SetRowSpan(SourceColumn, stacked ? 1 : 2);
        Grid.SetColumnSpan(SourceColumn, stacked ? 2 : 1);
        PhoneColumn.Margin = stacked ? new Thickness(0, gap, 0, 0) : new Thickness(gap, 0, 0, 0);
        PhoneColumn.VerticalAlignment = stacked ? VerticalAlignment.Top : VerticalAlignment.Center;
        SourceColumn.VerticalAlignment = stacked ? VerticalAlignment.Top : VerticalAlignment.Center;

        Player.Width = Math.Floor(Math.Max(100, sourceW));
        Player.Height = Math.Floor(Math.Max(56, videoH)) + controls;
        PhoneFrame.Width = Math.Floor(Math.Max(120, phoneW)) + 2;
        PhoneFrame.Height = Math.Floor(Math.Max(213, phoneH)) + 2;
    }

    void PhoneHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PhoneHost.Clip = new RectangleGeometry(new Rect(e.NewSize), 21, 21);
        PhoneCanvas.Clip = new RectangleGeometry(new Rect(e.NewSize));
        GuideV.Height = e.NewSize.Height;
        GuideH.Width = e.NewSize.Width;
        UpdatePhoneVisuals();
        DrawAppOverlay(force: true);
    }

    // =====================================================================
    // Visuals: source boxes, live preview layers, preview boxes
    // =====================================================================

    Color ColorOf(int index) => Palette[index % Palette.Length];

    VisualBrush LiveBrush(Rect viewbox, Stretch stretch = Stretch.Fill) => new(Player.VideoSurface)
    {
        ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
        Viewbox = viewbox,
        Stretch = stretch,
    };

    /// <summary>Recreates every box and layer; used when elements are added, removed or replaced.</summary>
    void RebuildAll()
    {
        SourceCanvas.Children.Clear();
        PhoneCanvas.Children.Clear();
        _sourceBoxes.Clear();
        _phoneBoxes.Clear();
        _phoneLayers.Clear();
        _cropBox = _frameBox = null;
        if (_video is null) return;

        // Preview layers, bottom to top: blurred background, dim, gameplay, HUD.
        _bgLayer = new Rectangle
        {
            Fill = LiveBrush(new Rect(0, 0, 1, 1), Stretch.UniformToFill),
            Effect = new BlurEffect { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
            IsHitTestVisible = false,
        };
        _bgDim = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0)), IsHitTestVisible = false };
        _mainLayer = new Rectangle { Fill = LiveBrush(new Rect(0, 0, 1, 1)), IsHitTestVisible = false };
        PhoneCanvas.Children.Add(_bgLayer);
        PhoneCanvas.Children.Add(_bgDim);
        PhoneCanvas.Children.Add(_mainLayer);
        foreach (var e in _layout.Elements)
        {
            var layer = new Rectangle { Fill = LiveBrush(new Rect(e.SrcX, e.SrcY, e.SrcW, e.SrcH)), IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(layer, BitmapScalingMode.HighQuality);
            _phoneLayers.Add(layer);
            PhoneCanvas.Children.Add(layer);
        }

        // Dragging the gameplay on the preview pans the crop (and moves the frame when it does not fill the screen).
        _frameBox = new RegionBox(null, AccentColor, "Gameplay", edgeHandles: false, cornerHandles: false, subtle: true);
        _frameBox.DragStarted += (_, _) => BeginEdit(null);
        _frameBox.Dragging += (_, _, d) => DragFrameOnPhone(d);
        _frameBox.DragCompleted += _ => EndEdit();
        _frameBox.MouseWheel += (_, e) => ZoomCrop(e);
        PhoneCanvas.Children.Add(_frameBox);

        _cropBox = new RegionBox(null, AccentColor, "Gameplay", edgeHandles: false, pinnedHandles: true);
        _cropBox.DragStarted += (_, _) => BeginEdit(null);
        _cropBox.Dragging += (_, h, d) => DragCrop(h, d);
        _cropBox.DragCompleted += _ => EndEdit();
        _cropBox.MouseWheel += (_, e) => ZoomCrop(e);
        SourceCanvas.Children.Add(_cropBox);

        for (int i = 0; i < _layout.Elements.Count; i++)
        {
            int index = i;
            var e = _layout.Elements[i];

            var src = new RegionBox(e, ColorOf(i), e.Name, edgeHandles: true);
            src.DragStarted += (_, _) => BeginEdit(_layout.Elements[index]);
            src.Dragging += (_, h, d) => DragSourceElement(index, h, d);
            src.DragCompleted += _ => EndEdit();
            _sourceBoxes.Add(src);
            SourceCanvas.Children.Add(src);

            var phone = new RegionBox(e, ColorOf(i), e.Name, edgeHandles: false, subtle: true);
            phone.DragStarted += (_, _) => BeginEdit(_layout.Elements[index]);
            phone.Dragging += (_, h, d) => DragPhoneElement(index, h, d);
            phone.DragCompleted += _ => EndEdit();
            phone.MouseWheel += (_, args) => ScaleElementByWheel(index, args);
            _phoneBoxes.Add(phone);
            PhoneCanvas.Children.Add(phone);
        }

        UpdateAllVisuals();
        RebuildElementList();
        SyncOptionUi();
        // The rows are new, so their warning icons need the coverage check again.
        UpdateCoverage();
    }

    void UpdateAllVisuals()
    {
        UpdateSourceVisuals();
        UpdatePhoneVisuals();
    }

    static Rect Scale(Rect r, double sx, double sy) => new(r.X * sx, r.Y * sy, r.Width * sx, r.Height * sy);

    static void Place(FrameworkElement el, Rect r)
    {
        Canvas.SetLeft(el, r.X);
        Canvas.SetTop(el, r.Y);
        el.Width = Math.Max(0, r.Width);
        el.Height = Math.Max(0, r.Height);
    }

    void UpdateSourceVisuals()
    {
        if (_video is null || _cropBox is null) return;
        double sx = SourceCanvas.ActualWidth / SW, sy = SourceCanvas.ActualHeight / SH;
        if (sx <= 0 || sy <= 0) return;

        _cropBox.Place(Scale(_layout.CropRect(SW, SH), sx, sy), SourceCanvas.ActualWidth);
        for (int i = 0; i < _sourceBoxes.Count && i < _layout.Elements.Count; i++)
        {
            var e = _layout.Elements[i];
            var box = _sourceBoxes[i];
            box.Place(Scale(ShortsLayout.SourceRect(e, SW, SH), sx, sy), SourceCanvas.ActualWidth);
            box.Label = e.Name;
            box.IsShown = e.Visible;
            box.IsSelected = e == _selected;
        }
    }

    void UpdatePhoneVisuals()
    {
        if (_video is null || _mainLayer is null || _frameBox is null) return;
        double pw = PhoneCanvas.ActualWidth, ph = PhoneCanvas.ActualHeight, k = pw / ShortsLayout.Width;
        if (k <= 0) return;

        bool blur = !_layout.FillsFrame && _layout.Background == ShortsBackground.Blur;
        double radius = 60 * k;
        _bgLayer!.Visibility = _bgDim!.Visibility = blur ? Visibility.Visible : Visibility.Collapsed;
        ((BlurEffect)_bgLayer.Effect).Radius = radius;
        // Oversize the blurred layer so its soft edges fall outside the screen, like FFmpeg's edge-clamped blur.
        Place(_bgLayer, new Rect(-radius, -radius, pw + 2 * radius, ph + 2 * radius));
        Place(_bgDim, new Rect(0, 0, pw, ph));

        var frame = Scale(_layout.FrameRect(), k, k);
        var crop = _layout.CropRect(SW, SH);
        Place(_mainLayer, frame);
        ((VisualBrush)_mainLayer.Fill).Viewbox = new Rect(crop.X / SW, crop.Y / SH, crop.Width / SW, crop.Height / SH);
        _frameBox.Place(frame, pw);

        for (int i = 0; i < _phoneLayers.Count && i < _layout.Elements.Count; i++)
        {
            var e = _layout.Elements[i];
            var r = Scale(ShortsLayout.OutputRect(e, SW, SH), k, k);
            Place(_phoneLayers[i], r);
            ((VisualBrush)_phoneLayers[i].Fill).Viewbox = new Rect(e.SrcX, e.SrcY, e.SrcW, e.SrcH);
            _phoneLayers[i].Visibility = e.Visible ? Visibility.Visible : Visibility.Collapsed;
            _phoneBoxes[i].Place(r, pw);
            _phoneBoxes[i].Label = e.Name;
            _phoneBoxes[i].IsShown = e.Visible;
            _phoneBoxes[i].IsSelected = e == _selected;
        }
        UpdateCoverage();
    }

    /// <summary>Redraws the app interface over the preview when the platform, size or covered zones changed.</summary>
    void DrawAppOverlay(bool force = false)
    {
        double pw = PhoneHost.ActualWidth;
        bool show = SafeZoneSwitch.IsChecked == true && pw > 0;
        var key = show ? $"{Platform.Platform}|{pw}|{string.Join(",", _covered.Select(z => z.Name))}" : "";
        if (!force && key == _overlayKey) return;
        _overlayKey = key;
        AppOverlayLayer.Children.Clear();
        if (show) AppOverlay.Draw(AppOverlayLayer, Platform, pw / ShortsLayout.Width, _covered);
    }

    /// <summary>Flags HUD elements that the app's buttons, caption or menus would hide.</summary>
    void UpdateCoverage()
    {
        if (_video is null) return;
        var ui = Platform;
        var lines = new List<string>();
        var covered = new List<UiZone>();
        for (int i = 0; i < _layout.Elements.Count; i++)
        {
            var e = _layout.Elements[i];
            var zone = e.Visible ? ui.Covering(ShortsLayout.OutputRect(e, SW, SH)) : null;
            if (i < _phoneBoxes.Count) _phoneBoxes[i].IsWarning = zone is not null;
            if (_rowWarnings.TryGetValue(e, out var icon))
            {
                icon.Visibility = zone is null ? Visibility.Collapsed : Visibility.Visible;
                icon.ToolTip = zone is null ? null : $"Covered by {zone.Name}";
            }
            if (zone is null) continue;
            covered.Add(zone);
            lines.Add($"{e.Name} is covered by {zone.Name}.");
        }
        _covered = covered.Distinct().ToList();
        CoverageWarning.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (lines.Count > 0) lines.Add("Drag it somewhere else or use Auto arrange.");
        CoverageText.Text = string.Join(Environment.NewLine, lines);
        DrawAppOverlay();
    }

    void ShowGuides(double? x, double? y)
    {
        double k = PhoneCanvas.ActualWidth / ShortsLayout.Width;
        GuideV.Visibility = x is null ? Visibility.Collapsed : Visibility.Visible;
        GuideH.Visibility = y is null ? Visibility.Collapsed : Visibility.Visible;
        if (x is { } gx) Canvas.SetLeft(GuideV, Math.Round(gx * k));
        if (y is { } gy) Canvas.SetTop(GuideH, Math.Round(gy * k));
    }

    // =====================================================================
    // Editing: undo, drags, wheel
    // =====================================================================

    void PushUndo(ShortsLayout before)
    {
        _undo.Push(before);
        _redo.Clear();
        UpdateUndoButtons();
    }

    /// <summary>Records the current layout before a one-off change (button, preset, delete…).</summary>
    void Checkpoint()
    {
        PushUndo(_layout.Clone());
        _lastGroupedTarget = null;
    }

    /// <summary>Wheel turns and slider moves on the same thing in quick succession become a single undo step.</summary>
    void GroupedCheckpoint(object target)
    {
        var now = DateTime.UtcNow;
        if (!ReferenceEquals(target, _lastGroupedTarget) || now - _lastGroupedEdit > GroupEditsWithin)
            PushUndo(_layout.Clone());
        _lastGroupedTarget = target;
        _lastGroupedEdit = now;
    }

    void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
        UndoButton.Opacity = UndoButton.IsEnabled ? 1 : 0.35;
        RedoButton.Opacity = RedoButton.IsEnabled ? 1 : 0.35;
    }

    void Undo()
    {
        if (_undo.Count == 0 || _dragSnapshot is not null) return;
        _redo.Push(_layout.Clone());
        RestoreLayout(_undo.Pop());
    }

    void Redo()
    {
        if (_redo.Count == 0 || _dragSnapshot is not null) return;
        _undo.Push(_layout.Clone());
        RestoreLayout(_redo.Pop());
    }

    void RestoreLayout(ShortsLayout layout)
    {
        int selectedIndex = _selected is null ? -1 : _layout.Elements.IndexOf(_selected);
        _layout = layout;
        _selected = selectedIndex >= 0 && selectedIndex < _layout.Elements.Count ? _layout.Elements[selectedIndex] : null;
        _lastGroupedTarget = null;
        RebuildAll();
        UpdateSelectionUi();
        UpdateUndoButtons();
        Persist();
    }

    void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    void BeginEdit(HudElement? element)
    {
        Keyboard.ClearFocus();
        Select(element);
        _dragSnapshot = _layout.Clone();
        _dragChanged = false;
    }

    void EndEdit()
    {
        if (_dragSnapshot is not null && _dragChanged) PushUndo(_dragSnapshot);
        _dragSnapshot = null;
        _lastGroupedTarget = null;
        ShowGuides(null, null);
        Persist();
    }

    void Edited()
    {
        _dragChanged = true;
        UpdateAllVisuals();
        UpdateSelectionUi();
    }

    /// <summary>Moves or resizes the gameplay crop on the source; the crop keeps the frame's aspect ratio.</summary>
    void DragCrop(RegionHandle handle, Vector d)
    {
        if (_dragSnapshot is not { } s || _video is null) return;
        double sx = SourceCanvas.ActualWidth / SW, sy = SourceCanvas.ActualHeight / SH;
        var start = s.CropRect(SW, SH);

        if (handle == RegionHandle.Move)
        {
            double cx = start.X + start.Width / 2 + d.X / sx, cy = start.Y + start.Height / 2 + d.Y / sy;
            // Snap to the middle of the screen, where the crosshair is.
            if (Math.Abs((cx - SW / 2) * sx) < SnapDistance) cx = SW / 2;
            if (Math.Abs((cy - SH / 2) * sy) < SnapDistance) cy = SH / 2;
            _layout.CropX = cx / SW;
            _layout.CropY = cy / SH;
        }
        else
        {
            bool left = handle is RegionHandle.TopLeft or RegionHandle.BottomLeft;
            bool top = handle is RegionHandle.TopLeft or RegionHandle.TopRight;
            double byWidth = (start.Width + (left ? -d.X : d.X) / sx) / start.Width;
            double byHeight = (start.Height + (top ? -d.Y : d.Y) / sy) / start.Height;
            double scale = Math.Abs(byWidth - 1) > Math.Abs(byHeight - 1) ? byWidth : byHeight;
            double aspect = start.Width / start.Height;
            double h = Math.Clamp(start.Height * scale, 0.12 * SH, s.MaxZoom(SW, SH) * SH), w = h * aspect;
            // Resize from the opposite corner, which stays where it is.
            double x = left ? start.Right - w : start.X, y = top ? start.Bottom - h : start.Y;
            _layout.CropZoom = h / SH;
            _layout.CropX = (x + w / 2) / SW;
            _layout.CropY = (y + h / 2) / SH;
        }
        _layout.NormalizeCrop(SW, SH);
        Edited();
    }

    /// <summary>Drag on the gameplay in the preview: the picture follows the mouse; a smaller frame also moves up and down.</summary>
    void DragFrameOnPhone(Vector d)
    {
        if (_dragSnapshot is not { } s || _video is null) return;
        double k = PhoneCanvas.ActualWidth / ShortsLayout.Width;
        var frame = s.FrameRect();
        var crop = s.CropRect(SW, SH);
        double perOutput = crop.Width / frame.Width;   // source pixels per output pixel

        _layout.CropX = (crop.X + crop.Width / 2 - d.X / k * perOutput) / SW;
        if (_layout.FillsFrame)
        {
            _layout.CropY = (crop.Y + crop.Height / 2 - d.Y / k * perOutput) / SH;
        }
        else
        {
            double y = frame.Y + d.Y / k;
            double? guide = null;
            if (Math.Abs((y + frame.Height / 2 - ShortsLayout.Height / 2.0) * k) < SnapDistance)
            {
                y = ShortsLayout.Height / 2.0 - frame.Height / 2;
                guide = ShortsLayout.Height / 2.0;
            }
            _layout.FrameY = (y + frame.Height / 2) / ShortsLayout.Height;
            ShowGuides(null, guide);
        }
        _layout.NormalizeCrop(SW, SH);
        Edited();
    }

    void ZoomCrop(MouseWheelEventArgs e)
    {
        if (_video is null || IsBusy) return;
        e.Handled = true;
        GroupedCheckpoint(_layout);
        _layout.CropZoom = Math.Clamp(_layout.CropZoom * (e.Delta > 0 ? 0.95 : 1 / 0.95), 0.12, _layout.MaxZoom(SW, SH));
        _layout.NormalizeCrop(SW, SH);
        UpdateAllVisuals();
        Persist();
    }

    /// <summary>Moves or resizes a HUD region on the source (free aspect ratio, stays inside the frame).</summary>
    void DragSourceElement(int index, RegionHandle handle, Vector d)
    {
        if (_dragSnapshot is not { } s || index >= _layout.Elements.Count) return;
        var start = s.Elements[index];
        var e = _layout.Elements[index];
        double dx = d.X / SourceCanvas.ActualWidth, dy = d.Y / SourceCanvas.ActualHeight;
        double l = start.SrcX, t = start.SrcY, r = l + start.SrcW, b = t + start.SrcH;

        if (handle == RegionHandle.Move)
        {
            double nl = Math.Clamp(l + dx, 0, 1 - start.SrcW), nt = Math.Clamp(t + dy, 0, 1 - start.SrcH);
            r += nl - l;
            b += nt - t;
            l = nl;
            t = nt;
        }
        else
        {
            if (handle is RegionHandle.Left or RegionHandle.TopLeft or RegionHandle.BottomLeft) l = Math.Clamp(l + dx, 0, r - MinSourceSize);
            if (handle is RegionHandle.Right or RegionHandle.TopRight or RegionHandle.BottomRight) r = Math.Clamp(r + dx, l + MinSourceSize, 1);
            if (handle is RegionHandle.Top or RegionHandle.TopLeft or RegionHandle.TopRight) t = Math.Clamp(t + dy, 0, b - MinSourceSize);
            if (handle is RegionHandle.Bottom or RegionHandle.BottomLeft or RegionHandle.BottomRight) b = Math.Clamp(b + dy, t + MinSourceSize, 1);
        }
        e.SrcX = l;
        e.SrcY = t;
        e.SrcW = r - l;
        e.SrcH = b - t;
        Edited();
    }

    /// <summary>Moves (with snapping) or resizes (keeping the aspect ratio) a HUD element on the preview.</summary>
    void DragPhoneElement(int index, RegionHandle handle, Vector d)
    {
        if (_dragSnapshot is not { } s || _video is null || index >= _layout.Elements.Count) return;
        const double W = ShortsLayout.Width, H = ShortsLayout.Height;
        double k = PhoneCanvas.ActualWidth / W, snap = SnapDistance / k;
        var e = _layout.Elements[index];
        var r0 = ShortsLayout.OutputRect(s.Elements[index], SW, SH);

        if (handle == RegionHandle.Move)
        {
            double x = r0.X + d.X / k, y = r0.Y + d.Y / k, w = r0.Width, h = r0.Height;
            double margin = 0.045 * W;
            double? gx = null, gy = null;

            if (Math.Abs(x + w / 2 - W / 2) < snap) { x = W / 2 - w / 2; gx = W / 2; }
            else if (Math.Abs(x - margin) < snap) { x = margin; gx = margin; }
            else if (Math.Abs(x + w - (W - margin)) < snap) { x = W - margin - w; gx = W - margin; }

            var frame = _layout.FrameRect();
            if (Math.Abs(y + h / 2 - H / 2) < snap) { y = H / 2 - h / 2; gy = H / 2; }
            else if (!_layout.FillsFrame && Math.Abs(y + h - frame.Top) < snap) { y = frame.Top - h; gy = frame.Top; }
            else if (!_layout.FillsFrame && Math.Abs(y - frame.Bottom) < snap) { y = frame.Bottom; gy = frame.Bottom; }

            e.OutX = Math.Clamp(x, -w / 2, W - w / 2) / W;
            e.OutY = Math.Clamp(y, -h / 2, H - h / 2) / H;
            ShowGuides(gx, gy);
        }
        else
        {
            bool left = handle is RegionHandle.TopLeft or RegionHandle.BottomLeft;
            bool top = handle is RegionHandle.TopLeft or RegionHandle.TopRight;
            double aspect = r0.Width / r0.Height;
            double byWidth = r0.Width + (left ? -d.X : d.X) / k;
            double byHeight = (r0.Height + (top ? -d.Y : d.Y) / k) * aspect;
            double w = Math.Clamp(Math.Abs(byWidth - r0.Width) > Math.Abs(byHeight - r0.Width) ? byWidth : byHeight, 0.06 * W, W);
            double h = w / aspect;
            e.OutW = w / W;
            e.OutX = (left ? r0.Right - w : r0.X) / W;
            e.OutY = (top ? r0.Bottom - h : r0.Y) / H;
        }
        Edited();
    }

    void ScaleElementByWheel(int index, MouseWheelEventArgs args)
    {
        if (_video is null || IsBusy || index >= _layout.Elements.Count) return;
        args.Handled = true;
        var e = _layout.Elements[index];
        Select(e);
        GroupedCheckpoint(e);
        ResizeAroundCenter(e, e.OutW * (args.Delta > 0 ? 1.05 : 1 / 1.05));
        UpdateAllVisuals();
        UpdateSelectionUi();
        Persist();
    }

    void ResizeAroundCenter(HudElement e, double outW)
    {
        var before = ShortsLayout.OutputRect(e, SW, SH);
        e.OutW = Math.Clamp(outW, 0.06, 1);
        var after = ShortsLayout.OutputRect(e, SW, SH);
        e.OutX = (before.X + before.Width / 2 - after.Width / 2) / ShortsLayout.Width;
        e.OutY = (before.Y + before.Height / 2 - after.Height / 2) / ShortsLayout.Height;
    }

    void PhoneCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == PhoneCanvas)
        {
            Keyboard.ClearFocus();
            Select(null);
        }
    }

    // =====================================================================
    // Selection and the element list
    // =====================================================================

    void Select(HudElement? element)
    {
        if (_selected == element) return;
        _selected = element;
        for (int i = 0; i < _layout.Elements.Count && i < _sourceBoxes.Count; i++)
        {
            _sourceBoxes[i].IsSelected = _layout.Elements[i] == element;
            _phoneBoxes[i].IsSelected = _layout.Elements[i] == element;
        }
        UpdateSelectionUi();
    }

    void UpdateSelectionUi()
    {
        foreach (var row in ElementList.Children.OfType<Border>())
        {
            bool selected = row.Tag == _selected;
            row.Background = (Brush)FindResource(selected ? "Hover" : "Surface");
            row.BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)) : (Brush)FindResource("Line");
        }

        SelectedPanel.Visibility = _selected is null ? Visibility.Collapsed : Visibility.Visible;
        if (_selected is null || _video is null) return;
        var r = ShortsLayout.OutputRect(_selected, SW, SH);
        SelectedTitle.Text = _selected.Name.ToUpperInvariant();
        SelectedSizeText.Text = $"{(int)Math.Round(r.Width)} × {(int)Math.Round(r.Height)} px";
        _syncingUi = true;
        SizeSlider.Value = _selected.OutW;
        _syncingUi = false;
    }

    void RebuildElementList()
    {
        ElementList.Children.Clear();
        _rowWarnings.Clear();
        for (int i = 0; i < _layout.Elements.Count; i++)
            ElementList.Children.Add(CreateElementRow(_layout.Elements[i], ColorOf(i)));
        NoElementsText.Visibility = _layout.Elements.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionUi();
    }

    Border CreateElementRow(HudElement e, Color color)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
        grid.Children.Add(dot);

        var name = new TextBox { Style = (Style)FindResource("InlineInput"), Text = e.Name, MaxLength = 24 };
        Grid.SetColumn(name, 1);
        name.GotKeyboardFocus += (_, _) =>
        {
            Select(e);
            _renameSnapshotName = e.Name;
        };
        name.TextChanged += (_, _) =>
        {
            e.Name = name.Text;
            UpdateAllVisuals();
            if (_selected == e) SelectedTitle.Text = e.Name.ToUpperInvariant();
        };
        name.LostKeyboardFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) name.Text = _renameSnapshotName ?? "Element";
            int index = _layout.Elements.IndexOf(e);
            if (_renameSnapshotName is { } before && before != e.Name && index >= 0)
            {
                var snapshot = _layout.Clone();
                snapshot.Elements[index].Name = before;
                PushUndo(snapshot);
                Persist();
            }
            _renameSnapshotName = null;
        };
        name.KeyDown += (_, args) =>
        {
            if (args.Key is Key.Enter or Key.Escape)
            {
                if (args.Key == Key.Escape && _renameSnapshotName is { } before) name.Text = before;
                Keyboard.ClearFocus();
                args.Handled = true;
            }
        };
        grid.Children.Add(name);

        var eye = new Button
        {
            Style = (Style)FindResource("RowIconButton"),
            Content = e.Visible ? "\uE7B3" : "\uED1A",
            ToolTip = e.Visible ? "Hide" : "Show",
        };
        Grid.SetColumn(eye, 3);
        eye.Click += (_, _) =>
        {
            Checkpoint();
            e.Visible = !e.Visible;
            eye.Content = e.Visible ? "\uE7B3" : "\uED1A";
            eye.ToolTip = e.Visible ? "Hide" : "Show";
            dot.Opacity = name.Opacity = e.Visible ? 1 : 0.4;
            UpdateAllVisuals();
            Persist();
        };
        grid.Children.Add(eye);

        var delete = new Button { Style = (Style)FindResource("RowIconButton"), Content = "\uE74D", ToolTip = "Delete" };
        Grid.SetColumn(delete, 4);
        delete.Click += (_, _) => DeleteElement(e);
        grid.Children.Add(delete);

        // Shown by UpdateCoverage when the app interface would hide this element.
        var warning = new TextBlock
        {
            Text = "\uE7BA",
            Style = (Style)FindResource("Icon"),
            FontSize = 13,
            Foreground = (Brush)FindResource("Danger"),
            Margin = new Thickness(4, 0, 6, 0),
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(warning, 2);
        grid.Children.Add(warning);
        _rowWarnings[e] = warning;

        dot.Opacity = name.Opacity = e.Visible ? 1 : 0.4;

        var row = new Border
        {
            Tag = e,
            Child = grid,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 4, 4),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand,
        };
        row.MouseLeftButtonDown += (_, _) => Select(e);
        return row;
    }

    void AddElement_Click(object sender, RoutedEventArgs e)
    {
        if (_video is null) return;
        Checkpoint();
        var used = _layout.Elements.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = DefaultNames.FirstOrDefault(n => !used.Contains(n)) ?? $"Element {_layout.Elements.Count + 1}";
        // Starts in the lower left, where most games draw health; drag it onto the real HUD.
        var element = new HudElement { Name = name, SrcX = 0.03, SrcY = 0.8, SrcW = 0.2, SrcH = 0.12 };
        _layout.PlaceNew(element, SW, SH, Platform);
        _layout.Elements.Add(element);
        _selected = element;
        RebuildAll();
        Persist();
    }

    void DeleteElement(HudElement element)
    {
        int index = _layout.Elements.IndexOf(element);
        if (index < 0) return;
        Checkpoint();
        _layout.Elements.RemoveAt(index);
        if (_selected == element) _selected = null;
        RebuildAll();
        Persist();
    }

    void DeleteElement_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) DeleteElement(_selected);
    }

    void DuplicateElement_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        Checkpoint();
        var copy = _selected.Clone();
        copy.Name = _selected.Name + " 2";
        copy.OutY = Math.Min(copy.OutY + 0.03, 0.95);
        _layout.Elements.Insert(_layout.Elements.IndexOf(_selected) + 1, copy);
        _selected = copy;
        RebuildAll();
        Persist();
    }

    void CenterElement_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _video is null) return;
        Checkpoint();
        var r = ShortsLayout.OutputRect(_selected, SW, SH);
        _selected.OutX = (ShortsLayout.Width - r.Width) / 2 / ShortsLayout.Width;
        UpdateAllVisuals();
        Persist();
    }

    void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingUi || _selected is null || _video is null) return;
        GroupedCheckpoint(_selected);
        ResizeAroundCenter(_selected, e.NewValue);
        UpdateAllVisuals();
        UpdateSelectionUi();
        Persist();
    }

    void AutoArrange_Click(object sender, RoutedEventArgs e)
    {
        if (_video is null || _layout.Elements.Count == 0) return;
        Checkpoint();
        _layout.AutoArrange(SW, SH, Platform);
        UpdateAllVisuals();
        UpdateSelectionUi();
        Persist();
    }

    // =====================================================================
    // Framing, background, safe zones
    // =====================================================================

    void SyncOptionUi()
    {
        _syncingUi = true;
        (_layout.Framing switch
        {
            ShortsFraming.Portrait => FramePortrait,
            ShortsFraming.Square => FrameSquare,
            ShortsFraming.Wide => FrameWide,
            _ => FrameFull,
        }).IsChecked = true;
        (_layout.Background == ShortsBackground.Black ? BgBlack : BgBlur).IsChecked = true;
        BackgroundSection.Visibility = _layout.FillsFrame ? Visibility.Collapsed : Visibility.Visible;
        _syncingUi = false;
    }

    void Framing_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingUi || !IsLoaded) return;
        var framing = sender == FramePortrait ? ShortsFraming.Portrait
            : sender == FrameSquare ? ShortsFraming.Square
            : sender == FrameWide ? ShortsFraming.Wide
            : ShortsFraming.Full;
        if (_video is null || framing == _layout.Framing) return;

        Checkpoint();
        _layout.Framing = framing;
        _layout.FrameY = 0.5;
        // Show as much of the picture as the new frame allows, then make room for the HUD around it.
        _layout.CropZoom = 1;
        _layout.NormalizeCrop(SW, SH);
        _layout.AutoArrange(SW, SH, Platform);
        SyncOptionUi();
        UpdateAllVisuals();
        UpdateSelectionUi();
        Persist();
    }

    void Background_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingUi || !IsLoaded || _video is null) return;
        var background = sender == BgBlack ? ShortsBackground.Black : ShortsBackground.Blur;
        if (background == _layout.Background) return;
        Checkpoint();
        _layout.Background = background;
        UpdatePhoneVisuals();
        Persist();
    }

    void SafeZoneSwitch_Click(object sender, RoutedEventArgs e)
    {
        _state.Settings.ShortsSafeZones = SafeZoneSwitch.IsChecked == true;
        _state.Settings.Save();
        DrawAppOverlay();
    }

    void Platform_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingUi || !IsLoaded) return;
        _state.Settings.ShortsPlatform = sender == PlatformReels ? ShortsPlatform.Reels
            : sender == PlatformShorts ? ShortsPlatform.Shorts
            : ShortsPlatform.TikTok;
        _state.Settings.Save();
        // Turning the preview on with the platform makes the change visible right away.
        if (SafeZoneSwitch.IsChecked != true)
        {
            SafeZoneSwitch.IsChecked = true;
            _state.Settings.ShortsSafeZones = true;
            _state.Settings.Save();
        }
        UpdateCoverage();
    }

    /// <summary>Saves the layout shortly after the last change, so wheel turns and slider drags don't rewrite the settings file each step.</summary>
    void Persist()
    {
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    void PersistNow()
    {
        _persistTimer.Stop();
        _state.Settings.ShortsLayout = _layout.Clone();
        _state.Settings.Save();
    }

    // =====================================================================
    // Game presets
    // =====================================================================

    ShortsPreset? FindPreset(string? name) =>
        name is null ? null
            : _state.Settings.ShortsPresets.FirstOrDefault(p => p.Name == name)
              ?? GamePresets.BuiltIn.FirstOrDefault(p => p.Name == name);

    bool IsCustomPreset(string? name) => name is not null && _state.Settings.ShortsPresets.Any(p => p.Name == name);

    void BuildGameTiles()
    {
        GamePanel.Children.Clear();
        void Add(string name, ShortsPreset? preset, bool custom)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (custom)
                content.Children.Add(new TextBlock { Text = "\uE734", Style = (Style)FindResource("Icon"), FontSize = 10, Foreground = (Brush)FindResource("Accent"), Margin = new Thickness(0, 1, 6, 0) });
            content.Children.Add(new TextBlock { Text = name, Style = (Style)FindResource("TileMain"), FontSize = 12.5 });
            var tile = new RadioButton
            {
                Style = (Style)FindResource("OptionTile"),
                GroupName = "ShortsGame",
                Content = content,
                IsChecked = _state.Settings.ShortsGame == name,
                ToolTip = custom ? "Your saved layout" : preset is null ? "Start without HUD elements" : $"HUD layout for {name}",
            };
            tile.Click += (_, _) => ApplyPreset(name, preset);
            GamePanel.Children.Add(tile);
        }

        foreach (var p in GamePresets.BuiltIn) Add(p.Name, p, custom: false);
        foreach (var p in _state.Settings.ShortsPresets) Add(p.Name, p, custom: true);
        Add(BlankGame, null, custom: false);
        UpdateDeletePresetButton();
    }

    void ApplyPreset(string name, ShortsPreset? preset)
    {
        _state.Settings.ShortsGame = name;
        UpdateDeletePresetButton();
        if (_video is null)
        {
            _layout = preset?.Layout.Clone() ?? new ShortsLayout();
            Persist();
            return;
        }

        Checkpoint();
        _layout = preset is null
            ? new ShortsLayout { Framing = _layout.Framing, Background = _layout.Background, CropX = _layout.CropX, CropY = _layout.CropY, CropZoom = _layout.CropZoom, FrameY = _layout.FrameY }
            : GamePresets.Instantiate(preset, SW, SH, Platform);
        _selected = null;
        RebuildAll();
        Persist();
    }

    void UpdateDeletePresetButton()
    {
        var name = _state.Settings.ShortsGame;
        bool custom = IsCustomPreset(name);
        DeletePresetButton.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (custom) DeletePresetButton.Content = $"Delete preset “{name}”";
    }

    void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        var name = _state.Settings.ShortsGame;
        _state.Settings.ShortsPresets.RemoveAll(p => p.Name == name);
        _state.Settings.ShortsGame = null;
        _state.Settings.Save();
        BuildGameTiles();
    }

    void SavePresetLink_Click(object sender, RoutedEventArgs e)
    {
        SavePresetPanel.Visibility = Visibility.Visible;
        SavePresetLink.Visibility = Visibility.Collapsed;
        var current = _state.Settings.ShortsGame;
        PresetNameBox.Text = IsCustomPreset(current) ? current : current is null or BlankGame ? "My layout" : $"My {current}";
        PresetNameBox.Focus();
        PresetNameBox.SelectAll();
    }

    void PresetNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SavePreset_Click(sender, e);
        else if (e.Key == Key.Escape) CancelPreset_Click(sender, e);
        else return;
        e.Handled = true;
    }

    void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var name = PresetNameBox.Text.Trim();
        if (name.Length == 0) return;
        // Built-in names stay reserved so their tiles keep working.
        if (GamePresets.BuiltIn.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) || name.Equals(BlankGame, StringComparison.OrdinalIgnoreCase))
            name += " (mine)";

        var presets = _state.Settings.ShortsPresets;
        presets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        presets.Add(new ShortsPreset { Name = name, Layout = _layout.Clone() });
        _state.Settings.ShortsGame = name;
        _state.Settings.Save();
        BuildGameTiles();
        CancelPreset_Click(sender, e);
    }

    void CancelPreset_Click(object sender, RoutedEventArgs e)
    {
        SavePresetPanel.Visibility = Visibility.Collapsed;
        SavePresetLink.Visibility = Visibility.Visible;
        Keyboard.ClearFocus();
    }

    // =====================================================================
    // Clip range
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
        Player.SetRange(_start, _end >= _video.Duration ? null : _end, highlight: !IsFullLength);

        LengthNote.Visibility = Visibility.Visible;
        if (length > ShortsMaxLength)
        {
            LengthNote.Text = $"Length {Format.Clock(length)}. YouTube Shorts allows up to 3:00; TikTok and Reels take longer clips.";
            LengthNote.Foreground = (Brush)FindResource("Warning");
        }
        else
        {
            LengthNote.Text = $"Length {Format.Clock(length)}";
            LengthNote.Foreground = (Brush)FindResource("Text2");
        }
        UpdateEstimate();
    }

    void SetStart_Click(object sender, RoutedEventArgs e) => SetStart(Player.Position, seek: false);

    void SetEnd_Click(object sender, RoutedEventArgs e) => SetEnd(Player.Position, seek: false);

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

    // =====================================================================
    // Export
    // =====================================================================

    void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _state.Settings.ShortsLimit60 = Limit60Switch.IsChecked == true;
        _state.Settings.Save();
        UpdateEstimate();
    }

    EncodePlan CreatePlan(VideoInfo video, string workDir)
    {
        var options = new ShortsOptions
        {
            Start = _start,
            End = _end >= video.Duration ? null : _end,
            Limit60 = Limit60Switch.IsChecked == true,
            RemoveAudio = RemoveAudioSwitch.IsChecked == true && RemoveAudioSwitch.IsEnabled,
            UseHardware = _state.Settings.UseHardware && _state.Encoders.H264 != HwVendor.None,
            SubtitlesAss = SubtitlesForExport(video),
        };
        return VideoEngine.CreateShortsPlan(video, _layout, options, _state.Encoders,
            _state.Settings.BuildOutputPath(video.Path, "_short", ".mp4"), workDir);
    }

    void UpdateEstimate()
    {
        if (_video is null) return;
        try
        {
            var plan = CreatePlan(_video, Path.GetTempPath());
            SummaryOutput.Text = $"1080 × 1920 · {Format.Fps(plan.Fps)} fps";
            SummarySize.Text = "≈ " + Format.Size(plan.EstimatedBytes);
            ExportButton.IsEnabled = !GeneratingSubtitles;
        }
        catch (InvalidOperationException)
        {
            SummarySize.Text = "—";
            ExportButton.IsEnabled = false;
        }
    }

    async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_video is null || _state.Tools is null || IsBusy || GeneratingSubtitles) return;

        var video = _video;
        EncodePlan plan;
        try
        {
            plan = CreatePlan(video, Path.Combine(Path.GetTempPath(), "compress-" + Guid.NewGuid().ToString("N")));
        }
        catch (InvalidOperationException ex)
        {
            ShowError("Can't export with these settings", ex.Message);
            return;
        }

        Player.Pause();
        Stage.IsEnabled = false;
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
            Usage.Export("shorts");
            await ShowResultAsync(plan, ProgressPanel.Elapsed);
        }
        catch (OperationCanceledException)
        {
            ShowPanel(SettingsPanel);
        }
        catch (Exception ex)
        {
            string message = ex.Message;
            if (plan.IsHardware) message += "\n\nTip: turn off GPU acceleration on the Compress page and try again.";
            ShowError("Export failed", message);
        }
        finally
        {
            ProgressPanel.Stop();
            _jobCts.Dispose();
            _jobCts = null;
            _jobOutputPath = null;
            Stage.IsEnabled = true;
        }
    }

    async Task ShowResultAsync(EncodePlan plan, TimeSpan elapsed)
    {
        _resultPath = plan.OutputPath;
        var size = new FileInfo(plan.OutputPath).Length;
        var length = plan.Duration;
        try
        {
            length = (await VideoEngine.ProbeAsync(_state.Tools!, plan.OutputPath)).Duration;
        }
        catch { /* fall back to the planned length */ }

        DoneFileText.Text = $"Saved as {Path.GetFileName(plan.OutputPath)}";
        DoneLength.Text = Format.Clock(length);
        DoneSize.Text = Format.Size(size);
        DoneDetails.Text = $"{plan.EncoderLabel} · 1080×1920 · {Format.Fps(plan.Fps)} fps · took {Format.Time(elapsed)}";
        CopyFileIcon.Text = "\uE8C8";
        CopyFileText.Text = "Copy file";
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
