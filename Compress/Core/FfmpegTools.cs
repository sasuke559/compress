using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Compress.Core;

/// <summary>Locates, downloads and runs the ffmpeg / ffprobe executables.</summary>
public sealed record FfmpegTools(string Ffmpeg, string Ffprobe)
{
    public static string DataDir { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compress");

    public static string ToolsDir => System.IO.Path.Combine(DataDir, "ffmpeg");

    static readonly string[] DownloadUrls =
    [
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
    ];

    public string Directory => System.IO.Path.GetDirectoryName(Ffmpeg) ?? "";

    public static FfmpegTools? Locate(string? preferredDir)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredDir)) dirs.Add(preferredDir);
        dirs.Add(AppContext.BaseDirectory);
        dirs.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
        dirs.Add(ToolsDir);
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        dirs.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));

        foreach (var dir in dirs)
        {
            try
            {
                var ffmpeg = System.IO.Path.Combine(dir, "ffmpeg.exe");
                var ffprobe = System.IO.Path.Combine(dir, "ffprobe.exe");
                if (File.Exists(ffmpeg) && File.Exists(ffprobe)) return new FfmpegTools(ffmpeg, ffprobe);
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return null;
    }

    public static async Task<FfmpegTools> DownloadAsync(IProgress<double> progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Compress/1.0");
        Exception? last = null;

        foreach (var url in DownloadUrls)
        {
            var zipPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"compress-ffmpeg-{Guid.NewGuid():N}.zip");
            try
            {
                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    long? total = response.Content.Headers.ContentLength;
                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    await using var target = File.Create(zipPath);
                    var buffer = new byte[1 << 16];
                    long read = 0;
                    int n;
                    while ((n = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, n), ct);
                        read += n;
                        if (total > 0) progress.Report(0.95 * read / total.Value);
                    }
                }

                System.IO.Directory.CreateDirectory(ToolsDir);
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var exe in new[] { "ffmpeg.exe", "ffprobe.exe" })
                    {
                        var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("bin/" + exe, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException($"{exe} not found in the downloaded archive.");
                        entry.ExtractToFile(System.IO.Path.Combine(ToolsDir, exe), overwrite: true);
                    }
                }
                progress.Report(1);
                return Locate(ToolsDir) ?? throw new InvalidDataException("FFmpeg could not be installed.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
            finally
            {
                try { File.Delete(zipPath); } catch { /* best effort */ }
            }
        }
        throw new InvalidOperationException("FFmpeg download failed: " + last?.Message, last);
    }

    public async Task<string> GetVersionAsync()
    {
        var (_, output, _) = await RunCaptureAsync(Ffmpeg, ["-hide_banner", "-version"]);
        var parts = output.Split(' ', 4);
        return parts.Length > 2 ? parts[2] : "unknown";
    }

    internal static async Task<(int ExitCode, string StdOut, string StdErr)> RunCaptureAsync(
        string exe, IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        return (p.ExitCode, await stdout, await stderr);
    }
}
