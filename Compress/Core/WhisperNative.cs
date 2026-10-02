using System.Reflection;
using Whisper.net.LibraryLoader;

namespace Compress.Core;

/// <summary>
/// The whisper.cpp libraries travel inside Compress.exe (embedded resources) and are unpacked into the data folder the
/// first time subtitles are made. That keeps the app a single exe, which is also all the auto-update replaces.
/// </summary>
static class WhisperNative
{
    const string Prefix = "whisper/";
    static readonly Lock Gate = new();
    static bool _loaded;

    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version?.ToString() ?? "0";
            var dir = Path.Combine(FfmpegTools.DataDir, "whisper", version);
            // Whisper.net looks for runtimes/win-x64/whisper.dll (and its ggml-*.dll) below the folder of LibraryPath.
            var runtimeDir = Path.Combine(dir, "runtimes", "win-x64");
            Directory.CreateDirectory(runtimeDir);

            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
            {
                var target = Path.Combine(runtimeDir, name[Prefix.Length..]);
                using var source = assembly.GetManifestResourceStream(name)!;
                if (File.Exists(target) && new FileInfo(target).Length == source.Length) continue;
                var part = target + ".part";
                using (var file = File.Create(part)) source.CopyTo(file);
                File.Move(part, target, overwrite: true);
            }

            // Copies unpacked by older app versions are no longer used.
            foreach (var old in Directory.GetDirectories(Path.GetDirectoryName(dir)!).Where(d => !string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)))
                try { Directory.Delete(old, recursive: true); } catch { /* in use by another running copy */ }

            RuntimeOptions.LibraryPath = Path.Combine(dir, "whisper.dll");
            _loaded = true;
        }
    }
}
