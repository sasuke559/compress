using Microsoft.VisualBasic.FileIO;

namespace Compress.Core;

/// <summary>"Replace original" exports: the finished file takes the place of the video it was made from.</summary>
public static class OutputFiles
{
    /// <summary>
    /// Moves <paramref name="original"/> to the Recycle Bin (so a mistake can be undone) and gives <paramref name="output"/>
    /// its name, keeping the output's extension (a cut MKV stays MKV, a re-encoded one becomes MP4).
    /// The original must not be open anywhere in the app (close the player first).
    /// Returns the final path and, when the original had to be kept, why.
    /// </summary>
    public static async Task<(string Path, string? Note)> ReplaceOriginalAsync(string original, string output)
    {
        var target = Path.ChangeExtension(original, Path.GetExtension(output));
        bool sameName = string.Equals(target, original, StringComparison.OrdinalIgnoreCase);
        if (!sameName && File.Exists(target))
            return (output, $"Kept the original because {Path.GetFileName(target)} already exists.");

        // The player may need a moment to release the file after closing.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(original))
                    await Task.Run(() => FileSystem.DeleteFile(original, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin));
                break;
            }
            catch (OperationCanceledException)
            {
                return (output, "Kept the original (moving it to the Recycle Bin was cancelled).");
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(200);
            }
            catch (Exception ex)
            {
                return (output, $"Kept the original: {ex.Message}");
            }
        }

        File.Move(output, target);
        return (target, null);
    }
}
