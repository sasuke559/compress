using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Compress.Core;

namespace Compress.Controls;

/// <summary>
/// "SAVE AS  [New file | Replace original]" with a line explaining the choice. Every page shows the same setting
/// (<see cref="AppSettings.ReplaceOriginal"/>), so changing it on one page changes it everywhere.
/// </summary>
public sealed class SaveModeSelector : StackPanel
{
    static int _instances;

    readonly RadioButton _newFile, _replace;
    readonly TextBlock _note;
    AppState? _state;
    bool _updating;

    /// <summary>Explanation shown for "New file", e.g. which suffix the name gets.</summary>
    public string NewFileHint { get; set; } = "The original stays as it is; the result is saved next to it.";

    /// <summary>Explanation shown for "Replace original".</summary>
    public string ReplaceHint { get; set; } = "The result takes the original's name. The original goes to the Recycle Bin.";

    public SaveModeSelector()
    {
        // RadioButton groups are window-wide, so every selector needs its own group.
        string group = "SaveMode" + ++_instances;

        var label = new TextBlock { Text = "SAVE AS" };
        label.SetResourceReference(StyleProperty, "Label");

        _newFile = Segment("New file", group, "Keep the original and save the result next to it");
        _replace = Segment("Replace original", group, "The result takes the original's place; the original goes to the Recycle Bin");
        var grid = new UniformGrid { Rows = 1 };
        grid.Children.Add(_newFile);
        grid.Children.Add(_replace);
        var host = new Border { Child = grid, Margin = new Thickness(0, 0, 0, 6) };
        host.SetResourceReference(StyleProperty, "SegmentHost");

        _note = new TextBlock { Margin = new Thickness(2, 0, 2, 14) };
        _note.SetResourceReference(StyleProperty, "CardText");

        Children.Add(label);
        Children.Add(host);
        Children.Add(_note);
    }

    RadioButton Segment(string text, string group, string tip)
    {
        var radio = new RadioButton { Content = text, GroupName = group, ToolTip = tip };
        radio.SetResourceReference(StyleProperty, "Segment");
        radio.Checked += Changed;
        return radio;
    }

    public void Initialize(AppState state)
    {
        _state = state;
        state.EngineChanged += (_, _) => Update();
        Update();
    }

    void Update()
    {
        if (_state is null) return;
        _updating = true;
        (_state.Settings.ReplaceOriginal ? _replace : _newFile).IsChecked = true;
        _updating = false;
        _note.Text = _state.Settings.ReplaceOriginal ? ReplaceHint : NewFileHint;
    }

    void Changed(object sender, RoutedEventArgs e)
    {
        if (_updating || _state is null) return;
        _state.Settings.ReplaceOriginal = _replace.IsChecked == true;
        _state.Settings.Save();
        _state.NotifyEngineChanged(); // the other pages show the same choice
    }
}
