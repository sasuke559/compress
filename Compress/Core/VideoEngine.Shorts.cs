using System.Windows;

namespace Compress.Core;

public sealed class ShortsOptions
{
    public TimeSpan Start { get; init; }
    /// <summary>null = until the end of the video.</summary>
    public TimeSpan? End { get; init; }
    /// <summary>TikTok, Reels and Shorts play at most 60 fps; higher rates only cost size.</summary>
    public bool Limit60 { get; init; } = true;
    public bool RemoveAudio { get; init; }
    public bool UseHardware { get; init; }
    /// <summary>ASS captions to burn in (see <see cref="Subtitles.BuildAss"/>), null for none.</summary>
    public string? SubtitlesAss { get; init; }
}

public static partial class VideoEngine
{
    const string SubtitlesFile = "captions.ass";

    /// <summary>
    /// Builds the 1080×1920 composition: gameplay crop (optionally over a blurred or black background)
    /// with every HUD element cropped from the source and overlaid at its spot.
    /// </summary>
    public static EncodePlan CreateShortsPlan(VideoInfo v, ShortsLayout layout, ShortsOptions o, EncoderSupport support, string outputPath, string workDir)
    {
        double sw = v.DisplayWidth, sh = v.DisplayHeight;
        if (sw <= 0 || sh <= 0) throw new InvalidOperationException("The size of this video could not be read.");

        const int W = ShortsLayout.Width, H = ShortsLayout.Height;
        var vendor = o.UseHardware ? support.VendorFor(CodecChoice.H264) : HwVendor.None;
        string encoder = EncoderName(vendor, CodecChoice.H264);

        var start = o.Start < TimeSpan.Zero || o.Start >= v.Duration ? TimeSpan.Zero : o.Start;
        var end = o.End is { } te && te > start && te < v.Duration ? te : v.Duration;
        bool trimmed = start > TimeSpan.Zero || end < v.Duration;
        double duration = Math.Max(0.1, (end - start).TotalSeconds);

        double srcFps = v.Fps > 0 ? v.Fps : 30;
        double fps = o.Limit60 && srcFps > 60.5 ? 60 : srcFps;

        var elements = layout.Elements.Where(e => e.Visible && e.OutW > 0 && e.SrcW > 0 && e.SrcH > 0).ToList();
        bool blur = !layout.FillsFrame && layout.Background == ShortsBackground.Blur;
        bool tonemap = v.IsHdr && support.CanTonemap;

        string N(double d) => ((int)Math.Round(d)).ToString(Inv);
        string Crop(Rect r)
        {
            int x = Math.Clamp((int)Math.Floor(r.X), 0, (int)sw - 2), y = Math.Clamp((int)Math.Floor(r.Y), 0, (int)sh - 2);
            int w = Math.Clamp((int)Math.Round(r.Width) / 2 * 2, 2, ((int)sw - x) / 2 * 2);
            int h = Math.Clamp((int)Math.Round(r.Height) / 2 * 2, 2, ((int)sh - y) / 2 * 2);
            return $"crop={w}:{h}:{x}:{y}";
        }

        // One decoded stream, split into the gameplay, the background and one copy per HUD element.
        var pre = new List<string>();
        if (fps < srcFps - 0.01) pre.Add("fps=" + fps.ToString("0.###", Inv));
        if (tonemap) pre.Add(TonemapChain);
        var outputs = new List<string> { "[m]" };
        if (blur) outputs.Add("[b]");
        for (int i = 0; i < elements.Count; i++) outputs.Add($"[s{i}]");

        var graph = new List<string>();
        string head = pre.Count > 0 ? string.Join(",", pre) + "," : "";
        graph.Add(outputs.Count == 1 ? $"[0:v]{head}null[m]" : $"[0:v]{head}split={outputs.Count}{string.Concat(outputs)}");

        var frame = layout.FrameRect();
        graph.Add($"[m]{Crop(layout.CropRect(sw, sh))},scale={Even(frame.Width)}:{Even(frame.Height)}:flags=lanczos,setsar=1[main]");

        string current;
        if (layout.FillsFrame) current = "[main]";
        else if (blur)
        {
            // Blurring a small copy is much faster than blurring 1080×1920 and looks the same once scaled up.
            graph.Add($"[b]scale=270:480:force_original_aspect_ratio=increase,crop=270:480,boxblur=12:2," +
                      $"eq=brightness=-0.12:saturation=1.15,scale={W}:{H}:flags=bicubic,setsar=1[bg]");
            graph.Add($"[bg][main]overlay=0:{N(frame.Y)}[v0]");
            current = "[v0]";
        }
        else
        {
            graph.Add($"[main]pad={W}:{H}:0:{N(frame.Y)}:black[v0]");
            current = "[v0]";
        }

        for (int i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            var dst = ShortsLayout.OutputRect(e, sw, sh);
            graph.Add($"[s{i}]{Crop(ShortsLayout.SourceRect(e, sw, sh))},scale={Even(dst.Width)}:{Even(dst.Height)}:flags=lanczos,setsar=1[h{i}]");
            graph.Add($"{current}[h{i}]overlay={N(dst.X)}:{N(dst.Y)}[o{i}]");
            current = $"[o{i}]";
        }
        // Captions go on top of everything; FFmpeg runs in the work folder, so a plain file name avoids Windows path escaping.
        if (o.SubtitlesAss is not null)
        {
            graph.Add($"{current}subtitles={SubtitlesFile}[sub]");
            current = "[sub]";
        }
        graph.Add($"{current}format={(vendor == HwVendor.Intel ? "nv12" : "yuv420p")}[out]");

        bool keepAudio = v.HasAudio && !o.RemoveAudio;
        int audioKbps = keepAudio && v.AudioBitRate > 0 ? Math.Clamp((int)Math.Ceiling(v.AudioBitRate / 1000.0 / 16) * 16, 96, 192) : 192;

        // The platforms re-encode uploads, so give them a clean, high quality master.
        long heuristicKbps = HeuristicKbps(W, H, fps, false, vendor, QualityMode.High);
        long capKbps = fps > 40 ? 18_000 : 12_000;

        var args = new List<string> { "-hide_banner", "-y" };
        if (start > TimeSpan.Zero) args.AddRange(["-ss", start.TotalSeconds.ToString("0.###", Inv)]);
        args.AddRange(["-i", v.Path]);
        if (trimmed) args.AddRange(["-t", duration.ToString("0.###", Inv)]);
        // Mixed game + mic tracks join the same graph.
        var (audioGraph, audioMap) = keepAudio ? AudioSource(v) : (null, []);
        if (audioGraph is not null) graph.Add(audioGraph);
        args.AddRange(["-filter_complex", string.Join(";", graph), "-map", "[out]"]);
        args.AddRange(Quality(vendor, encoder, false, QualityMode.High, capKbps, heuristicKbps));
        if (tonemap) args.AddRange(["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"]);
        if (keepAudio)
        {
            args.AddRange([.. audioMap, "-c:a", "aac", "-b:a", audioKbps + "k"]);
            if (v.AudioChannels > 2) args.AddRange(["-ac", "2"]);
        }
        else args.Add("-an");
        args.AddRange(["-sn", "-dn", "-movflags", "+faststart", outputPath]);

        long kbps = Math.Min(heuristicKbps, capKbps) + (keepAudio ? audioKbps : 0);
        return new EncodePlan
        {
            Passes = [args],
            WorkFiles = o.SubtitlesAss is null ? null : new Dictionary<string, string> { [SubtitlesFile] = o.SubtitlesAss },
            WorkDir = workDir,
            OutputPath = outputPath,
            EncoderLabel = $"H.264 · {Format.Vendor(vendor)}",
            Duration = TimeSpan.FromSeconds(duration),
            Width = W,
            Height = H,
            Fps = fps,
            EstimatedBytes = (long)(kbps * 1000.0 * duration / 8),
            Note = tonemap ? "HDR video converted to SDR so colors look right everywhere." : null,
            IsHardware = vendor != HwVendor.None,
        };
    }
}
