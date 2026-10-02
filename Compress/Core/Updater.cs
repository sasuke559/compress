using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Compress.Core;

/// <summary>A newer release on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string PageUrl, string DownloadUrl, long Size, string? Sha256, bool IsZip, string? SignatureUrl);

/// <summary>
/// Keeps the portable app up to date from the GitHub releases.
/// The new Compress.exe is downloaded next to the running one, the running exe is renamed to Compress.exe.old
/// (Windows allows renaming a running exe, not deleting it) and the new one takes its name, so the next start runs the new version.
/// Every update must carry Compress.exe.sig, made with the developer's private key (tools/sign-release.cs); unsigned or
/// tampered files are never installed. If the new version cannot start, the previous one is restored automatically.
/// </summary>
public static class Updater
{
    const string Repo = "sasuke559/compress";
    const string ExeAsset = "Compress.exe";
    const string ZipAsset = "Compress-win-x64.zip";
    const string SignatureAsset = "Compress.exe.sig";

    /// <summary>ECDSA P-256 public key; the matching private key never leaves the developer's PC.</summary>
    const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEdhdNG/qU4JQINHIJh7dQFwFPa48F9z10/hprTpHYNOZijdN+MdQbOZDOZctBJUi61UrDoXwyXCmURayLkvQEig==";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Compress/{Feedback.AppVersion}");
        return http;
    }

    public static Version Current { get; } = Version.TryParse(Feedback.AppVersion, out var v) ? v : new Version(0, 0, 0);

    static string? ExePath => Environment.ProcessPath;
    static string OldPath => ExePath + ".old";
    static string NewPath => ExePath + ".new";
    /// <summary>Exists while a freshly installed version has not started successfully yet; holds the number of start attempts.</summary>
    static string PendingPath => ExePath + ".pending";
    static string BadPath => ExePath + ".bad";
    /// <summary>Version that failed to start and was rolled back; not offered again.</summary>
    static string SkipPath => Path.Combine(FfmpegTools.DataDir, "update-skip.txt");

    /// <summary>True once this start reached the main window; from then on a crash is not blamed on the update.</summary>
    public static bool IsHealthy { get; private set; }

    /// <summary>Set once this process started replacing its own exe; the files next to it then belong to the new version.</summary>
    static bool _installing;

    /// <summary>Only the published exe updates itself; a build started from Visual Studio (bin\…) never does.</summary>
    public static bool CanSelfUpdate =>
        ExePath is { } path
        && Path.GetFileName(path).Equals(ExeAsset, StringComparison.OrdinalIgnoreCase)
        && !path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// First thing at startup, before any window or theme loads. If the previous start of a just installed version
    /// never reached the main window, that version is broken: the old one is restored and started instead (returns true, exit then).
    /// </summary>
    public static bool RollBackIfBroken()
    {
        try
        {
            if (!CanSelfUpdate || !File.Exists(PendingPath) || !File.Exists(OldPath)) return false;
            if (File.ReadAllText(PendingPath).Trim() == "0")
            {
                File.WriteAllText(PendingPath, "1");
                return false;
            }
            return RollBack();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restores the previous version after this freshly installed one failed during startup. Returns true when the old one was started.</summary>
    public static bool RollBack()
    {
        if (IsHealthy || !CanSelfUpdate || !File.Exists(OldPath)) return false;
        try
        {
            var exe = ExePath!;
            Directory.CreateDirectory(FfmpegTools.DataDir);
            File.WriteAllText(SkipPath, Current.ToString(3));
            if (File.Exists(BadPath)) File.Delete(BadPath);
            File.Move(exe, BadPath);
            File.Move(OldPath, exe);
            File.Delete(PendingPath);
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The main window is up: the update worked, so the previous version and leftovers can go.</summary>
    public static void MarkHealthy()
    {
        IsHealthy = true;
        if (!CanSelfUpdate || _installing) return;
        foreach (var path in new[] { PendingPath, OldPath, BadPath, NewPath })
            try { if (File.Exists(path)) File.Delete(path); } catch { /* still in use, try next start */ }
    }

    /// <summary>Latest release if it is newer than this app, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        // COMPRESS_UPDATE_FEED points tests at a local copy of the release JSON; the signature check still applies.
        var feed = Environment.GetEnvironmentVariable("COMPRESS_UPDATE_FEED") ?? $"https://api.github.com/repos/{Repo}/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, feed);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;

        if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V'), out var latest) || latest <= Current)
            return null;
        try
        {
            if (File.Exists(SkipPath) && Version.TryParse(File.ReadAllText(SkipPath).Trim(), out var skip) && skip == latest) return null;
        }
        catch { /* unreadable: offer it */ }

        string? signatureUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
            if (string.Equals(asset.GetProperty("name").GetString(), SignatureAsset, StringComparison.OrdinalIgnoreCase))
                signatureUrl = asset.GetProperty("browser_download_url").GetString();

        UpdateInfo? Asset(string name, bool zip)
        {
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                string? sha = asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:")
                    ? digest["sha256:".Length..] : null;
                return new UpdateInfo(latest, root.GetProperty("html_url").GetString()!, asset.GetProperty("browser_download_url").GetString()!,
                    asset.GetProperty("size").GetInt64(), sha, zip, signatureUrl);
            }
            return null;
        }

        // The bare exe is ~70 MB; the zip (with FFmpeg) is only a fallback for releases without it.
        return Asset(ExeAsset, false) ?? Asset(ZipAsset, true);
    }

    /// <summary>Downloads the update, checks it and puts it in place of the running exe. Takes effect on the next start.</summary>
    public static async Task DownloadAndInstallAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!CanSelfUpdate) throw new InvalidOperationException("This copy of Compress cannot update itself.");
        if (update.SignatureUrl is null) throw new InvalidOperationException("This release is not signed.");
        _installing = true;
        var exe = ExePath!;
        var download = update.IsZip ? Path.Combine(Path.GetTempPath(), $"Compress-{update.Version}.zip") : NewPath;

        try
        {
            using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(download);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report(update.Size > 0 ? Math.Min(1, (double)done / update.Size) : 0);
                }
            }

            if (new FileInfo(download).Length != update.Size) throw new IOException("The download is incomplete.");
            if (update.Sha256 is { } expected)
            {
                await using var file = File.OpenRead(download);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("The download is damaged (checksum mismatch).");
            }

            if (update.IsZip)
            {
                using var zip = ZipFile.OpenRead(download);
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(ExeAsset, StringComparison.OrdinalIgnoreCase))
                    ?? throw new IOException("The update package has no Compress.exe.");
                entry.ExtractToFile(NewPath, overwrite: true);
            }

            var signature = Convert.FromBase64String((await Http.GetStringAsync(update.SignatureUrl, ct)).Trim());
            if (!IsSignedByDeveloper(NewPath, signature)) throw new IOException("The update is not signed by the developer and was not installed.");

            if (File.Exists(OldPath)) File.Delete(OldPath);
            File.Move(exe, OldPath);
            try
            {
                File.Move(NewPath, exe);
                File.WriteAllText(PendingPath, "0");
            }
            catch
            {
                File.Move(OldPath, exe); // put the running version back
                throw;
            }
        }
        finally
        {
            try { if (update.IsZip && File.Exists(download)) File.Delete(download); } catch { /* temp file */ }
            try { if (File.Exists(NewPath)) File.Delete(NewPath); } catch { /* best effort */ }
        }
    }

    static bool IsSignedByDeveloper(string file, byte[] signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
        using var stream = File.OpenRead(file);
        return key.VerifyData(stream, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// <summary>Starts the (updated) exe; the caller closes this instance.</summary>
    public static void StartNewVersion() => Process.Start(new ProcessStartInfo(ExePath!) { UseShellExecute = true });

    public static void OpenReleasePage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser */ }
    }
}
