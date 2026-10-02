using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using Whisper.net;

namespace Compress.Core;

/// <summary>One spoken word; times are seconds in the source video.</summary>
public sealed class SubWord
{
    public required string Text { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
}

/// <summary>A caption shown on screen at once (a few words), highlighted word by word.</summary>
public sealed class SubLine
{
    public List<SubWord> Words { get; set; } = [];
    public double Start => Words[0].Start;
    public double End => Words[^1].End;
    public string Text => string.Join(" ", Words.Select(w => w.Text));

    /// <summary>Replaces the words after the user edited the line; the line keeps its time span, split by word length.</summary>
    public void SetText(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || Words.Count == 0) return;
        double start = Start, length = Math.Max(0.1, End - Start), total = parts.Sum(p => p.Length + 1.0), t = start;
        Words = parts.Select(p =>
        {
            double d = length * (p.Length + 1.0) / total;
            var w = new SubWord { Text = p, Start = t, End = t + d };
            t += d;
            return w;
        }).ToList();
    }
}

public enum SubtitlePosition { Top, Middle, Bottom }

/// <summary>Spoken language; "auto" lets Whisper decide from the voice.</summary>
public sealed record SubtitleLanguage(string Code, string Name)
{
    public static readonly SubtitleLanguage[] All =
    [
        new("auto", "Detect automatically"), new("en", "English"), new("de", "Deutsch"), new("ru", "Русский"), new("uk", "Українська"),
        new("pl", "Polski"), new("tr", "Türkçe"), new("fr", "Français"), new("es", "Español"), new("pt", "Português"), new("it", "Italiano"),
    ];

    public override string ToString() => Name;
}

/// <summary>
/// Speech to captions, fully on this PC: FFmpeg cuts the voice out of the clip, Whisper (whisper.cpp) turns it into words
/// with timestamps, and the words become short TikTok-style lines that are burned in with an ASS subtitle file.
/// </summary>
public static class Subtitles
{
    /// <summary>Multilingual "small" model, quantized: good with German, English and Russian gaming voice chat, ~190 MB.</summary>
    const string ModelFile = "ggml-small-q5_1.bin";
    const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + ModelFile;

    public static string ModelPath => Path.Combine(FfmpegTools.DataDir, "models", ModelFile);
    public static bool ModelReady => File.Exists(ModelPath);

    // Caption look (1080×1920 canvas).
    public const string FontName = "Segoe UI Black";
    public const double FontSize = 92;
    const int MaxWordsPerLine = 3;
    const int MaxCharsPerLine = 18;

    /// <summary>Vertical centre of the caption on the 1920 px canvas; Bottom stays above the apps' caption and buttons.</summary>
    public static double CenterY(SubtitlePosition position) => position switch
    {
        SubtitlePosition.Top => 430,
        SubtitlePosition.Middle => 960,
        _ => 1290,
    };

    public static async Task DownloadModelAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (ModelReady) return;
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        var part = ModelPath + ".part";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Compress/{Feedback.AppVersion}");
        using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(part))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        if (total > 0 && new FileInfo(part).Length != total) throw new IOException("The speech model download is incomplete.");
        File.Move(part, ModelPath, overwrite: true);
    }

    /// <summary>
    /// Transcribes <paramref name="start"/>–<paramref name="end"/> of the video. <paramref name="track"/> picks one audio track
    /// (e.g. the microphone of an NVIDIA clip); null mixes all tracks. <paramref name="language"/> is a Whisper code or "auto".
    /// Returns the lines and the language that was used.
    /// </summary>
    public static async Task<(List<SubLine> Lines, string Language)> TranscribeAsync(FfmpegTools tools, VideoInfo v, double start, double end,
        int? track, string language, IProgress<double>? progress, CancellationToken ct)
    {
        if (!v.HasAudio) throw new InvalidOperationException("This video has no sound to make subtitles from.");
        var wav = Path.Combine(Path.GetTempPath(), $"compress-voice-{Guid.NewGuid():N}.wav");
        try
        {
            await ExtractVoiceAsync(tools, v, start, end, track, wav, ct);
            var samples = ReadWav(wav);
            var regions = SpeechRegions(samples);
            if (regions.Count == 0) return ([], language);

            WhisperNative.EnsureLoaded();
            using var factory = WhisperFactory.FromPath(ModelPath);

            // Whisper's own detection looks at the first 30 s only, often silence or game noise there; use the speech instead.
            if (language == "auto")
            {
                using var detector = factory.CreateBuilder().WithLanguage("auto").Build();
                var speech = regions.OrderByDescending(r => r.Length).SelectMany(r => samples.Skip(r.Start).Take(r.Length)).Take(SampleRate * 30).ToArray();
                var codes = SubtitleLanguage.All.Skip(1).Select(l => l.Code).ToArray();
                language = await Task.Run(() => detector.DetectLanguageWithProbability(speech, codes).language ?? "en", ct);
            }

            using var processor = factory.CreateBuilder()
                .WithLanguage(language)
                .WithTokenTimestamps()
                .SplitOnWord()
                .WithMaxSegmentLength(1) // one word per segment, so every word has its own time
                .WithNoContext() // each chunk on its own: stops one misheard phrase from repeating through the clip
                .WithThreads(Math.Max(1, Environment.ProcessorCount - 1))
                .Build();

            // Only the speech goes to Whisper: its timestamps drift badly across long silences. Stretches close together
            // are processed as one chunk (with their real pauses), since every Whisper pass costs about the same.
            var chunks = new List<(int Start, int End)>();
            foreach (var r in regions)
            {
                int rEnd = r.Start + r.Length;
                if (chunks.Count > 0 && r.Start - chunks[^1].End < SampleRate && rEnd - chunks[^1].Start <= SampleRate * 12)
                    chunks[^1] = (chunks[^1].Start, rEnd);
                else chunks.Add((r.Start, rEnd));
            }

            var words = new List<SubWord>();
            double total = chunks.Sum(c => c.End - c.Start), done = 0;
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                var audio = Normalize(samples.AsSpan(chunk.Start, chunk.End - chunk.Start).ToArray());
                double offset = start + (double)chunk.Start / SampleRate, chunkEnd = start + (double)chunk.End / SampleRate;
                bool inNoise = false;
                await foreach (var segment in processor.ProcessAsync(audio, ct))
                {
                    var text = segment.Text.Trim();
                    // Sound descriptions like "(engine rumbling)" or "[Music]" arrive word by word.
                    if (text.StartsWith('(') || text.StartsWith('[') || text.StartsWith('*')) inNoise = true;
                    if (inNoise)
                    {
                        if (text.EndsWith(')') || text.EndsWith(']') || text.EndsWith('*')) inNoise = false;
                        continue;
                    }
                    if (text.Length == 0 || IsNoise(text)) continue;
                    words.Add(new SubWord
                    {
                        Text = text,
                        Start = Math.Min(offset + segment.Start.TotalSeconds, chunkEnd),
                        End = Math.Min(offset + segment.End.TotalSeconds, chunkEnd),
                    });
                }
                done += chunk.End - chunk.Start;
                progress?.Report(done / total);
            }
            return (GroupLines(RemoveRepeats(RemoveHallucinations(words))), language);
        }
        finally
        {
            try { File.Delete(wav); } catch { /* temp */ }
        }
    }

    const int SampleRate = 16000;

    /// <summary>Reads the 16-bit mono PCM that <see cref="ExtractVoiceAsync"/> writes.</summary>
    static float[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, pos, 4);
            int size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "data")
            {
                int count = Math.Min(size, bytes.Length - pos - 8) / 2;
                var samples = new float[count];
                for (int i = 0; i < count; i++) samples[i] = BitConverter.ToInt16(bytes, pos + 8 + i * 2) / 32768f;
                return samples;
            }
            pos += 8 + size + (size & 1);
        }
        return [];
    }

    /// <summary>
    /// Simple energy voice detection on 30 ms frames: louder than both an absolute floor and the clip's own noise floor.
    /// Short gaps are bridged and a little padding keeps word beginnings and endings.
    /// </summary>
    static List<(int Start, int Length)> SpeechRegions(float[] samples)
    {
        const int frame = SampleRate * 30 / 1000;
        int frames = samples.Length / frame;
        if (frames == 0) return [];
        var db = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * frame; i < (f + 1) * frame; i++) sum += samples[i] * samples[i];
            db[f] = 10 * Math.Log10(sum / frame + 1e-12);
        }
        double noise = db.Order().ElementAt(frames / 10); // 10th percentile ≈ background
        double threshold = Math.Max(-50, noise + 12);

        var regions = new List<(int Start, int End)>();
        int gapFrames = 400 / 30, padFrames = 200 / 30, minFrames = 250 / 30;
        int? open = null;
        int lastLoud = -1000;
        for (int f = 0; f < frames; f++)
        {
            if (db[f] < threshold) continue;
            if (open is not null && f - lastLoud > gapFrames)
            {
                regions.Add((open.Value, lastLoud + 1));
                open = null;
            }
            open ??= f;
            lastLoud = f;
        }
        if (open is not null) regions.Add((open.Value, lastLoud + 1));

        return regions
            .Where(r => r.End - r.Start >= minFrames)
            .Select(r => (Math.Max(0, r.Start - padFrames) * frame, Math.Min(frames, r.End + padFrames) * frame))
            .Select(r => (r.Item1, r.Item2 - r.Item1))
            .ToList();
    }

    /// <summary>Brings a quiet voice chunk up to a healthy level (Whisper hears quiet mics poorly).</summary>
    static float[] Normalize(float[] chunk)
    {
        float peak = chunk.Length == 0 ? 0 : chunk.Max(Math.Abs);
        if (peak < 1e-4f) return chunk;
        float gain = Math.Min(20f, 0.9f / peak);
        for (int i = 0; i < chunk.Length; i++) chunk[i] *= gain;
        return chunk;
    }

    static async Task ExtractVoiceAsync(FfmpegTools tools, VideoInfo v, double start, double end, int? track, string wav, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
                     "-i", v.Path, "-t", Math.Max(0.1, end - start).ToString("0.###", CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);
        int tracks = Math.Max(1, v.AudioTracks);
        if (track is { } t && t < tracks) { psi.ArgumentList.Add("-map"); psi.ArgumentList.Add($"0:a:{t}"); }
        else if (tracks > 1)
        {
            psi.ArgumentList.Add("-filter_complex");
            psi.ArgumentList.Add(VideoEngine.AudioMixGraph(tracks));
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("[aout]");
        }
        else { psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:a:0"); }
        // Whisper wants 16 kHz mono; the high-pass cuts game rumble that the model mistakes for speech.
        foreach (var a in new[] { "-af", "highpass=f=80", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-map_metadata", "-1", "-fflags", "+bitexact", wav })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var errors = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(); } catch { /* exited */ }
            throw;
        }
        if (p.ExitCode != 0) throw new InvalidOperationException("Could not read the sound: " + (await errors).Trim());
    }

    /// <summary>Whisper writes sounds as [Music], (laughs), ♪ … – not speech.</summary>
    static bool IsNoise(string text) =>
        text.StartsWith('[') || text.StartsWith('(') || text.StartsWith('*') || text.EndsWith(')') || text.EndsWith(']') || text.EndsWith('*')
        || text.All(c => !char.IsLetterOrDigit(c));

    /// <summary>
    /// On silence or game noise Whisper sometimes "hears" phrases from the subtitles it was trained on
    /// ("Untertitel im Auftrag des ZDF", "Thanks for watching"). Such lines are dropped.
    /// </summary>
    static List<SubWord> RemoveHallucinations(List<SubWord> words)
    {
        string[] phrases =
        [
            "untertitel im auftrag", "untertitelung des zdf", "untertitel der amara", "thanks for watching", "thank you for watching",
            "subtitles by", "please subscribe", "редактор субтитров", "субтитры сделал", "субтитры создавал", "продолжение следует",
            "swr 20", "wdr 20", "zdf 20", "ndr 20", "ard 20", "copyright wdr", "copyright swr", "mehr infos auf", "vielen dank fürs zuschauen",
        ];
        var text = string.Join(" ", words.Select(w => w.Text)).ToLowerInvariant();
        foreach (var phrase in phrases)
        {
            int at;
            while ((at = text.IndexOf(phrase, StringComparison.Ordinal)) >= 0)
            {
                // Map the character range back to words and drop them.
                int pos = 0, first = -1, last = -1;
                for (int i = 0; i < words.Count; i++)
                {
                    int len = words[i].Text.Length;
                    if (pos + len > at && pos < at + phrase.Length) { if (first < 0) first = i; last = i; }
                    pos += len + 1;
                }
                if (first < 0) break;
                // Drop the rest of that sentence too (e.g. "… des ZDF, 2020").
                while (last + 1 < words.Count && !EndsSentence(words[last].Text)) last++;
                words.RemoveRange(first, last - first + 1);
                text = string.Join(" ", words.Select(w => w.Text)).ToLowerInvariant();
            }
        }
        return words;
    }

    /// <summary>Whisper can get stuck repeating one word over noise; more than four in a row are cut.</summary>
    static List<SubWord> RemoveRepeats(List<SubWord> words)
    {
        static string Key(string w) => new string(w.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var result = new List<SubWord>();
        int run = 0;
        for (int i = 0; i < words.Count; i++)
        {
            run = i > 0 && Key(words[i].Text) == Key(words[i - 1].Text) ? run + 1 : 1;
            if (run <= 4) result.Add(words[i]);
        }
        return result;
    }

    static bool EndsSentence(string word) => word.EndsWith('.') || word.EndsWith('!') || word.EndsWith('?');

    /// <summary>Short lines read best on a phone: at most 3 words, broken at pauses and sentence ends.</summary>
    static List<SubLine> GroupLines(List<SubWord> words)
    {
        var lines = new List<SubLine>();
        SubLine? line = null;
        foreach (var w in words)
        {
            bool newLine = line is null
                || line.Words.Count >= MaxWordsPerLine
                || line.Text.Length + 1 + w.Text.Length > MaxCharsPerLine
                || w.Start - line.End > 0.6
                || EndsSentence(line.Words[^1].Text) || line.Words[^1].Text.EndsWith(',');
            if (newLine)
            {
                line = new SubLine();
                lines.Add(line);
            }
            line!.Words.Add(w);
        }
        return lines;
    }

    /// <summary>The line on screen at <paramref name="time"/> (source seconds), held a little after its last word.</summary>
    public static SubLine? LineAt(IReadOnlyList<SubLine> lines, double time)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var hold = i + 1 < lines.Count ? Math.Min(lines[i + 1].Start, lines[i].End + 0.4) : lines[i].End + 0.4;
            if (time >= lines[i].Start && time < hold) return lines[i];
        }
        return null;
    }

    /// <summary>Displayed form of a word: capitals, no trailing comma or full stop (TikTok style).</summary>
    public static string Display(string word) => word.TrimEnd(',', '.').ToUpper(CultureInfo.CurrentCulture);

    /// <summary>ASS subtitle file for the clip <paramref name="clipStart"/>–<paramref name="clipEnd"/>; the current word is lime.</summary>
    public static string BuildAss(IReadOnlyList<SubLine> lines, double clipStart, double clipEnd, SubtitlePosition position)
    {
        static string T(double s)
        {
            s = Math.Max(0, s);
            int cs = (int)Math.Round(s * 100);
            return $"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}";
        }
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("{", "(").Replace("}", ")");

        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine($"PlayResX: {ShortsLayout.Width}");
        sb.AppendLine($"PlayResY: {ShortsLayout.Height}");
        sb.AppendLine("ScaledBorderAndShadow: yes");
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        sb.AppendLine($"Style: Sub,{FontName},{FontSize.ToString(CultureInfo.InvariantCulture)},&H00FFFFFF,&H00FFFFFF,&H00000000,&H99000000,-1,0,0,0,100,100,0,0,1,7,4,5,70,70,0,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        int y = (int)CenterY(position);
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            double hold = i + 1 < lines.Count ? Math.Min(lines[i + 1].Start, line.End + 0.4) : line.End + 0.4;
            for (int w = 0; w < line.Words.Count; w++)
            {
                double from = w == 0 ? line.Start : line.Words[w].Start;
                double to = w + 1 < line.Words.Count ? line.Words[w + 1].Start : hold;
                // Only the part inside the exported clip, shifted so the clip starts at 0.
                double a = Math.Max(from, clipStart) - clipStart, b = Math.Min(to, clipEnd) - clipStart;
                if (b <= a) continue;

                var text = new StringBuilder($"{{\\pos({ShortsLayout.Width / 2},{y})");
                if (w == 0) text.Append("\\fscx112\\fscy112\\t(0,90,\\fscx100\\fscy100)"); // small pop when a line appears
                text.Append('}');
                for (int k = 0; k < line.Words.Count; k++)
                {
                    if (k > 0) text.Append(' ');
                    var word = Escape(Display(line.Words[k].Text));
                    text.Append(k == w ? $"{{\\c&H16CC84&}}{word}{{\\c&HFFFFFF&}}" : word);
                }
                sb.AppendLine($"Dialogue: 0,{T(a)},{T(b)},Sub,,0,0,0,,{text}");
            }
        }
        return sb.ToString();
    }
}
