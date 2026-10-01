namespace Compress.Core;

public enum ConvertFormat { Mp4, Mp4Hevc, Mov, Mkv, WebM, Avi, Gif, Mp3, Wav }

/// <summary>A target format as shown in the converter.</summary>
public sealed record ConvertFormatInfo(ConvertFormat Format, string Name, string Extension, string Description)
{
    public bool AudioOnly => Format is ConvertFormat.Mp3 or ConvertFormat.Wav;
    public bool HasQuality => Format != ConvertFormat.Wav;
}

public sealed class ConvertOptions
{
    public ConvertFormat Format { get; init; }
    /// <summary>Source (near-lossless), High or Medium (small).</summary>
    public QualityMode Quality { get; init; } = QualityMode.Source;
    /// <summary>Copy the streams instead of re-encoding when the target container accepts them.</summary>
    public bool PreferCopy { get; init; } = true;
    public bool UseHardware { get; init; }
}

public static partial class VideoEngine
{
    public static IReadOnlyList<ConvertFormatInfo> ConvertFormats { get; } =
    [
        new(ConvertFormat.Mp4, "MP4", ".mp4", "Plays everywhere"),
        new(ConvertFormat.Mp4Hevc, "MP4 · H.265", ".mp4", "Smaller, newer devices"),
        new(ConvertFormat.Mov, "MOV", ".mov", "Apple & editing"),
        new(ConvertFormat.Mkv, "MKV", ".mkv", "All audio tracks"),
        new(ConvertFormat.WebM, "WebM", ".webm", "VP9 + Opus · web"),
        new(ConvertFormat.Avi, "AVI", ".avi", "Old players & TVs"),
        new(ConvertFormat.Gif, "GIF", ".gif", "Animated, no sound"),
        new(ConvertFormat.Mp3, "MP3", ".mp3", "Audio only"),
        new(ConvertFormat.Wav, "WAV", ".wav", "Audio only · lossless"),
    ];

    public static ConvertFormatInfo FormatInfo(ConvertFormat format) => ConvertFormats.First(f => f.Format == format);

    /// <summary>Video codecs each container can take as they are (stream copy).</summary>
    static bool CanCopyVideo(ConvertFormat f, string codec) => f switch
    {
        ConvertFormat.Mp4 => codec is "h264" or "hevc" or "av1",
        ConvertFormat.Mp4Hevc => codec is "hevc",
        ConvertFormat.Mov => codec is "h264" or "hevc" or "prores" or "mjpeg",
        ConvertFormat.Mkv => codec.Length > 0,
        ConvertFormat.WebM => codec is "vp8" or "vp9" or "av1",
        ConvertFormat.Avi => codec is "mpeg4" or "mjpeg",
        _ => false,
    };

    static bool CanCopyAudio(ConvertFormat f, string codec) => f switch
    {
        ConvertFormat.Mp4 or ConvertFormat.Mp4Hevc => codec is "aac" or "mp3" or "alac",
        ConvertFormat.Mov => codec is "aac" or "alac" or "pcm_s16le" or "pcm_s24le",
        ConvertFormat.Mkv => codec.Length > 0,
        ConvertFormat.WebM => codec is "opus" or "vorbis",
        ConvertFormat.Avi => codec is "mp3",
        _ => false,
    };

    /// <summary>True when the converted file can be written without re-encoding the picture.</summary>
    public static bool IsLosslessRemux(VideoInfo v, ConvertOptions o) =>
        o.PreferCopy && CanCopyVideo(o.Format, v.VideoCodec);

    /// <summary>Builds the ffmpeg command for converting one file. Throws InvalidOperationException for impossible jobs.</summary>
    public static EncodePlan CreateConvertPlan(VideoInfo v, ConvertOptions o, EncoderSupport support, string outputPath, string workDir)
    {
        var info = FormatInfo(o.Format);
        if (info.AudioOnly && !v.HasAudio) throw new InvalidOperationException("This video has no sound to extract.");

        double duration = Math.Max(0.1, v.Duration.TotalSeconds);
        // MP3 / WAV hold one track, so separate game and mic tracks are always mixed there.
        string[] audioOnlySource = v.AudioTracks > 1
            ? ["-filter_complex", AudioMixGraph(v.AudioTracks), "-map", "[aout]"]
            : ["-map", "0:a:0"];
        var mode = o.Quality;
        var args = new List<string> { "-hide_banner", "-y", "-i", v.Path };
        string label;
        long estimate;
        int width = v.DisplayWidth, height = v.DisplayHeight;
        double fps = v.Fps;
        bool hardware = false;

        switch (o.Format)
        {
            case ConvertFormat.Mp3:
            {
                // LAME VBR: V0 ≈ 245k, V2 ≈ 190k, V5 ≈ 130k.
                int q = mode switch { QualityMode.Source => 0, QualityMode.High => 2, _ => 5 };
                args.AddRange([.. audioOnlySource, "-vn", "-c:a", "libmp3lame", "-q:a", q.ToString(Inv), "-map_metadata", "0", outputPath]);
                label = $"MP3 · VBR V{q}";
                estimate = (long)((q == 0 ? 245 : q == 2 ? 190 : 130) * 1000.0 * duration / 8);
                width = height = 0;
                break;
            }
            case ConvertFormat.Wav:
                args.AddRange([.. audioOnlySource, "-vn", "-c:a", "pcm_s16le", "-map_metadata", "0", outputPath]);
                label = "WAV · 16-bit PCM";
                // 48 kHz, 16 bit per channel.
                estimate = (long)(768 * Math.Clamp(v.AudioChannels, 1, 8) * 1000.0 * duration / 8);
                width = height = 0;
                break;

            case ConvertFormat.Gif:
            {
                // Palette per clip gives clean colors; size and frame rate keep files reasonable.
                (int maxW, int gifFps) = mode switch { QualityMode.Source => (720, 24), QualityMode.High => (480, 15), _ => (320, 12) };
                width = Math.Min(maxW, v.DisplayWidth) / 2 * 2;
                height = v.DisplayWidth > 0 ? Even((double)width * v.DisplayHeight / v.DisplayWidth) : width;
                fps = Math.Min(gifFps, v.Fps > 0 ? v.Fps : gifFps);
                string pre = $"fps={fps.ToString("0.###", Inv)},scale={width}:-2:flags=lanczos";
                if (v.IsHdr && support.CanTonemap) pre += "," + TonemapChain;
                args.AddRange(["-filter_complex", $"[0:v]{pre},split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle[out]",
                    "-map", "[out]", "-an", "-loop", "0", outputPath]);
                label = $"GIF · {width}px · {Format.Fps(fps)} fps";
                estimate = (long)(width * height * fps * duration * 0.12);
                break;
            }

            default:
            {
                bool copyVideo = IsLosslessRemux(v, o);
                bool keepAudio = v.HasAudio;
                // Separate game/mic tracks are mixed into one (setting); MKV is the format for keeping them all.
                bool mixAudio = keepAudio && v.AudioTracks > 1 && MixAudioTracks && o.Format != ConvertFormat.Mkv;
                bool copyAudio = keepAudio && !mixAudio && o.PreferCopy && CanCopyAudio(o.Format, v.AudioCodec);

                args.AddRange(["-map", "0:v:0"]);
                if (mixAudio) args.AddRange(["-filter_complex", AudioMixGraph(v.AudioTracks), "-map", "[aout]"]);
                else if (keepAudio) args.AddRange(["-map", "0:a?"]);

                long videoKbps;
                if (copyVideo)
                {
                    args.AddRange(["-c:v", "copy"]);
                    if (o.Format is ConvertFormat.Mp4 or ConvertFormat.Mp4Hevc or ConvertFormat.Mov && v.VideoCodec == "hevc")
                        args.AddRange(["-tag:v", "hvc1"]);
                    videoKbps = v.VideoBitRate / 1000;
                    label = copyAudio || !keepAudio ? "Remux · lossless" : mixAudio ? "Remux · audio tracks mixed" : "Remux · audio re-encoded";
                }
                else
                {
                    bool tonemap = v.IsHdr && support.CanTonemap;
                    var filters = new List<string>();
                    if (tonemap) filters.Add(TonemapChain);
                    width = Even(v.DisplayWidth);
                    height = Even(v.DisplayHeight);
                    if (width != v.DisplayWidth || height != v.DisplayHeight) filters.Add($"scale={width}:{height}");
                    if (filters.Count > 0) args.AddRange(["-vf", string.Join(",", filters)]);

                    // Never spend much more than the source had; same rule as the compressor.
                    long capKbps = v.VideoBitRate > 0 ? (long)Math.Max(250, v.VideoBitRate / 1000.0 * CapRatio(mode)) : 0;
                    switch (o.Format)
                    {
                        case ConvertFormat.WebM:
                        {
                            int crf = mode switch { QualityMode.Source => 24, QualityMode.High => 31, _ => 37 };
                            // Constrained quality: the CRF decides, the source bitrate is the ceiling (VP9 rejects maxrate with -b:v 0).
                            args.AddRange(["-c:v", "libvpx-vp9", "-crf", crf.ToString(Inv), "-b:v", capKbps > 0 ? capKbps + "k" : "0",
                                "-deadline", "good", "-cpu-used", "4", "-row-mt", "1", "-pix_fmt", "yuv420p"]);
                            label = "VP9 · CPU";
                            videoKbps = (long)(HeuristicKbps(width, height, fps, true, HwVendor.None, mode) * 0.9);
                            break;
                        }
                        case ConvertFormat.Avi:
                        {
                            int q = mode switch { QualityMode.Source => 2, QualityMode.High => 3, _ => 6 };
                            args.AddRange(["-c:v", "mpeg4", "-vtag", "XVID", "-q:v", q.ToString(Inv), "-pix_fmt", "yuv420p"]);
                            label = "MPEG-4 (Xvid) · CPU";
                            videoKbps = (long)(HeuristicKbps(width, height, fps, false, HwVendor.None, mode) * 3.5);
                            break;
                        }
                        default:
                        {
                            var codec = o.Format == ConvertFormat.Mp4Hevc ? CodecChoice.H265 : CodecChoice.H264;
                            var vendor = o.UseHardware ? support.VendorFor(codec) : HwVendor.None;
                            hardware = vendor != HwVendor.None;
                            string encoder = EncoderName(vendor, codec);
                            long heuristic = HeuristicKbps(width, height, fps, codec == CodecChoice.H265, vendor, mode);
                            args.AddRange(Quality(vendor, encoder, codec == CodecChoice.H265, mode, capKbps, heuristic));
                            args.AddRange(["-pix_fmt", vendor == HwVendor.Intel ? "nv12" : "yuv420p"]);
                            label = $"{(codec == CodecChoice.H265 ? "H.265" : "H.264")} · {Format.Vendor(vendor)}";
                            videoKbps = capKbps > 0 ? Math.Min(heuristic, capKbps) : heuristic;
                            break;
                        }
                    }
                    if (tonemap) args.AddRange(["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"]);
                }

                long audioKbps = 0;
                if (!keepAudio) args.Add("-an");
                else if (copyAudio)
                {
                    args.AddRange(["-c:a", "copy"]);
                    audioKbps = v.AudioBitRate / 1000;
                }
                else
                {
                    (string codec, string bitrate) = o.Format switch
                    {
                        ConvertFormat.WebM => ("libopus", "160k"),
                        ConvertFormat.Avi => ("libmp3lame", "192k"),
                        _ => ("aac", "192k"),
                    };
                    args.AddRange(["-c:a", codec, "-b:a", bitrate]);
                    if (v.AudioChannels > 2) args.AddRange(["-ac", "2"]);
                    audioKbps = 192 * (mixAudio ? 1 : Math.Max(1, v.AudioTracks));
                }

                args.AddRange(["-sn", "-dn", "-map_metadata", "0"]);
                if (o.Format is ConvertFormat.Mp4 or ConvertFormat.Mp4Hevc or ConvertFormat.Mov) args.AddRange(["-movflags", "+faststart"]);
                args.Add(outputPath);
                estimate = copyVideo && copyAudio
                    ? v.SizeBytes
                    : (long)((videoKbps + audioKbps) * 1000.0 * duration / 8);
                break;
            }
        }

        return new EncodePlan
        {
            Passes = [args],
            WorkDir = workDir,
            OutputPath = outputPath,
            EncoderLabel = label,
            Duration = TimeSpan.FromSeconds(duration),
            Width = width,
            Height = height,
            Fps = fps,
            EstimatedBytes = estimate,
            IsHardware = hardware,
        };
    }
}
