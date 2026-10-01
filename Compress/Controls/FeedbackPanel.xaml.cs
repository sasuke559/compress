using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Compress.Core;

namespace Compress.Controls;

/// <summary>
/// Overlay where users report bugs or suggest ideas. The text stays when the panel is closed,
/// so an accidental click outside does not throw a half-written report away.
/// </summary>
public partial class FeedbackPanel : UserControl
{
    /// <summary>Supplies the system info block (app, Windows, FFmpeg, GPU, current page) when sending.</summary>
    public Func<string>? SystemInfoProvider { get; set; }

    FeedbackKind Kind => KindIdea.IsChecked == true ? FeedbackKind.Idea : FeedbackKind.Bug;

    public FeedbackPanel()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
        UpdateTexts();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public void Open(FeedbackKind? kind = null, string? title = null, string? description = null)
    {
        if (kind is { } k) (k == FeedbackKind.Idea ? KindIdea : KindBug).IsChecked = true;
        if (title is not null) TitleBox.Text = title;
        if (description is not null) DescriptionBox.Text = description;
        StatusText.Text = "Opens your browser with the report filled in. Sending needs a free GitHub account.";
        CopyText.Text = "Copy";

        Visibility = Visibility.Visible;
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        Dispatcher.BeginInvoke(() => (string.IsNullOrWhiteSpace(TitleBox.Text) ? TitleBox : DescriptionBox).Focus(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Close() => Visibility = Visibility.Collapsed;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && SendButton.IsEnabled)
        {
            Send_Click(this, e);
            e.Handled = true;
        }
    }

    void Kind_Checked(object sender, RoutedEventArgs e) => UpdateTexts();

    void UpdateTexts()
    {
        if (TitleHint is null || DescriptionHint is null) return; // Checked fires during InitializeComponent
        bool bug = Kind == FeedbackKind.Bug;
        TitleHint.Text = bug ? "Short summary, e.g. \"Cutter freezes on 4K clips\"" : "Short summary, e.g. \"Add a speed slider to the Cutter\"";
        DescriptionLabel.Text = bug ? "WHAT HAPPENED?" : "YOUR IDEA";
        DescriptionHint.Text = bug
            ? "What did you do, what happened, and what did you expect instead? Steps to repeat it help the most."
            : "What should Compress do, and what would you use it for?";
    }

    void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool ready = TitleBox.Text.Trim().Length >= 3 && DescriptionBox.Text.Trim().Length >= 5;
        SendButton.IsEnabled = CopyButton.IsEnabled = ready;
    }

    (string Title, string Body) BuildReport()
    {
        string? info = SystemInfoSwitch.IsChecked == true ? SystemInfoProvider?.Invoke() : null;
        return (Feedback.IssueTitle(Kind, TitleBox.Text), Feedback.Body(Kind, DescriptionBox.Text, info));
    }

    bool CopyToClipboard(string title, string body)
    {
        try
        {
            Clipboard.SetText($"{title}\n\n{body}");
            return true;
        }
        catch
        {
            return false; // clipboard can be locked by another app
        }
    }

    void Send_Click(object sender, RoutedEventArgs e)
    {
        var (title, body) = BuildReport();
        CopyToClipboard(title, body); // backup in case the browser form gets lost or the URL had to be shortened
        try
        {
            Process.Start(new ProcessStartInfo(Feedback.GitHubIssueUrl(title, body)) { UseShellExecute = true });
            StatusText.Text = "Your browser opened with the report. Click \"Create\" there to send it. A copy is also on your clipboard.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open the browser ({ex.Message}). The report is on your clipboard instead.";
        }
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        var (title, body) = BuildReport();
        CopyText.Text = CopyToClipboard(title, body) ? "Copied" : "Failed";
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
