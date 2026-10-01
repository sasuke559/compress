using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Compress.Core;

namespace Compress.Controls;

/// <summary>Percentage, progress bar, elapsed/remaining time and speed for a running job.</summary>
public partial class ProgressView : UserControl
{
    readonly Stopwatch _total = new();
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    TimeSpan _stageStart;

    public event EventHandler? CancelRequested;

    public ProgressView()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => ElapsedText.Text = Format.Time(_total.Elapsed);
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public TimeSpan Elapsed => _total.Elapsed;

    public void Start()
    {
        _total.Restart();
        _clock.Start();
        ElapsedText.Text = "0:00";
        Ui.SetTaskbarProgress(this, 0);
    }

    /// <summary>Resets the bar for a new encode (e.g. an automatic retry).</summary>
    public void BeginStage(string stage, int passCount)
    {
        _stageStart = _total.Elapsed;
        StageText.Text = stage;
        PercentText.Text = "0%";
        Bar.Value = 0;
        RemainingText.Text = "—";
        SpeedText.Text = "—";
        PassText.Text = $"1 / {passCount}";
    }

    public void Report(EncodeProgress p)
    {
        Bar.Value = p.Fraction;
        PercentText.Text = $"{Math.Floor(p.Fraction * 100):0}%";
        PassText.Text = $"{p.Pass} / {p.PassCount}";
        if (p.Speed > 0) SpeedText.Text = p.Speed.ToString("0.0", CultureInfo.InvariantCulture) + "×";
        if (p.Fraction > 0.02)
        {
            double elapsed = (_total.Elapsed - _stageStart).TotalSeconds;
            RemainingText.Text = Format.Time(TimeSpan.FromSeconds(elapsed * (1 - p.Fraction) / p.Fraction));
        }
        Ui.SetTaskbarProgress(this, p.Fraction);
    }

    public void Stop()
    {
        _total.Stop();
        _clock.Stop();
        Ui.SetTaskbarProgress(this, null);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
}
