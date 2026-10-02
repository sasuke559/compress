using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Compress.Controls;
using Compress.Core;

namespace Compress.Pages;

/// <summary>Shorts page: captions from the clip's speech, made only when the user clicks "Generate subtitles".</summary>
public partial class ShortsPage
{
    List<SubLine>? _subLines;
    string _subLanguageUsed = "auto";
    CancellationTokenSource? _subCts;
    readonly List<(SubLine Line, Border Row)> _subRows = [];

    bool GeneratingSubtitles => _subCts is not null;

    SubtitlePosition SubPosition =>
        SubPosTop.IsChecked == true ? SubtitlePosition.Top : SubPosMiddle.IsChecked == true ? SubtitlePosition.Middle : SubtitlePosition.Bottom;

    void InitSubtitles()
    {
        Ui.MakeDropdown(SubLanguagePopup, SubLanguageButton, FillLanguageMenu);
        UpdateLanguageButton();
        _syncingUi = true;
        (_state.Settings.SubtitlePosition switch { SubtitlePosition.Top => SubPosTop, SubtitlePosition.Middle => SubPosMiddle, _ => SubPosBottom })
            .IsChecked = true;
        _syncingUi = false;
        Player.PositionChanged += (_, _) => UpdateCaptionPreview();
    }

    bool FillLanguageMenu()
    {
        SubLanguageList.Children.Clear();
        foreach (var language in SubtitleLanguage.All)
        {
            var option = new Button
            {
                Style = (Style)FindResource("MenuOption"),
                Tag = language.Code == _state.Settings.SubtitleLanguage ? "current" : null,
                Content = new TextBlock { Text = language.Name },
            };
            option.Click += (_, _) =>
            {
                SubLanguagePopup.IsOpen = false;
                _state.Settings.SubtitleLanguage = language.Code;
                _state.Settings.Save();
                UpdateLanguageButton();
            };
            SubLanguageList.Children.Add(option);
        }
        return true;
    }

    void UpdateLanguageButton() =>
        SubLanguageText.Text = (SubtitleLanguage.All.FirstOrDefault(l => l.Code == _state.Settings.SubtitleLanguage) ?? SubtitleLanguage.All[0]).Name;

    /// <summary>A new video: forget the old captions.</summary>
    void ResetSubtitles(VideoInfo video)
    {
        _subCts?.Cancel();
        _subLines = null;
        SubVoicePanel.Visibility = video.AudioTracks > 1 ? Visibility.Visible : Visibility.Collapsed;
        SubGenerateButton.IsEnabled = video.HasAudio;
        SubErrorText.Visibility = Visibility.Collapsed;
        if (!video.HasAudio) ShowSubError("This video has no sound.");
        ShowSubPanel(SubIdlePanel);
        UpdateCaptionPreview();
    }

    void ShowSubPanel(FrameworkElement panel)
    {
        Ui.ShowOnly(panel, SubIdlePanel, SubWorkingPanel, SubDonePanel);
        SubModelNote.Visibility = Subtitles.ModelReady ? Visibility.Collapsed : Visibility.Visible;
    }

    void ShowSubError(string message)
    {
        SubErrorText.Text = message;
        SubErrorText.Visibility = Visibility.Visible;
    }

    async void SubGenerate_Click(object sender, RoutedEventArgs e)
    {
        if (_video is null || _state.Tools is null || GeneratingSubtitles || IsBusy) return;
        var video = _video;
        var tools = _state.Tools;
        double start = _start.TotalSeconds, end = (_end < video.Duration ? _end : video.Duration).TotalSeconds;
        int? track = video.AudioTracks > 1 && SubVoiceMic.IsChecked == true ? 1 : null;

        using var cts = new CancellationTokenSource();
        _subCts = cts;
        SubErrorText.Visibility = Visibility.Collapsed;
        ShowSubPanel(SubWorkingPanel);
        ExportButton.IsEnabled = false;
        try
        {
            if (!Subtitles.ModelReady)
            {
                SubStatusText.Text = "Downloading speech model…";
                await Subtitles.DownloadModelAsync(new Progress<double>(p =>
                {
                    SubProgress.Value = p;
                    SubStatusText.Text = $"Downloading speech model {p:P0}";
                }), cts.Token);
            }

            SubStatusText.Text = "Listening…";
            SubProgress.Value = 0;
            var progress = new Progress<double>(p =>
            {
                SubProgress.Value = p;
                SubStatusText.Text = $"Listening… {p:P0}";
            });
            var language = _state.Settings.SubtitleLanguage;
            var (lines, used) = await Task.Run(() => Subtitles.TranscribeAsync(tools, video, start, end, track, language, progress, cts.Token));
            if (video != _video) return; // another video was opened meanwhile

            if (lines.Count == 0)
            {
                ShowSubPanel(SubIdlePanel);
                ShowSubError(track is null
                    ? "No speech found in this part of the clip."
                    : "No speech found on the microphone. Try \"All audio\".");
                return;
            }
            _subLines = lines;
            _subLanguageUsed = used;
            SubBurnSwitch.IsChecked = true;
            BuildSubtitleRows();
            ShowSubPanel(SubDonePanel);
            UpdateCaptionPreview();
        }
        catch (OperationCanceledException)
        {
            if (video == _video) ShowSubPanel(SubIdlePanel);
        }
        catch (Exception ex)
        {
            if (video != _video) return;
            ShowSubPanel(SubIdlePanel);
            ShowSubError("Could not make subtitles: " + ex.Message);
        }
        finally
        {
            if (_subCts == cts) _subCts = null;
            UpdateEstimate();
        }
    }

    void SubCancel_Click(object sender, RoutedEventArgs e) => _subCts?.Cancel();

    void SubRedo_Click(object sender, RoutedEventArgs e)
    {
        _subLines = null;
        ShowSubPanel(SubIdlePanel);
        UpdateCaptionPreview();
    }

    void SubRemove_Click(object sender, RoutedEventArgs e) => SubRedo_Click(sender, e);

    void SubBurn_Click(object sender, RoutedEventArgs e)
    {
        UpdateSubtitleSummary();
        UpdateCaptionPreview();
    }

    void SubPosition_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingUi || _state is null) return;
        _state.Settings.SubtitlePosition = SubPosition;
        _state.Settings.Save();
        UpdateCaptionPreview();
    }

    void UpdateSubtitleSummary()
    {
        if (_subLines is null) return;
        var language = SubtitleLanguage.All.FirstOrDefault(l => l.Code == _subLanguageUsed)?.Name ?? _subLanguageUsed;
        SubSummaryText.Text = SubBurnSwitch.IsChecked == true
            ? $"{_subLines.Count} lines · {language}"
            : "Off: the short is exported without captions.";
    }

    /// <summary>One editable row per caption line; the time jumps the player there.</summary>
    void BuildSubtitleRows()
    {
        SubLinesPanel.Children.Clear();
        _subRows.Clear();
        if (_subLines is null) return;
        foreach (var line in _subLines)
        {
            var time = new TextBlock
            {
                Text = Format.Clock(TimeSpan.FromSeconds(line.Start)),
                FontSize = 11.5, Foreground = (Brush)FindResource("Text3"), VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 42, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, ToolTip = "Jump here",
            };
            var box = new TextBox
            {
                Text = line.Text, Style = (Style)FindResource("Input"), FontSize = 12.5, Padding = new Thickness(8, 5, 8, 5),
            };
            var grid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
            Grid.SetColumn(box, 1);
            grid.Children.Add(time);
            grid.Children.Add(box);
            var row = new Border { Child = grid, Padding = new Thickness(4, 2, 2, 2), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent };

            time.MouseLeftButtonDown += (_, _) => Player.Position = TimeSpan.FromSeconds(line.Start + 0.01);
            box.GotKeyboardFocus += (_, _) =>
            {
                if (!Player.IsPlaying) Player.Position = TimeSpan.FromSeconds(line.Start + 0.01);
            };
            box.TextChanged += (_, _) =>
            {
                if (box.Text.Trim().Length == 0) return; // removed on leaving the box
                line.SetText(box.Text);
                UpdateCaptionPreview(force: true);
            };
            box.LostKeyboardFocus += (_, _) =>
            {
                if (box.Text.Trim().Length > 0 || _subLines is null) return;
                _subLines.Remove(line);
                if (_subLines.Count == 0) SubRedo_Click(this, new RoutedEventArgs());
                else BuildSubtitleRows();
                UpdateCaptionPreview(force: true);
            };
            SubLinesPanel.Children.Add(row);
            _subRows.Add((line, row));
        }
        UpdateSubtitleSummary();
    }

    /// <summary>Draws the caption that the export would show at the player's position and marks its row.</summary>
    void UpdateCaptionPreview(bool force = false)
    {
        if (force) CaptionPreview.Show(null, -1, SubPosition);
        if (_subLines is null || SubBurnSwitch.IsChecked != true || _video is null)
        {
            CaptionPreview.Show(null, -1, SubPosition);
            return;
        }
        double t = Player.Position.TotalSeconds;
        var line = t >= _start.TotalSeconds && t <= _end.TotalSeconds ? Subtitles.LineAt(_subLines, t) : null;
        int word = line is null ? -1 : Math.Max(0, line.Words.FindLastIndex(w => w.Start <= t));
        CaptionPreview.Show(line, word, SubPosition);

        var accent = (Brush)FindResource("Accent");
        foreach (var (l, row) in _subRows) row.BorderBrush = l == line ? accent : Brushes.Transparent;
    }

    /// <summary>The captions for the export, or null when there are none or they are switched off.</summary>
    string? SubtitlesForExport(VideoInfo video) =>
        _subLines is { Count: > 0 } && SubBurnSwitch.IsChecked == true
            ? Subtitles.BuildAss(_subLines, _start.TotalSeconds, (_end < video.Duration ? _end : video.Duration).TotalSeconds, SubPosition)
            : null;
}
