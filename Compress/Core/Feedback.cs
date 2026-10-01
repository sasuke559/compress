using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Compress.Core;

public enum FeedbackKind { Bug, Idea }

/// <summary>
/// Builds bug reports and feature ideas and hands them to the developer.
/// Delivery is a prefilled GitHub issue: no server, no secrets in the exe, and the report lands in the repo's issue list.
/// </summary>
public static class Feedback
{
    const string Repo = "sasuke559/compress";

    /// <summary>GitHub rejects longer new-issue URLs, so the body is trimmed until the URL fits.</summary>
    const int MaxUrlLength = 7500;

    public static string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    public static string IssueTitle(FeedbackKind kind, string title) =>
        $"[{(kind == FeedbackKind.Bug ? "Bug" : "Idea")}] {title.Trim()}";

    public static string Body(FeedbackKind kind, string description, string? systemInfo)
    {
        var sb = new StringBuilder();
        sb.AppendLine(kind == FeedbackKind.Bug ? "### What happened" : "### Idea");
        sb.AppendLine(description.Trim());
        if (!string.IsNullOrWhiteSpace(systemInfo))
        {
            sb.AppendLine();
            sb.AppendLine("### System");
            sb.AppendLine("```");
            sb.AppendLine(systemInfo.Trim());
            sb.AppendLine("```");
        }
        sb.AppendLine();
        sb.Append($"_Sent from Compress {AppVersion}_");
        return sb.ToString();
    }

    public static string SystemInfo(AppState state, string? ffmpegVersion, string page)
    {
        var enc = state.Encoders;
        var lines = new List<string>
        {
            $"App: Compress {AppVersion}",
            $"Windows: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            $"FFmpeg: {(state.Tools is null ? "not installed" : ffmpegVersion ?? "found")}",
            $"GPU encoding: {(enc.Any ? $"H.264 {Format.Vendor(enc.H264)}, H.265 {Format.Vendor(enc.Hevc)}" : "none, CPU")}",
            $"Page: {page}",
        };
        if (LastCrash() is { } crash) lines.Add($"Last error:\n{crash}");
        return string.Join("\n", lines);
    }

    /// <summary>Newest entry of crash.log, shortened so it fits into the issue URL.</summary>
    static string? LastCrash()
    {
        try
        {
            var log = Path.Combine(FfmpegTools.DataDir, "crash.log");
            if (!File.Exists(log)) return null;
            var entries = File.ReadAllText(log).Split("\n[", StringSplitOptions.RemoveEmptyEntries);
            var last = entries[^1].Trim();
            if (!last.StartsWith('[')) last = "[" + last;
            return last.Length > 1500 ? last[..1500] + " …" : last;
        }
        catch
        {
            return null;
        }
    }

    public static string GitHubIssueUrl(string title, string body)
    {
        string Url(string b) => $"https://github.com/{Repo}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(b)}";

        var url = Url(body);
        for (int keep = body.Length; url.Length > MaxUrlLength && keep > 200; )
        {
            keep = keep * 4 / 5;
            url = Url(body[..keep] + "\n\n_(shortened, paste the full text from the clipboard)_");
        }
        return url;
    }
}
