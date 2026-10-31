using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Compress.Controls;
using Compress.Core;

namespace Compress.Pages;

/// <summary>
/// Folder mode of the Compress page: every video of a folder (or several dropped videos) in a queue, compressed one
/// after another with the settings of the side panel. Results go to a "&lt;folder&gt; (compressed)" folder next to the
/// original one, with the same subfolders, so the original folder stays untouched.
/// </summary>
public partial class CompressPage
{
    readonly ObservableCollection<QueueItem> _batch = [];
    /// <summary>Folder the queue was read from; null when loose videos were dropped.</summary>
    string? _batchFolder;
    string? _batchLastOutput;
    bool _batchWired;

    bool IsBatch => BatchArea.Visibility == Visibility.Visible;

    // =====================================================================
    // Entering / leaving folder mode
    // =====================================================================

    /// <summary>Several videos or a folder go to the queue; a single video opens the normal editor.</summary>
    public Task LoadVideosAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy || _loadingVideo || paths.Count == 0) return Task.CompletedTask;
        if (paths.FirstOrDefault(Directory.Exists) is { } folder) return LoadFolderAsync(folder);
        var videos = paths.Where(Ui.IsVideo).ToList();
        if (videos.Count == 1) return LoadVideoAsync(videos[0]);
        return videos.Count > 1 ? StartBatchAsync(null, videos) : Task.CompletedTask;
    }

    void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || _loadingVideo) return;
        if (Ui.PickFolder(this) is { } folder) _ = LoadFolderAsync(folder);
    }

    void ChooseAnother_Click(object sender, RoutedEventArgs e)
    {
        if (IsBatch && _batchFolder is not null) SelectFolder_Click(sender, e);
        else if (IsBatch) BatchAddVideos_Click(sender, e);
        else SelectVideo_Click(sender, e);
    }

    void Another_Click(object sender, RoutedEventArgs e)
    {
        if (IsBatch) SelectFolder_Click(sender, e);
        else SelectVideo_Click(sender, e);
    }

    public Task LoadFolderAsync(string folder)
    {
        if (IsBusy || _state.Tools is null) return Task.CompletedTask;
        var videos = ScanFolder(folder);
        if (videos.Count == 0)
        {
            string text = $"No videos found in \"{Path.GetFileName(folder.TrimEnd('\\')) ?? folder}\""
                + (_state.Settings.CompressSubfolders ? "." : ". Turn on \"Include subfolders\" to look deeper.");
            if (IsBatch) ShowError("Nothing to compress", text);
            else
            {
                EmptyError.Text = text;
                EmptyError.Visibility = Visibility.Visible;
            }
            return Task.CompletedTask;
        }
        return StartBatchAsync(folder, videos);
    }

    /// <summary>Videos of the folder, without earlier results of this app (own output folder, "_compressed" names).</summary>
    List<string> ScanFolder(string folder)
    {
        string outRoot = BatchOutputRoot(folder);
        string suffix = string.IsNullOrWhiteSpace(_state.Settings.Suffix) ? "_compressed" : _state.Settings.Suffix;
        return Ui.FindVideos(folder, _state.Settings.CompressSubfolders)
            .Where(f => !f.StartsWith(outRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    async Task StartBatchAsync(string? folder, List<string> videos)
    {
        WireBatch();
        // Leave the single-video editor.
        Player.Close();
        _video = null;
        _resultPath = null;
        _trimStart = TimeSpan.Zero;
        _trimEnd = null;
        CompareBar.Visibility = Visibility.Collapsed;

        _batch.Clear();
        _batchFolder = folder;
        _batchLastOutput = null;
        EmptyError.Visibility = Visibility.Collapsed;
        EmptyView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Visible;
        VideoArea.Visibility = Visibility.Collapsed;
        BatchArea.Visibility = Visibility.Visible;
        SubfoldersRow.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
        SubfoldersSwitch.IsChecked = _state.Settings.CompressSubfolders;
        BatchTitle.Text = folder is null ? "Selected videos" : Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } name ? name : folder;
        ApplyBatchTexts(true);
        ShowPanel(SettingsPanel);
        await AddBatchFilesAsync(videos);
    }

    void LeaveBatch()
    {
        if (!IsBatch) return;
        _batch.Clear();
        _batchFolder = null;
        BatchArea.Visibility = Visibility.Collapsed;
        VideoArea.Visibility = Visibility.Visible;
        ApplyBatchTexts(false);
    }

    void WireBatch()
    {
        if (_batchWired) return;
        _batchWired = true;
        BatchList.ItemsSource = _batch;
        _batch.CollectionChanged += (_, _) => UpdateBatchHeader();
    }

    /// <summary>Texts that differ between one video and a folder.</summary>
    void ApplyBatchTexts(bool batch)
    {
        EstimateLabel.Text = batch ? "Estimated total" : "Estimated size";
        EstimateSubLabel.Text = batch ? "Before" : "Output";
        ChooseAnotherButton.Content = !batch ? "Choose another video" : _batchFolder is not null ? "Choose another folder" : "Add more videos";
        AnotherButton.Content = !batch ? "Compress another video" : "Compress another folder";
        SaveMode.NewFileHint = batch && _batchFolder is not null
            ? $"The originals stay as they are; the smaller copies go to \"{Path.GetFileName(BatchOutputRoot(_batchFolder))}\"."
            : batch
                ? "The originals stay as they are; the smaller copies are saved next to them."
                : "The original stays as it is; the smaller copy is saved next to it.";
        if (!batch) CompressButtonText.Text = "Compress";
    }

    // =====================================================================
    // Queue
    // =====================================================================

    async Task AddBatchFilesAsync(IReadOnlyList<string> paths)
    {
        var added = new List<QueueItem>();
        foreach (var path in paths)
        {
            if (_batch.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new QueueItem(path);
            _batch.Add(item);
            added.Add(item);
        }
        UpdateBatchEstimate();

        // A folder can hold hundreds of clips: read a few at a time instead of starting hundreds of ffprobe processes.
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(added.Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                item.Info = await VideoEngine.ProbeAsync(_state.Tools!, item.Path);
                item.Status = QueueStatus.Ready;
            }
            catch (Exception ex)
            {
                item.Status = QueueStatus.Failed;
                item.StatusText = "Can't read file";
                item.Message = ex.Message;
            }
            finally
            {
                gate.Release();
            }
        }));
        UpdateBatchEstimate();
    }

    void BatchAddVideos_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var files = Ui.PickVideos(this);
        if (files.Length > 0) _ = AddBatchFilesAsync(files);
    }

    void RemoveBatchItem_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is QueueItem { CanRemove: true } item) RemoveBatchItem(item);
    }

    void RemoveBatchItem(QueueItem item)
    {
        _batch.Remove(item);
        if (_batch.Count == 0 && !IsBusy) ResetToStart();
        else UpdateBatchEstimate();
    }

    void BatchClearFinished_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _batch.Where(i => i.Status is QueueStatus.Done or QueueStatus.Skipped or QueueStatus.Failed).ToList())
            _batch.Remove(item);
        if (_batch.Count == 0) ResetToStart();
        else UpdateBatchEstimate();
    }

    void ResetToStart()
    {
        LeaveBatch();
        EditorView.Visibility = Visibility.Collapsed;
        EmptyView.Visibility = Visibility.Visible;
    }

    async void Subfolders_Click(object sender, RoutedEventArgs e)
    {
        _state.Settings.CompressSubfolders = SubfoldersSwitch.IsChecked == true;
        _state.Settings.Save();
        if (_batchFolder is null || IsBusy) return;
        // Read the folder again; finished rows are kept so the results stay visible.
        var finished = _batch.Where(i => i.Status == QueueStatus.Done).Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var videos = ScanFolder(_batchFolder);
        foreach (var item in _batch.Where(i => !videos.Contains(i.Path, StringComparer.OrdinalIgnoreCase) && i.Status != QueueStatus.Done).ToList())
            _batch.Remove(item);
        await AddBatchFilesAsync(videos.Where(v => !finished.Contains(v)).ToList());
    }

    bool HandleBatchKey(KeyEventArgs e)
    {
        if (e.Key == Key.Delete && !IsBusy && BatchList.SelectedItem is QueueItem { CanRemove: true } item)
        {
            RemoveBatchItem(item);
            return true;
        }
        return false;
    }

    void UpdateBatchHeader()
    {
        BatchCount.Text = _batch.Count.ToString(CultureInfo.InvariantCulture);
        long total = _batch.Where(i => i.Info is not null).Sum(i => i.Info!.SizeBytes);
        int reading = _batch.Count(i => i.Status == QueueStatus.Reading);
        BatchMeta.Text = string.Join("  ·  ", new[]
        {
            _batchFolder,
            _batch.Count == 1 ? "1 video" : $"{_batch.Count} videos",
            total > 0 ? Format.Size(total) : null,
            reading > 0 ? $"reading {reading}…" : null,
        }.Where(s => s is not null));
        BatchClearButton.Visibility = _batch.Any(i => i.Status is QueueStatus.Done or QueueStatus.Skipped or QueueStatus.Failed)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // =====================================================================
    // Estimate
    // =====================================================================

    /// <summary>Why a video is left out with the current settings, or null when it gets compressed.</summary>
    static string? SkipReason(VideoInfo info, CompressOptions options) =>
        options.Mode == QualityMode.TargetSize && options.TargetMb > 0 && info.SizeBytes <= options.TargetMb * 1024 * 1024
            ? $"Already under {options.TargetMb.ToString("0.##", CultureInfo.InvariantCulture)} MB"
            : null;

    void UpdateBatchEstimate()
    {
        UpdateBatchHeader();
        if (BusyOrRestoring) return;
        var options = ReadOptions();
        long before = 0, after = 0;
        int todo = 0;
        string? problem = null;

        foreach (var item in _batch.Where(i => i.Status == QueueStatus.Ready && i.Info is not null))
        {
            item.Message = null;
            if (SkipReason(item.Info!, options) is { } reason)
            {
                item.StatusText = reason;
                continue;
            }
            try
            {
                var plan = VideoEngine.CreatePlan(item.Info!, options, _state.Encoders, "out.mp4", Path.GetTempPath());
                item.StatusText = $"≈ {Format.Size(plan.EstimatedBytes)}";
                before += item.Info!.SizeBytes;
                after += Math.Min(plan.EstimatedBytes, item.Info.SizeBytes);
                todo++;
            }
            catch (InvalidOperationException ex)
            {
                item.StatusText = "Can't compress";
                item.Message = ex.Message;
                problem ??= ex.Message;
            }
        }

        double ratio = 1 - (double)after / Math.Max(1, before);
        EstimateText.Text = todo > 0 ? $"≈ {Format.Size(after)}" + (ratio > 0.01 ? $"  (−{ratio * 100:0}%)" : "") : "—";
        EstimateSub.Text = todo > 0 ? Format.Size(before) : "—";
        CompressButtonText.Text = todo == 1 ? "Compress 1 video" : $"Compress {todo} videos";
        CompressButton.IsEnabled = todo > 0;

        int reading = _batch.Count(i => i.Status == QueueStatus.Reading);
        int skipped = _batch.Count(i => i.Status == QueueStatus.Ready && i.StatusText.StartsWith("Already", StringComparison.Ordinal));
        PlanNote.Foreground = (System.Windows.Media.Brush)FindResource(problem is not null ? "Warning" : "Text2");
        PlanNote.Text = reading > 0 ? $"Reading {reading} more video{(reading == 1 ? "" : "s")}…"
            : problem is not null ? $"Some videos can't be compressed with these settings (hover over them): {problem}"
            : skipped > 0 ? $"{skipped} video{(skipped == 1 ? " is" : "s are")} already small enough and will be skipped."
            : "";
        PlanNote.Visibility = PlanNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    bool BusyOrRestoring => IsBusy || _restoringOptions || _state is null;

    // =====================================================================
    // Output paths
    // =====================================================================

    /// <summary>"D:\Clips" → "D:\Clips (compressed)", or inside the custom output folder from the settings.</summary>
    string BatchOutputRoot(string folder)
    {
        folder = folder.TrimEnd('\\');
        string name = Path.GetFileName(folder);
        if (string.IsNullOrEmpty(name)) name = "Drive " + folder.TrimEnd(':');
        var custom = _state.Settings.OutputFolder;
        if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom)) return Path.Combine(custom, name + " (compressed)");
        return Directory.GetParent(folder) is { } parent
            ? Path.Combine(parent.FullName, name + " (compressed)")
            : Path.Combine(folder + "\\", "Compressed"); // a drive root has no parent
    }

    string BatchOutputPath(QueueItem item, bool replace)
    {
        // Replace mode and loose videos: next to the original, like a single video.
        if (replace || _batchFolder is null) return _state.Settings.BuildOutputPath(item.Path);

        var relative = Path.GetRelativePath(_batchFolder, Path.GetDirectoryName(item.Path)!);
        var dir = Path.GetFullPath(Path.Combine(BatchOutputRoot(_batchFolder), relative));
        Directory.CreateDirectory(dir);
        var name = Path.GetFileNameWithoutExtension(item.Path);
        var path = Path.Combine(dir, name + ".mp4");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{name} ({i}).mp4");
        return path;
    }

    // =====================================================================
    // Compressing the queue
    // =====================================================================

    async Task CompressBatchAsync()
    {
        if (_state.Tools is null || IsBusy) return;
        var options = ReadOptions();
        var todo = _batch.Where(i => i.Status == QueueStatus.Ready && i.Info is not null).ToList();
        if (todo.Count == 0) return;

        bool replace = _state.Settings.ReplaceOriginal;
        _jobCts = new CancellationTokenSource();
        BatchAddButton.IsEnabled = false;
        SubfoldersSwitch.IsEnabled = false;
        ShowPanel(ProgressPanel);
        ProgressPanel.Start();

        long before = 0, after = 0;
        bool cancelled = false, anyHardwareFailure = false;

        for (int n = 0; n < todo.Count && !cancelled; n++)
        {
            var item = todo[n];
            if (!_batch.Contains(item)) continue; // removed while waiting
            var info = item.Info!;
            ProgressPanel.Title = todo.Count == 1 ? "Compressing" : $"Compressing {n + 1} of {todo.Count}";

            if (SkipReason(info, options) is { } reason)
            {
                item.Status = QueueStatus.Skipped;
                item.StatusText = $"Skipped · {char.ToLowerInvariant(reason[0])}{reason[1..]}";
                continue;
            }

            BatchList.SelectedItem = item;
            BatchList.ScrollIntoView(item);
            item.Status = QueueStatus.Processing;
            item.Progress = 0;
            item.StatusText = "0%";
            EncodePlan? plan = null;

            try
            {
                plan = VideoEngine.CreatePlan(info, options, _state.Encoders, BatchOutputPath(item, replace),
                    Path.Combine(Path.GetTempPath(), "compress-" + Guid.NewGuid().ToString("N")));
                await RunBatchPlanAsync(item, plan, "");

                // Same safety net as for one video: if a preset made it bigger, retry with a hard size budget.
                if (options.Mode != QualityMode.TargetSize && new FileInfo(plan.OutputPath).Length >= info.SizeBytes * 0.97
                    && SizeLimitedRetry(info, options, info.SizeBytes) is { } retry)
                {
                    File.Delete(plan.OutputPath);
                    plan = VideoEngine.CreatePlan(info, retry, _state.Encoders, plan.OutputPath,
                        Path.Combine(Path.GetTempPath(), "compress-" + Guid.NewGuid().ToString("N")));
                    await RunBatchPlanAsync(item, plan, "Size limit · ");
                }

                long size = new FileInfo(plan.OutputPath).Length;
                if (size >= info.SizeBytes)
                {
                    // Never keep (or swap in) a bigger file: this one is already as small as it gets.
                    File.Delete(plan.OutputPath);
                    item.Status = QueueStatus.Skipped;
                    item.StatusText = "Skipped · already optimal";
                    continue;
                }

                string final = plan.OutputPath;
                if (replace)
                {
                    var original = item.Path;
                    string? note;
                    (final, note) = await OutputFiles.ReplaceOriginalAsync(original, plan.OutputPath);
                    PreviewAudio.Forget(original);
                    PreviewProxy.Forget(original);
                    if (note is null) item.Path = final;
                    else item.Message = note;
                }

                item.OutputPath = final;
                item.Status = QueueStatus.Done;
                double ratio = 1 - (double)size / Math.Max(1, info.SizeBytes);
                item.StatusText = $"{Format.Size(info.SizeBytes)} → {Format.Size(size)} · −{ratio * 100:0}%";
                before += info.SizeBytes;
                after += size;
                _batchLastOutput = final;
                Usage.Export("compress");
            }
            catch (OperationCanceledException)
            {
                item.Status = QueueStatus.Ready;
                item.Progress = 0;
                cancelled = true;
            }
            catch (Exception ex)
            {
                anyHardwareFailure |= plan?.IsHardware == true;
                item.Status = QueueStatus.Failed;
                item.StatusText = "Failed";
                item.Message = ex.Message + (plan?.IsHardware == true ? "\n\nTip: turn off GPU acceleration under Advanced settings and try again." : "");
            }
        }

        ProgressPanel.Stop();
        var elapsed = ProgressPanel.Elapsed;
        _jobCts.Dispose();
        _jobCts = null;
        _jobOutputPath = null;
        BatchAddButton.IsEnabled = true;
        SubfoldersSwitch.IsEnabled = true;
        UpdateBatchEstimate();

        if (cancelled)
        {
            ShowPanel(SettingsPanel);
            return;
        }
        ShowBatchDone(todo, before, after, elapsed, replace, anyHardwareFailure);
        Ui.NotifyFinished(this);
    }

    async Task RunBatchPlanAsync(QueueItem item, EncodePlan plan, string stagePrefix)
    {
        _jobOutputPath = plan.OutputPath;
        ProgressPanel.BeginStage($"{item.FileName}\n{stagePrefix}{plan.EncoderLabel} · {plan.Width}×{plan.Height} · {Format.Fps(plan.Fps)} fps", plan.Passes.Count);
        var progress = new Progress<EncodeProgress>(p =>
        {
            if (_jobCts is null || item.Status != QueueStatus.Processing) return;
            item.Progress = p.Fraction;
            item.StatusText = $"{Math.Floor(p.Fraction * 100):0}%";
            ProgressPanel.Report(p);
        });
        await VideoEngine.RunAsync(_state.Tools!, plan, progress, _jobCts!.Token);
    }

    void ShowBatchDone(List<QueueItem> processed, long before, long after, TimeSpan elapsed, bool replaced, bool hardwareFailure)
    {
        int done = processed.Count(i => i.Status == QueueStatus.Done);
        int skipped = processed.Count(i => i.Status == QueueStatus.Skipped);
        int failed = processed.Count(i => i.Status == QueueStatus.Failed);

        DoneTitle.Text = failed == 0 ? (_batchFolder is not null ? "Folder compressed" : "All done")
            : done > 0 ? "Finished with errors" : "Compression failed";
        var parts = new List<string> { done == 1 ? "1 video compressed" : $"{done} videos compressed" };
        if (skipped > 0) parts.Add($"{skipped} skipped");
        if (failed > 0) parts.Add($"{failed} failed (hover over it in the queue)");
        string where = done == 0 ? ""
            : replaced ? " The originals are in the Recycle Bin."
            : _batchFolder is not null ? $" Saved to \"{Path.GetFileName(BatchOutputRoot(_batchFolder))}\"."
            : " Saved next to the originals.";
        DoneFileText.Text = string.Join(" · ", parts) + "." + where;

        double ratio = 1 - (double)after / Math.Max(1, before);
        BeforeText.Text = done > 0 ? Format.Size(before) : "—";
        AfterText.Text = done > 0 ? Format.Size(after) : "—";
        ReductionText.Text = done > 0 ? $"−{ratio * 100:0}%" : "0%";
        ReductionText.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
        ReductionLabel.Text = done > 0 ? $"smaller · {Format.Size(before - after)} saved" : "smaller";
        DoneDetails.Text = $"took {Format.Time(elapsed)}";
        DoneWarning.Text = hardwareFailure ? "Tip: turn off GPU acceleration under Advanced settings and try the failed videos again." : "";
        DoneWarning.Visibility = hardwareFailure ? Visibility.Visible : Visibility.Collapsed;

        CopyFileButton.Visibility = Visibility.Collapsed;
        TryOtherButton.Visibility = Visibility.Collapsed;
        ShowPanel(DonePanel);
    }

    void ShowBatchInFolder()
    {
        if (!_state.Settings.ReplaceOriginal && _batchFolder is not null && Directory.Exists(BatchOutputRoot(_batchFolder)))
            Ui.OpenFolder(BatchOutputRoot(_batchFolder));
        else Ui.ShowInExplorer(_batchLastOutput);
    }
}
