using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Compress.Core;

/// <summary>Which hardware encoders and optional filters actually work on this machine.</summary>
public sealed class EncoderSupport
{
    public HwVendor H264 { get; init; }
    public HwVendor Hevc { get; init; }
    /// <summary>zscale + tonemap are available, so HDR sources can be converted to SDR properly.</summary>
    public bool CanTonemap { get; init; }

    public static EncoderSupport None { get; } = new();

    public HwVendor VendorFor(CodecChoice codec) => codec == CodecChoice.H265 ? Hevc : H264;

    public bool Any => H264 != HwVendor.None || Hevc != HwVendor.None;

    public static async Task<EncoderSupport> DetectAsync(FfmpegTools tools)
    {
        var (_, list, _) = await FfmpegTools.RunCaptureAsync(tools.Ffmpeg, ["-hide_banner", "-encoders"]);
        var (_, filters, _) = await FfmpegTools.RunCaptureAsync(tools.Ffmpeg, ["-hide_banner", "-filters"]);

        async Task<HwVendor> FindAsync(CodecChoice codec)
        {
            foreach (var vendor in new[] { HwVendor.Nvidia, HwVendor.Intel, HwVendor.Amd })
            {
                var name = VideoEngine.EncoderName(vendor, codec);
                if (list.Contains(" " + name + " ") && await TestAsync(tools, name)) return vendor;
            }
            return HwVendor.None;
        }

        var h264 = FindAsync(CodecChoice.H264);
        var hevc = FindAsync(CodecChoice.H265);
        return new EncoderSupport
        {
            H264 = await h264,
            Hevc = await hevc,
            CanTonemap = filters.Contains(" zscale ") && filters.Contains(" tonemap "),
        };
    }

    static async Task<bool> TestAsync(FfmpegTools tools, string encoder)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var (code, _, _) = await FfmpegTools.RunCaptureAsync(tools.Ffmpeg,
            [
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=c=black:s=640x360:r=30",
                "-frames:v", "5", "-pix_fmt", encoder.EndsWith("_qsv") ? "nv12" : "yuv420p",
                "-c:v", encoder, "-f", "null", "-",
            ], cts.Token);
            return code == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Everything needed to run one compression job.</summary>
public sealed class EncodePlan
{
    public required List<List<string>> Passes { get; init; }
    public required string WorkDir { get; init; }
    public required string OutputPath { get; init; }
    public required string EncoderLabel { get; init; }
    public TimeSpan Duration { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public long EstimatedBytes { get; init; }
    public string? Note { get; init; }
    public bool IsHardware { get; init; }
}

public static partial class VideoEngine
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly ConcurrentDictionary<int, Process> Running = new();

    /// <summary>HDR (PQ / HLG) to SDR BT.709.</summary>
    const string TonemapChain =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    /// <summary>Share of the source video bitrate a preset may use at most, so output never balloons.</summary>
    static double CapRatio(QualityMode mode) => mode switch
    {
        QualityMode.Source => 1.0,
        QualityMode.High => 0.85,
        QualityMode.Low => 0.40,
        _ => 0.60,
    };

    /// <summary>
    /// Mix all audio tracks into one. The NVIDIA app and OBS can record game and mic on separate tracks, but most players
    /// (Discord, phones, Windows) only play the first one. Off = keep every track separately (for editing). Set from the settings.
    /// </summary>
    public static bool MixAudioTracks { get; set; } = true;

    /// <summary>filter_complex graph that adds up the audio tracks of input 0 into [aout] at full volume, like an editor plays them.</summary>
    public static string AudioMixGraph(int tracks) =>
        string.Concat(Enumerable.Range(0, tracks).Select(i => $"[0:a:{i}]")) +
        $"amix=inputs={tracks}:duration=longest:dropout_transition=0:normalize=0[aout]";

    /// <summary>
    /// Audio for a re-encode: one mixed track, every track separately, or the only track, each at its own volume
    /// (<paramref name="volumes"/>: 1 = unchanged, 0 = left out). A graph goes into the command's -filter_complex
    /// (works next to -vf). An empty map means no audio at all (every track muted).
    /// </summary>
    static (string? Graph, string[] Map) AudioSource(VideoInfo v, IReadOnlyList<double>? volumes = null)
    {
        int n = v.AudioTracks;
        if (n < 2) return (null, ["-map", "0:a:0"]);

        var vol = Enumerable.Range(0, n).Select(i => volumes is not null && i < volumes.Count ? Math.Clamp(volumes[i], 0, 4) : 1.0).ToArray();
        var active = Enumerable.Range(0, n).Where(i => vol[i] > 0.001).ToArray();
        if (active.Length == 0) return (null, []);
        bool unchanged = active.Length == n && vol.All(x => Math.Abs(x - 1) < 0.001);

        if (!MixAudioTracks)
        {
            if (unchanged) return (null, ["-map", "0:a"]);
            return (string.Join(";", active.Select(i => $"[0:a:{i}]volume={vol[i].ToString("0.###", Inv)}[t{i}]")),
                    active.SelectMany(i => new[] { "-map", $"[t{i}]" }).ToArray());
        }
        if (unchanged) return (AudioMixGraph(n), ["-map", "[aout]"]);

        var graph = string.Join(";", active.Select(i => $"[0:a:{i}]volume={vol[i].ToString("0.###", Inv)}[t{i}]")) + ";" +
                    string.Concat(active.Select(i => $"[t{i}]")) +
                    (active.Length > 1 ? $"amix=inputs={active.Length}:duration=longest:dropout_transition=0:normalize=0[aout]" : "anull[aout]");
        return (graph, ["-map", "[aout]"]);
    }

    /// <summary>Kills every ffmpeg process this app started (used when the window closes mid-job).</summary>
    public static void KillAll()
    {
        foreach (var p in Running.Values)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already exited */ }
        }
    }

    // ---------------------------------------------------------------- probe

    public static async Task<VideoInfo> ProbeAsync(FfmpegTools tools, string path)
    {
        var (code, json, err) = await FfmpegTools.RunCaptureAsync(tools.Ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path]);
        if (code != 0 || string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException(string.IsNullOrWhiteSpace(err) ? "This file could not be read." : err.Trim());

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement? video = null, audio = null;
        int audioTracks = 0;
        var trackTitles = new List<string>();

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type");
                bool isCover = s.TryGetProperty("disposition", out var d) && Num(d, "attached_pic") == 1;
                if (type == "video" && video is null && !isCover) video = s;
                else if (type == "audio")
                {
                    audio ??= s;
                    audioTracks++;
                    trackTitles.Add(s.TryGetProperty("tags", out var at) ? Str(at, "title") : "");
                }
            }
        }
        if (video is not { } v) throw new InvalidDataException("No video stream was found in this file.");

        root.TryGetProperty("format", out var format);
        double duration = Num(format, "duration");
        if (duration <= 0) duration = Num(v, "duration");

        double fps = Rate(Str(v, "avg_frame_rate"));
        if (fps <= 0 || fps > 1000) fps = Rate(Str(v, "r_frame_rate"));

        int rotation = 0;
        if (v.TryGetProperty("side_data_list", out var sideData))
            foreach (var sd in sideData.EnumerateArray())
                if (sd.TryGetProperty("rotation", out var r) && r.TryGetDouble(out var rot)) rotation = (int)rot;
        if (rotation == 0 && v.TryGetProperty("tags", out var tags) && int.TryParse(Str(tags, "rotate"), out var tagRot))
            rotation = tagRot;

        long size = (long)Num(format, "size");
        if (size <= 0) size = new FileInfo(path).Length;
        long bitrate = (long)Num(format, "bit_rate");
        if (bitrate <= 0 && duration > 0) bitrate = (long)(size * 8 / duration);

        long audioBitrate = audio is { } au ? (long)Num(au, "bit_rate") : 0;
        long videoBitrate = (long)Num(v, "bit_rate"); // missing for MKV/WebM
        if (videoBitrate <= 0 && bitrate > 0)
            videoBitrate = Math.Max(0, bitrate - (audio is null ? 0 : audioBitrate > 0 ? audioBitrate : 160_000));

        string transfer = Str(v, "color_transfer");

        return new VideoInfo
        {
            Path = path,
            SizeBytes = size,
            Duration = TimeSpan.FromSeconds(Math.Max(0, duration)),
            Width = (int)Num(v, "width"),
            Height = (int)Num(v, "height"),
            Rotation = rotation,
            Fps = fps > 0 ? fps : 30,
            VideoCodec = Str(v, "codec_name"),
            AudioCodec = audio is { } ac ? Str(ac, "codec_name") : "",
            AudioTracks = audioTracks,
            AudioTrackTitles = trackTitles,
            BitRate = bitrate,
            VideoBitRate = videoBitrate,
            AudioBitRate = audioBitrate,
            HasAudio = audio is not null,
            AudioChannels = audio is { } a ? (int)Num(a, "channels") : 0,
            IsHdr = transfer is "smpte2084" or "arib-std-b67",
        };
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
            ? p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.ToString()
            : "";

    static double Num(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, Inv, out var d) ? d : 0;

    static double Rate(string r)
    {
        var parts = r.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, Inv, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, Inv, out var dd) && dd > 0)
            return n / dd;
        return double.TryParse(r, NumberStyles.Float, Inv, out var x) ? x : 0;
    }

    public static Task<string?> ExtractPosterAsync(FfmpegTools tools, VideoInfo video) =>
        ExtractFrameAsync(tools, video.Path, Math.Min(1.0, video.Duration.TotalSeconds / 2), "scale='min(1600,iw)':-2");

    /// <summary>Saves a single frame as a temporary JPEG and returns its path.</summary>
    public static async Task<string?> ExtractFrameAsync(FfmpegTools tools, string path, double seconds, string scaleFilter)
    {
        var file = Path.Combine(Path.GetTempPath(), $"compress-frame-{Guid.NewGuid():N}.jpg");
        var (code, _, _) = await FfmpegTools.RunCaptureAsync(tools.Ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-ss", Math.Max(0, seconds).ToString("0.###", Inv), "-i", path,
            "-frames:v", "1", "-vf", scaleFilter, "-q:v", "4", file,
        ]);
        return code == 0 && File.Exists(file) ? file : null;
    }

    // ---------------------------------------------------------------- cutting

    /// <summary>Where a lossless cut at <paramref name="time"/> really starts: the last keyframe at or before it.</summary>
    public static async Task<TimeSpan?> FindKeyframeAtOrBeforeAsync(FfmpegTools tools, string path, TimeSpan time, CancellationToken ct = default)
    {
        if (time <= TimeSpan.Zero) return TimeSpan.Zero;
        double from = Math.Max(0, time.TotalSeconds - 20);
        var (code, output, _) = await FfmpegTools.RunCaptureAsync(tools.Ffprobe,
        [
            "-v", "error", "-select_streams", "v:0", "-skip_frame", "nokey",
            "-show_entries", "frame=pts_time,best_effort_timestamp_time", "-of", "csv=p=0",
            "-read_intervals", $"{from.ToString("0.###", Inv)}%{(time.TotalSeconds + 0.05).ToString("0.###", Inv)}", path,
        ], ct);
        if (code != 0) return null;

        double best = -1;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var field in line.Split(','))
                if (double.TryParse(field, NumberStyles.Float, Inv, out var t) && t <= time.TotalSeconds + 0.001 && t > best)
                    best = t;
        return best >= 0 ? TimeSpan.FromSeconds(best) : TimeSpan.Zero;
    }

    static readonly string[] CopyFriendlyContainers = [".mp4", ".mov", ".m4v", ".mkv", ".webm", ".ts", ".mts", ".m2ts", ".avi"];

    /// <summary>Container for a lossless cut: keep the source's own container when stream copy into it is safe.</summary>
    public static string CopyCutExtension(string sourcePath)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        return CopyFriendlyContainers.Contains(ext) ? ext : ".mkv";
    }

    /// <summary>
    /// Lossless cut via stream copy. Very fast, but the start snaps to the keyframe at or before <paramref name="start"/>.
    /// </summary>
    public static EncodePlan CreateCopyCutPlan(VideoInfo v, TimeSpan start, TimeSpan? end, bool removeAudio, string outputPath, string workDir,
        IReadOnlyList<double>? trackVolumes = null)
    {
        if (start < TimeSpan.Zero || start >= v.Duration) start = TimeSpan.Zero;
        var stop = end is { } e && e > start && e < v.Duration ? e : v.Duration;
        var clip = stop - start;
        bool mp4Like = Path.GetExtension(outputPath).ToLowerInvariant() is ".mp4" or ".mov" or ".m4v";

        var args = new List<string> { "-hide_banner", "-y" };
        if (start > TimeSpan.Zero) args.AddRange(["-ss", start.TotalSeconds.ToString("0.###", Inv)]);
        args.AddRange(["-i", v.Path]);
        if (stop < v.Duration) args.AddRange(["-t", clip.TotalSeconds.ToString("0.###", Inv)]);
        args.AddRange(["-map", "0:v:0"]);
        // Separate game/mic tracks get mixed into one (or get their own volume); only the sound is re-encoded then,
        // the picture stays lossless.
        var (audioGraph, audioMap) = removeAudio || !v.HasAudio ? (null, []) : AudioSource(v, trackVolumes);
        bool mixAudio = audioGraph is not null;
        if (audioMap.Length == 0) args.Add("-an");
        else if (mixAudio) args.AddRange(["-filter_complex", audioGraph!, .. audioMap]);
        else args.AddRange(["-map", "0:a?"]);
        args.AddRange(["-c", "copy"]);
        if (mixAudio)
        {
            args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
            if (v.AudioChannels > 2) args.AddRange(["-ac", "2"]);
        }
        args.AddRange(["-avoid_negative_ts", "make_zero", "-map_metadata", "0"]);
        if (mp4Like) args.AddRange(["-movflags", "+faststart"]);
        args.Add(outputPath);

        double share = v.Duration.TotalSeconds > 0 ? clip.TotalSeconds / v.Duration.TotalSeconds : 1;
        return new EncodePlan
        {
            Passes = [args],
            WorkDir = workDir,
            OutputPath = outputPath,
            EncoderLabel = mixAudio ? "Stream copy · audio tracks adjusted" : "Stream copy · lossless",
            Duration = clip,
            Width = v.DisplayWidth,
            Height = v.DisplayHeight,
            Fps = v.Fps,
            EstimatedBytes = (long)(v.SizeBytes * share),
        };
    }

    // ---------------------------------------------------------------- planning

    public static string EncoderName(HwVendor vendor, CodecChoice codec)
    {
        bool hevc = codec == CodecChoice.H265;
        return vendor switch
        {
            HwVendor.Nvidia => hevc ? "hevc_nvenc" : "h264_nvenc",
            HwVendor.Intel => hevc ? "hevc_qsv" : "h264_qsv",
            HwVendor.Amd => hevc ? "hevc_amf" : "h264_amf",
            _ => hevc ? "libx265" : "libx264",
        };
    }

    static int Even(double x) => Math.Max(2, (int)Math.Round(x / 2) * 2);

    static (int W, int H) Fit(int w, int h, int? maxShortSide)
    {
        int shortSide = Math.Min(w, h);
        if (maxShortSide is not { } cap || shortSide <= cap) return (Even(w), Even(h));
        double scale = (double)cap / shortSide;
        return (Even(w * scale), Even(h * scale));
    }

    /// <summary>Scale filter that brings any video to exactly width x height.</summary>
    public static string ResizeFilter(int width, int height, ScaleMode mode) => mode switch
    {
        // Fill the frame and distort the image, e.g. 4:3 stretched gameplay to 16:9.
        ScaleMode.Stretch => $"scale={width}:{height}:flags=lanczos,setsar=1",
        // Keep proportions, pad with black bars.
        ScaleMode.Fit => $"scale={width}:{height}:force_original_aspect_ratio=decrease:flags=lanczos," +
                          $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black,setsar=1",
        // Keep proportions, fill the frame and cut off what sticks out.
        _ => $"scale={width}:{height}:force_original_aspect_ratio=increase:flags=lanczos,crop={width}:{height},setsar=1",
    };
    /// <summary>Builds the ffmpeg command lines for a job. Throws InvalidOperationException for impossible settings.</summary>
    public static EncodePlan CreatePlan(VideoInfo v, CompressOptions o, EncoderSupport support, string outputPath, string workDir)
    {
        var vendor = o.UseHardware ? support.VendorFor(o.Codec) : HwVendor.None;
        bool hevc = o.Codec == CodecChoice.H265;

        var start = o.TrimStart < TimeSpan.Zero || o.TrimStart >= v.Duration ? TimeSpan.Zero : o.TrimStart;
        var end = o.TrimEnd is { } te && te > start && te < v.Duration ? te : v.Duration;
        bool trimmed = start > TimeSpan.Zero || end < v.Duration;
        var clip = end - start;
        double duration = Math.Max(0.1, clip.TotalSeconds);

        double srcFps = v.Fps > 0 ? v.Fps : 30;
        double? maxFps = o.MaxFps ?? (o.Mode == QualityMode.Low ? 60 : null);
        double fps = maxFps is { } mf && srcFps > mf + 0.5 ? mf : srcFps;

        bool keepAudio = v.HasAudio && !o.RemoveAudio;
        int audioKbps = !keepAudio ? 0 : o.Mode switch { QualityMode.High or QualityMode.Source => 192, QualityMode.Low => 96, _ => 128 };
        // Never spend more on audio than the source had.
        if (keepAudio && v.AudioBitRate > 0) audioKbps = Math.Min(audioKbps, Math.Max(64, (int)Math.Ceiling(v.AudioBitRate / 1000.0 / 16) * 16));

        int? shortCap = o.MaxShortSide;
        long videoKbps = 0;
        string? note = null;

        if (o.Mode == QualityMode.Low && shortCap is null && o.ResizeTo is null) shortCap = 720;

        if (o.Mode == QualityMode.TargetSize)
        {
            if (o.TargetMb <= 0) throw new InvalidOperationException("Enter a target size in MB.");
            double totalKbps = o.TargetMb * 1024 * 1024 * 8 / 1000 / duration * 0.96; // container overhead
            if (keepAudio) audioKbps = Math.Min(audioKbps, totalKbps < 400 ? 48 : totalKbps < 1200 ? 96 : 128);
            videoKbps = (long)(totalKbps - audioKbps);
            if (videoKbps < 60)
            {
                double minMb = Math.Ceiling((60 + audioKbps) * 1000 * duration / 8 / 1024 / 1024 / 0.96 * 10) / 10;
                throw new InvalidOperationException(
                    $"{o.TargetMb.ToString("0.##", Inv)} MB is too small for a {Format.Time(clip)} clip. Try at least {minMb.ToString("0.#", Inv)} MB.");
            }

            double minBppTarget = hevc ? 0.03 : 0.045;
            if (o.MaxFps is null && fps > 60.5 && videoKbps * 1000.0 / ((double)v.DisplayWidth * v.DisplayHeight * fps) < minBppTarget)
            {
                // High-refresh clips: halve the frame rate before sacrificing resolution.
                fps = 60;
                note = "Frame rate lowered to 60 fps to keep the image sharp. ";
            }

            if (shortCap is null && o.ResizeTo is null)
            {
                // Pick the largest resolution that still gets a sensible amount of bits per pixel.
                int?[] ladder = [null, 1080, 720, 540, 480, 360];
                foreach (var cap in ladder)
                {
                    var (tw, th) = Fit(v.DisplayWidth, v.DisplayHeight, cap);
                    shortCap = cap;
                    if (videoKbps * 1000.0 / (tw * th * fps) >= minBppTarget) break;
                }
                if (shortCap is { } chosen && chosen < Math.Min(v.DisplayWidth, v.DisplayHeight))
                    note += $"Scaled to {chosen}p so the video still looks clean at {o.TargetMb.ToString("0.#", Inv)} MB.";
            }
        }

        var (width, height) = Fit(v.DisplayWidth, v.DisplayHeight, shortCap);
        var (srcW, srcH) = Fit(v.DisplayWidth, v.DisplayHeight, null);
        string? scaleFilter = width != srcW || height != srcH ? $"scale={width}:{height}:flags=lanczos" : null;
        if (o.ResizeTo is { } target)
        {
            (width, height) = (Even(target.Width), Even(target.Height));
            scaleFilter = ResizeFilter(width, height, o.ResizeMode);
        }

        // Bitrate ceiling for quality presets, scaled down with resolution and frame rate.
        long capKbps = 0;
        if (o.Mode != QualityMode.TargetSize && v.VideoBitRate > 0)
        {
            double workRatio = (double)width * height * fps / Math.Max(1.0, (double)srcW * srcH * srcFps);
            // Upscaling (e.g. stretching 1440x1080 to 1920x1080) gets a little more room than the source had.
            double scale = workRatio > 1 ? Math.Pow(workRatio, 0.5) : Math.Pow(workRatio, 0.75);
            capKbps = (long)Math.Max(250, v.VideoBitRate / 1000.0 * CapRatio(o.Mode) * scale);
        }

        bool tonemap = v.IsHdr && support.CanTonemap;
        var filters = new List<string>();
        if (fps < srcFps - 0.01) filters.Add("fps=" + fps.ToString("0.###", Inv));
        if (scaleFilter is not null) filters.Add(scaleFilter);
        if (tonemap)
        {
            filters.Add(TonemapChain);
            note = (note ?? "") + "HDR video converted to SDR so colors look right everywhere.";
        }

        List<string> Head()
        {
            var a = new List<string> { "-hide_banner", "-y" };
            if (start > TimeSpan.Zero) a.AddRange(["-ss", start.TotalSeconds.ToString("0.###", Inv)]);
            a.AddRange(["-i", v.Path]);
            if (trimmed) a.AddRange(["-t", duration.ToString("0.###", Inv)]);
            return a;
        }
        List<string> Common()
        {
            var a = new List<string> { "-map", "0:v:0" };
            if (filters.Count > 0) a.AddRange(["-vf", string.Join(",", filters)]);
            a.AddRange(["-pix_fmt", vendor == HwVendor.Intel ? "nv12" : "yuv420p"]);
            if (tonemap) a.AddRange(["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"]);
            return a;
        }
        List<string> Tail()
        {
            var a = new List<string>();
            var (graph, map) = keepAudio ? AudioSource(v, o.TrackVolumes) : (null, []);
            if (map.Length > 0)
            {
                if (graph is not null) a.AddRange(["-filter_complex", graph]);
                a.AddRange([.. map, "-c:a", "aac", "-b:a", audioKbps + "k"]);
                if (v.AudioChannels > 2) a.AddRange(["-ac", "2"]);
            }
            else a.Add("-an");
            a.AddRange(["-sn", "-dn", "-map_metadata", "0", "-movflags", "+faststart", outputPath]);
            return a;
        }

        var passes = new List<List<string>>();
        string encoder = EncoderName(vendor, o.Codec);
        long heuristicKbps = HeuristicKbps(width, height, fps, hevc, vendor, o.Mode);

        if (o.Mode == QualityMode.TargetSize && vendor == HwVendor.None)
        {
            // Two-pass ABR on the CPU hits the target size very precisely.
            var pass1 = Head(); pass1.AddRange(Common()); pass1.AddRange(CpuTarget(hevc, videoKbps, 1));
            pass1.AddRange(["-an", "-f", "null", "-"]);
            var pass2 = Head(); pass2.AddRange(Common()); pass2.AddRange(CpuTarget(hevc, videoKbps, 2)); pass2.AddRange(Tail());
            passes.Add(pass1);
            passes.Add(pass2);
        }
        else
        {
            var args = Head();
            args.AddRange(Common());
            args.AddRange(o.Mode == QualityMode.TargetSize
                ? HardwareTarget(vendor, encoder, videoKbps)
                : Quality(vendor, encoder, hevc, o.Mode, capKbps, heuristicKbps));
            args.AddRange(Tail());
            passes.Add(args);
        }

        long estimate;
        if (o.Mode == QualityMode.TargetSize)
            estimate = (long)(o.TargetMb * 1024 * 1024 * 0.97);
        else
        {
            long kbps = capKbps > 0 ? Math.Min(heuristicKbps, capKbps) : heuristicKbps;
            estimate = (long)((kbps + audioKbps) * 1000.0 * duration / 8);
        }

        return new EncodePlan
        {
            Passes = passes,
            WorkDir = workDir,
            OutputPath = outputPath,
            EncoderLabel = $"{(hevc ? "H.265" : "H.264")} · {Format.Vendor(vendor)}",
            Duration = TimeSpan.FromSeconds(duration),
            Width = width,
            Height = height,
            Fps = fps,
            EstimatedBytes = estimate,
            Note = note?.Trim(),
            IsHardware = vendor != HwVendor.None,
        };
    }

    static List<string> CpuTarget(bool hevc, long kbps, int pass)
    {
        var a = new List<string> { "-c:v", hevc ? "libx265" : "libx264", "-preset", "medium", "-b:v", kbps + "k" };
        if (hevc) a.AddRange(["-x265-params", $"pass={pass}:stats=x265_2pass.log:log-level=error", "-tag:v", "hvc1"]);
        else a.AddRange(["-pass", pass.ToString(Inv), "-passlogfile", "x264_2pass"]);
        return a;
    }

    static List<string> RateLimit(long avgKbps, long maxKbps) =>
        ["-b:v", avgKbps + "k", "-maxrate", maxKbps + "k", "-bufsize", maxKbps * 2 + "k"];

    static List<string> HardwareTarget(HwVendor vendor, string encoder, long kbps)
    {
        var a = new List<string> { "-c:v", encoder };
        switch (vendor)
        {
            case HwVendor.Nvidia:
                a.AddRange(["-preset", "p5", "-tune", "hq", "-rc", "vbr", "-multipass", "fullres", "-spatial-aq", "1"]);
                break;
            case HwVendor.Intel:
                a.AddRange(["-preset", "medium"]);
                break;
            case HwVendor.Amd:
                a.AddRange(["-quality", "balanced", "-rc", "vbr_peak"]);
                break;
        }
        a.AddRange(RateLimit(kbps, (long)(kbps * 1.5)));
        if (encoder.StartsWith("hevc")) a.AddRange(["-tag:v", "hvc1"]);
        return a;
    }

    static List<string> Quality(HwVendor vendor, string encoder, bool hevc, QualityMode mode, long capKbps, long heuristicKbps)
    {
        int Pick(int high, int medium, int low) => mode switch { QualityMode.Source => high - 3, QualityMode.High => high, QualityMode.Low => low, _ => medium };
        string PickS(string high, string medium, string low) => mode switch { QualityMode.High or QualityMode.Source => high, QualityMode.Low => low, _ => medium };
        string Kbps(long k) => k + "k";

        var a = new List<string> { "-c:v", encoder };
        switch (vendor)
        {
            case HwVendor.Nvidia:
                a.AddRange(["-preset", PickS("p6", "p5", "p3"), "-tune", "hq", "-rc", "vbr",
                    "-cq", (hevc ? Pick(25, 30, 34) : Pick(23, 28, 32)).ToString(Inv), "-b:v", "0",
                    "-spatial-aq", "1", "-rc-lookahead", "20"]);
                if (capKbps > 0) a.AddRange(["-maxrate", Kbps(capKbps), "-bufsize", Kbps(capKbps * 2)]);
                break;
            case HwVendor.Intel:
                a.AddRange(["-preset", PickS("slower", "medium", "veryfast")]);
                // ICQ ignores maxrate, so switch to capped VBR when we know the source bitrate.
                if (capKbps > 0) a.AddRange(RateLimit(Math.Min(heuristicKbps, (long)(capKbps * 0.8)), capKbps));
                else a.AddRange(["-global_quality", (hevc ? Pick(23, 27, 31) : Pick(21, 25, 30)).ToString(Inv)]);
                break;
            case HwVendor.Amd:
                a.AddRange(["-quality", PickS("quality", "balanced", "speed")]);
                if (capKbps > 0) a.AddRange(["-rc", "vbr_peak", .. RateLimit(Math.Min(heuristicKbps, (long)(capKbps * 0.8)), capKbps)]);
                else
                {
                    int q = hevc ? Pick(22, 27, 31) : Pick(20, 25, 30);
                    a.AddRange(["-rc", "cqp", "-qp_i", q.ToString(Inv), "-qp_p", (q + 2).ToString(Inv)]);
                    if (!hevc) a.AddRange(["-qp_b", (q + 4).ToString(Inv)]);
                }
                break;
            default:
                a.AddRange(["-preset", PickS("slow", "medium", "veryfast"),
                    "-crf", (hevc ? Pick(23, 27, 31) : Pick(20, 24, 29)).ToString(Inv)]);
                if (capKbps > 0) a.AddRange(["-maxrate", Kbps(capKbps), "-bufsize", Kbps(capKbps * 2)]);
                if (hevc) a.AddRange(["-x265-params", "log-level=error"]);
                break;
        }
        if (hevc) a.AddRange(["-tag:v", "hvc1"]);
        return a;
    }

    /// <summary>Rough bitrate a quality preset tends to need. Real results depend heavily on content.</summary>
    static long HeuristicKbps(int w, int h, double fps, bool hevc, HwVendor vendor, QualityMode mode)
    {
        double bpp = mode switch { QualityMode.Source => 0.14, QualityMode.High => 0.10, QualityMode.Low => 0.05, _ => 0.065 };
        if (hevc) bpp *= 0.62;
        if (vendor != HwVendor.None) bpp *= 1.5;
        // Bits per frame drop as frame rate rises (less change between frames).
        double effectiveFps = fps <= 30 ? fps : 30 * Math.Pow(fps / 30, 0.6);
        return (long)(bpp * w * h * effectiveFps / 1000);
    }

    // ---------------------------------------------------------------- run

    public static async Task RunAsync(FfmpegTools tools, EncodePlan plan, IProgress<EncodeProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(plan.WorkDir);
        try
        {
            for (int i = 0; i < plan.Passes.Count; i++)
            {
                int pass = i;
                await RunFfmpegAsync(tools.Ffmpeg, plan.Passes[i], plan.WorkDir, (seconds, speed) =>
                {
                    double f = plan.Duration.TotalSeconds > 0 ? Math.Clamp(seconds / plan.Duration.TotalSeconds, 0, 1) : 0;
                    progress.Report(new EncodeProgress((pass + f) / plan.Passes.Count, speed, pass + 1, plan.Passes.Count));
                }, ct);
            }
        }
        catch
        {
            try { if (File.Exists(plan.OutputPath)) File.Delete(plan.OutputPath); } catch { /* locked */ }
            throw;
        }
        finally
        {
            try { Directory.Delete(plan.WorkDir, recursive: true); } catch { /* best effort */ }
        }
    }

    static async Task RunFfmpegAsync(string exe, List<string> args, string workDir, Action<double, double> onProgress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Progress goes to stdout as key=value lines, errors to stderr.
        psi.ArgumentList.Add("-progress");
        psi.ArgumentList.Add("pipe:1");
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var errors = new Queue<string>();
        double seconds = 0, speed = 0;

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { } line) return;
            int eq = line.IndexOf('=');
            if (eq < 0) return;
            var key = line[..eq];
            var value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "out_time_us" when long.TryParse(value, out var us) && us >= 0:
                    seconds = us / 1_000_000.0;
                    break;
                case "speed" when double.TryParse(value.TrimEnd('x'), NumberStyles.Float, Inv, out var s):
                    speed = s;
                    break;
                case "progress":
                    onProgress(seconds, speed);
                    break;
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (errors)
            {
                errors.Enqueue(e.Data);
                while (errors.Count > 12) errors.Dequeue();
            }
        };

        p.Start();
        int pid = p.Id;
        Running[pid] = p;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* not critical */ }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already exited */ }
            await p.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            Running.TryRemove(pid, out _);
        }

        if (p.ExitCode != 0)
        {
            string message;
            lock (errors) message = string.Join(Environment.NewLine, errors);
            throw new FfmpegException(string.IsNullOrWhiteSpace(message) ? $"FFmpeg exited with code {p.ExitCode}." : message);
        }
    }
}
