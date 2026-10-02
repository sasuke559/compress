using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Compress.Core;

public enum FeedbackKind { Bug, Idea }

/// <summary>
/// Builds bug reports and feature ideas and posts them to the developer's Discord channel.
/// Users need no account; the Compress server (see <see cref="Usage.Server"/>) relays the report, so no webhook URL ships in the exe.
/// </summary>
public static class Feedback
{
    /// <summary>Minimum time between two reports, so a stuck key or an impatient user cannot flood the channel.</summary>
    static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    static DateTime _lastSent = DateTime.MinValue;

    public static string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    public static string Title(FeedbackKind kind, string title) =>
        $"[{(kind == FeedbackKind.Bug ? "Bug" : "Idea")}] {title.Trim()}";

    /// <summary>Plain-text version of the report, for the clipboard.</summary>
    public static string PlainText(FeedbackKind kind, string title, string description, string? contact, string? systemInfo)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Title(kind, title)).AppendLine();
        sb.AppendLine(description.Trim());
        if (!string.IsNullOrWhiteSpace(contact)) sb.AppendLine().AppendLine($"Contact: {contact.Trim()}");
        if (!string.IsNullOrWhiteSpace(systemInfo)) sb.AppendLine().AppendLine(systemInfo.Trim());
        if (LastCrash() is { } crash && systemInfo is not null) sb.AppendLine().AppendLine("Last error:").AppendLine(crash);
        return sb.ToString().TrimEnd();
    }

    public static string SystemInfo(AppState state, string? ffmpegVersion, string page)
    {
        var enc = state.Encoders;
        return string.Join("\n",
            $"App: Compress {AppVersion}",
            $"Windows: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            $"FFmpeg: {(state.Tools is null ? "not installed" : ffmpegVersion ?? "found")}",
            $"GPU encoding: {(enc.Any ? $"H.264 {Format.Vendor(enc.H264)}, H.265 {Format.Vendor(enc.Hevc)}" : "none, CPU")}",
            $"Page: {page}");
    }

    /// <summary>Newest entry of crash.log, sent as a text attachment with bug reports.</summary>
    public static string? LastCrash()
    {
        try
        {
            var log = Path.Combine(FfmpegTools.DataDir, "crash.log");
            if (!File.Exists(log)) return null;
            var entries = File.ReadAllText(log).Split("\n[", StringSplitOptions.RemoveEmptyEntries);
            var last = entries[^1].Trim();
            if (!last.StartsWith('[')) last = "[" + last;
            return last.Length > 20000 ? last[..20000] + " …" : last;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Seconds the user still has to wait before the next report, 0 when sending is allowed.</summary>
    public static int CooldownLeft => Math.Max(0, (int)Math.Ceiling((Cooldown - (DateTime.UtcNow - _lastSent)).TotalSeconds));

    /// <summary>Posts the report as a Discord embed; system info and the last error go along when <paramref name="systemInfo"/> is set.</summary>
    public static async Task SendAsync(FeedbackKind kind, string title, string description, string? contact, string? systemInfo)
    {
        if (CooldownLeft > 0) throw new InvalidOperationException($"Please wait {CooldownLeft} s before sending another report.");

        static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

        var fields = new List<object>();
        if (!string.IsNullOrWhiteSpace(contact))
            fields.Add(new { name = "Contact", value = Cut(contact.Trim(), 200), inline = false });
        if (!string.IsNullOrWhiteSpace(systemInfo))
            fields.Add(new { name = "System", value = $"```\n{Cut(systemInfo.Trim(), 990)}\n```", inline = false });

        var payload = new
        {
            username = "Compress Feedback",
            allowed_mentions = new { parse = Array.Empty<string>() }, // user text must never ping @everyone
            embeds = new[]
            {
                new
                {
                    title = Cut(Title(kind, title), 256),
                    description = Cut(description.Trim(), 4000),
                    color = kind == FeedbackKind.Bug ? 0xEF4444 : 0x84CC16,
                    fields,
                    footer = new { text = $"Compress {AppVersion}" },
                    timestamp = DateTime.UtcNow.ToString("o"),
                },
            },
        };

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), "payload_json");
        if (systemInfo is not null && LastCrash() is { } crash)
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(crash)), "files[0]", "last-error.txt");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Compress/{AppVersion}");
        using var response = await http.PostAsync(Usage.Server + "/feedback", form);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? "Too many reports right now, try again in a minute."
                : $"The server answered {(int)response.StatusCode}.");

        _lastSent = DateTime.UtcNow;
    }
}
