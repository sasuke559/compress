using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Compress.Core;
using Microsoft.Win32;

namespace Compress.Controls;

/// <summary>Small UI helpers shared by the pages.</summary>
static class Ui
{
    public static readonly string[] VideoExtensions =
        [".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".mpg", ".mpeg", ".ts", ".mts", ".m2ts", ".3gp",
         ".gif", ".ogv", ".vob", ".mxf", ".asf", ".f4v", ".divx"];

    public static bool IsVideo(string file) => VideoExtensions.Contains(Path.GetExtension(file).ToLowerInvariant());

    public static string? PickVideo(DependencyObject owner) => PickVideos(owner, multiple: false).FirstOrDefault();

    public static string[] PickVideos(DependencyObject owner, bool multiple = true)
    {
        var dialog = new OpenFileDialog
        {
            Title = multiple ? "Select videos" : "Select a video",
            Filter = "Videos|" + string.Join(";", VideoExtensions.Select(x => "*" + x)) + "|All files|*.*",
            Multiselect = multiple,
        };
        return dialog.ShowDialog(Window.GetWindow(owner)) == true ? dialog.FileNames : [];
    }

    /// <summary>
    /// Turns a popup into a dropdown for <paramref name="toggle"/>: the button opens and closes it, a click anywhere else,
    /// Escape or leaving the window closes it. (StaysOpen=False closes on mouse down and the same click then reopened it.)
    /// <paramref name="beforeOpen"/> can fill the popup and return false to keep it closed.
    /// </summary>
    public static void MakeDropdown(Popup popup, ButtonBase toggle, Func<bool>? beforeOpen = null)
    {
        popup.StaysOpen = true;
        toggle.Click += (_, _) =>
        {
            if (popup.IsOpen) popup.IsOpen = false;
            else if (beforeOpen?.Invoke() != false) popup.IsOpen = true;
        };

        Window? hooked = null;
        toggle.Loaded += (_, _) =>
        {
            if (hooked is not null || Window.GetWindow(toggle) is not { } window) return;
            hooked = window;
            window.PreviewMouseDown += (_, e) =>
            {
                if (popup.IsOpen && !IsInside(e.OriginalSource as DependencyObject, toggle, popup.Child)) popup.IsOpen = false;
            };
            window.PreviewKeyDown += (_, e) =>
            {
                if (popup.IsOpen && e.Key == Key.Escape)
                {
                    popup.IsOpen = false;
                    e.Handled = true;
                }
            };
            window.Deactivated += (_, _) => popup.IsOpen = false;
            window.LocationChanged += (_, _) => popup.IsOpen = false;
        };
    }

    static bool IsInside(DependencyObject? element, params DependencyObject?[] containers)
    {
        // A popup's content has no visual parent, so fall back to the logical tree to climb out of it.
        for (var e = element; e is not null; e = (e is Visual ? VisualTreeHelper.GetParent(e) : null) ?? LogicalTreeHelper.GetParent(e))
            if (containers.Contains(e)) return true;
        return false;
    }

    /// <summary>Shows exactly one of the panels, fading it in.</summary>
    public static void ShowOnly(FrameworkElement panel, params FrameworkElement[] all)
    {
        foreach (var p in all)
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
        panel.Opacity = 0;
        panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
    }

    /// <summary>Largest size with the given aspect ratio that fits into the available space.</summary>
    public static Size Fit(double availableWidth, double availableHeight, double aspect)
    {
        if (availableWidth <= 0 || availableHeight <= 0) return Size.Empty;
        if (aspect <= 0 || double.IsNaN(aspect)) aspect = 16.0 / 9;
        double w = availableWidth, h = w / aspect;
        if (h > availableHeight)
        {
            h = availableHeight;
            w = h * aspect;
        }
        return new Size(Math.Floor(w), Math.Floor(h));
    }

    public static double Aspect(VideoInfo v) =>
        v.DisplayWidth > 0 && v.DisplayHeight > 0 ? (double)v.DisplayWidth / v.DisplayHeight : 16.0 / 9;

    /// <summary>Loads an image fully into memory so the file can be deleted right away.</summary>
    public static BitmapImage LoadBitmap(string file)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(file);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static void TryDelete(string? file)
    {
        if (file is null) return;
        try { File.Delete(file); } catch { /* temp file, best effort */ }
    }

    public static void ShowInExplorer(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>Puts the file on the clipboard so it can be pasted into Discord, Explorer, mail, …</summary>
    public static bool CopyFileToClipboard(string? path)
    {
        if (path is null || !File.Exists(path)) return false;
        try
        {
            Clipboard.SetFileDropList(new StringCollection { path });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void SetTaskbarProgress(DependencyObject element, double? value)
    {
        if (Window.GetWindow(element) is not { } window) return;
        window.TaskbarItemInfo ??= new TaskbarItemInfo();
        window.TaskbarItemInfo.ProgressState = value is null ? TaskbarItemProgressState.None : TaskbarItemProgressState.Normal;
        if (value is { } v) window.TaskbarItemInfo.ProgressValue = v;
    }

    /// <summary>Flashes the taskbar and plays a sound when a job finishes in the background.</summary>
    public static void NotifyFinished(DependencyObject element)
    {
        if (Window.GetWindow(element) is not { IsActive: false } window) return;
        NativeMethods.FlashUntilFocused(window);
        System.Media.SystemSounds.Asterisk.Play();
    }
}
