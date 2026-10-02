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
        if (!_sending) SetStatus("Goes straight to the developer, no account needed.");
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
        if (SendText is null) return; // fires during InitializeComponent
        bool ready = TitleBox.Text.Trim().Length >= 3 && DescriptionBox.Text.Trim().Length >= 5;
        SendButton.IsEnabled = ready && !_sending;
        CopyButton.IsEnabled = ready;
        if (ready && SendText.Text == "Sent") SendText.Text = "Send";
    }

    bool _sending;

    string? SystemInfo => SystemInfoSwitch.IsChecked == true ? SystemInfoProvider?.Invoke() : null;

    void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(error ? "Danger" : "Text3");
    }

    bool CopyToClipboard()
    {
        try
        {
            Clipboard.SetText(Feedback.PlainText(Kind, TitleBox.Text, DescriptionBox.Text, ContactBox.Text, SystemInfo));
            return true;
        }
        catch
        {
            return false; // clipboard can be locked by another app
        }
    }

    async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_sending) return;
        if (Feedback.CooldownLeft > 0)
        {
            SetStatus($"Thanks! Please wait {Feedback.CooldownLeft} s before sending another report.");
            return;
        }

        _sending = true;
        SendButton.IsEnabled = false;
        SendText.Text = "Sending…";
        SetStatus("Sending your report…");
        try
        {
            await Feedback.SendAsync(Kind, TitleBox.Text, DescriptionBox.Text, ContactBox.Text, SystemInfo);
            TitleBox.Clear();
            DescriptionBox.Clear();
            SetStatus("Sent, thank you! Every report is read.");
            SendText.Text = "Sent";
        }
        catch (Exception ex)
        {
            bool copied = CopyToClipboard();
            SetStatus($"Could not send ({ex.Message}){(copied ? " The report was copied to your clipboard, so nothing is lost." : "")}", error: true);
            SendText.Text = "Send";
            Input_TextChanged(this, null!);
        }
        finally
        {
            _sending = false;
        }
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        CopyText.Text = CopyToClipboard() ? "Copied" : "Failed";
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
