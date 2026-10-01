namespace Compress.Core;

/// <summary>
/// Windows' media player garbles videos that are stored sideways with a rotation flag (phone videos, e.g. iPhone
/// portrait .MOV): parts of the picture end up duplicated and turned. For those, the preview plays a quickly made,
/// upright copy instead. Exports always use the original file.
/// </summary>
public static class PreviewProxy
{
    const int MaxCached = 4;
    const string Prefix = "compress-proxy-";

    static readonly Dictionary<string, Task<string?>> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<string> Order = [];

    /// <summary>File the preview should play for <paramref name="videoPath"/>: an upright copy, or null for the original.</summary>
    public static Task<string?> GetAsync(FfmpegTools tools, string videoPath)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(videoPath, out var existing)) return existing;
            var task = BuildAsync(tools, videoPath);
            Cache[videoPath] = task;
            Order.Add(videoPath);
            while (Order.Count > MaxCached)
            {
                var old = Order[0];
                Order.RemoveAt(0);
                if (Cache.Remove(old, out var oldTask)) _ = DeleteWhenDoneAsync(oldTask);
            }
            return task;
        }
    }

    /// <summary>Drops the copy of a file that was replaced, so its new content gets a fresh one.</summary>
    public static void Forget(string videoPath)
    {
        lock (Cache)
        {
            Order.Remove(videoPath);
            if (Cache.Remove(videoPath, out var task)) _ = DeleteWhenDoneAsync(task);
        }
    }

    static async Task<string?> BuildAsync(FfmpegTools tools, string videoPath)
    {
        try
        {
            var info = await VideoEngine.ProbeAsync(tools, videoPath);
            if (info.Rotation % 360 == 0) return null;

            // FFmpeg turns the picture upright by itself; small and fast to decode, keyframes often for smooth scrubbing.
            var file = Path.Combine(Path.GetTempPath(), $"{Prefix}{Guid.NewGuid():N}.mp4");
            var (code, _, _) = await FfmpegTools.RunCaptureAsync(tools.Ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-y", "-i", videoPath,
                "-map", "0:v:0", "-map", "0:a:0?",
                "-vf", "scale=1280:1280:force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuv420p",
                "-c:v", "libx264", "-preset", "ultrafast", "-tune", "fastdecode", "-crf", "24", "-g", "15",
                "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", file,
            ]);
            if (code == 0 && File.Exists(file)) return file;
            TryDelete(file);
            return null;
        }
        catch
        {
            return null; // fall back to the original
        }
    }

    static async Task DeleteWhenDoneAsync(Task<string?> task)
    {
        if (await task is { } file)
        {
            await Task.Delay(TimeSpan.FromSeconds(5)); // let a player that still uses it let go
            TryDelete(file);
        }
    }

    /// <summary>Deletes every copy, including ones left over from earlier sessions.</summary>
    public static void Cleanup()
    {
        lock (Cache)
        {
            Cache.Clear();
            Order.Clear();
        }
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), Prefix + "*.mp4")) TryDelete(file);
        }
        catch { /* temp folder not readable */ }
    }

    static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { /* in use or gone */ }
    }
}
