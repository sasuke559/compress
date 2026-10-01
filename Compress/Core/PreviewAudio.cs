namespace Compress.Core;

/// <summary>
/// Windows' media player only plays one audio track, so clips with game and mic on separate tracks
/// (NVIDIA app, OBS) would preview with half the sound. This writes every track to its own temporary WAV;
/// the player runs them in sync with the picture, each with its own volume. Decoding is fast (about a second for 10 minutes).
/// </summary>
public static class PreviewAudio
{
    const int MaxCached = 4;

    static readonly Dictionary<string, Task<IReadOnlyList<string>?>> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<string> Order = [];

    /// <summary>One WAV per audio track of <paramref name="videoPath"/>, or null when it has fewer than two.</summary>
    public static Task<IReadOnlyList<string>?> GetAsync(FfmpegTools tools, string videoPath)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(videoPath, out var existing)) return existing;
            var task = BuildAsync(tools, videoPath);
            Cache[videoPath] = task;
            Order.Add(videoPath);
            // Each track is ~110 MB per 10 minutes, so only keep the last few videos.
            while (Order.Count > MaxCached)
            {
                var old = Order[0];
                Order.RemoveAt(0);
                if (Cache.Remove(old, out var oldTask)) _ = DeleteWhenDoneAsync(oldTask);
            }
            return task;
        }
    }

    /// <summary>Drops the cached tracks of a file that was replaced, so its new content gets fresh ones.</summary>
    public static void Forget(string videoPath)
    {
        lock (Cache)
        {
            Order.Remove(videoPath);
            if (Cache.Remove(videoPath, out var task)) _ = DeleteWhenDoneAsync(task);
        }
    }

    static async Task<IReadOnlyList<string>?> BuildAsync(FfmpegTools tools, string videoPath)
    {
        try
        {
            var (code, streams, _) = await FfmpegTools.RunCaptureAsync(tools.Ffprobe,
                ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", videoPath]);
            int tracks = code == 0 ? streams.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length : 0;
            if (tracks < 2) return null;

            var id = Guid.NewGuid().ToString("N");
            var files = Enumerable.Range(0, tracks).Select(i => Path.Combine(Path.GetTempPath(), $"compress-mix-{id}-{i}.wav")).ToList();
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", videoPath };
            for (int i = 0; i < tracks; i++) args.AddRange(["-map", $"0:a:{i}", "-vn", "-c:a", "pcm_s16le", files[i]]);
            var (splitCode, _, _) = await FfmpegTools.RunCaptureAsync(tools.Ffmpeg, args);
            if (splitCode == 0 && files.All(File.Exists)) return files;
            files.ForEach(TryDelete);
            return null;
        }
        catch
        {
            return null; // preview falls back to the first track
        }
    }

    static async Task DeleteWhenDoneAsync(Task<IReadOnlyList<string>?> task)
    {
        // Give a player that still uses the files a moment to let go of them.
        if (await task is { } files)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            foreach (var file in files) TryDelete(file);
        }
    }

    /// <summary>Deletes every mix, including ones left over from earlier sessions (on exit).</summary>
    public static void Cleanup()
    {
        lock (Cache)
        {
            Cache.Clear();
            Order.Clear();
        }
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), "compress-mix-*.wav")) TryDelete(file);
        }
        catch { /* temp folder not readable */ }
    }

    static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { /* still open or already gone */ }
    }
}
