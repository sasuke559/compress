using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Compress.Core;

/// <summary>
/// Anonymous usage counters: "installed", "used today" (at most once a day) and "exported a video with tool X".
/// Nothing identifies the user: no ID, no file names, no system details beyond the app version.
/// Can be turned off in Settings.
/// </summary>
public static class Usage
{
    /// <summary>Compress backend (Cloudflare Worker, source in /server). Also relays feedback to Discord.</summary>
    public const string Server = "https://compress-stats.compress-stats.workers.dev";

    static readonly HttpClient Http = CreateClient();
    static AppSettings? _settings;

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Compress/{Feedback.AppVersion}");
        return http;
    }

    static bool Enabled => _settings?.UsageStats == true;

    /// <summary>Called once at startup: reports the first start ever and the first start of each day.</summary>
    public static void Start(AppSettings settings)
    {
        _settings = settings;
        if (!Enabled) return;
        _ = ReportStartAsync();
    }

    static async Task ReportStartAsync()
    {
        var settings = _settings!;
        var version = Feedback.AppVersion;
        if (!settings.UsageInstallSent && await PostAsync(new { e = "install", v = version }))
        {
            settings.UsageInstallSent = true;
            settings.Save();
        }
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (settings.UsageLastActiveDay != today && await PostAsync(new { e = "active", v = version }))
        {
            settings.UsageLastActiveDay = today;
            settings.Save();
        }
    }

    /// <summary>One finished export; <paramref name="tool"/> is compress, cutter, resize, shorts or converter.</summary>
    public static void Export(string tool)
    {
        if (Enabled) _ = PostAsync(new { e = "export", t = tool });
    }

    static async Task<bool> PostAsync(object body)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(Server + "/ping", content);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false; // offline or server down: statistics must never bother the user
        }
    }
}
