using System.Windows;
using System.Windows.Threading;
using Compress.Core;

namespace Compress;

public partial class App : Application
{
    public App()
    {
        // Runs before the theme and the main window load, so even an update that crashes while starting can be undone.
        if (Updater.RollBackIfBroken()) Environment.Exit(0);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log(e.ExceptionObject.ToString());
            Updater.RollBack(); // only acts while a fresh update is still starting
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }

    static string Log(string? error)
    {
        string log = Path.Combine(FfmpegTools.DataDir, "crash.log");
        try
        {
            Directory.CreateDirectory(FfmpegTools.DataDir);
            File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging is best effort */ }
        return log;
    }

    /// <summary>
    /// Last line of defense: an unexpected error in the UI shows a message and is written to crash.log
    /// instead of closing the app (and a running job) without a word.
    /// </summary>
    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string log = Log(e.Exception.ToString());

        // A freshly installed update that fails while starting goes back to the previous version.
        if (Updater.RollBack()) Environment.Exit(1);

        e.Handled = true;
        var answer = MessageBox.Show(MainWindow,
            $"Something went wrong:\n\n{e.Exception.Message}\n\nThe app keeps running. Details were saved to:\n{log}\n\nReport this bug so it can be fixed?",
            "Compress", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes && MainWindow is MainWindow main) main.ReportError(e.Exception);
    }
}
