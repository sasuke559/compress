using System.ComponentModel;
using System.Runtime.CompilerServices;
using Compress.Core;

namespace Compress.Pages;

public enum QueueStatus { Reading, Ready, Processing, Done, Skipped, Failed }

/// <summary>One video in a batch queue. Bound to the queue list, so it notifies on every change.</summary>
public sealed class QueueItem(string path) : INotifyPropertyChanged
{
    QueueStatus _status = QueueStatus.Reading;
    double _progress;
    string _statusText = "Reading…";
    string _details = "";
    VideoInfo? _info;

    public event PropertyChangedEventHandler? PropertyChanged;

    string _path = path;

    /// <summary>The video in the queue. Changes when a "replace original" export put a file with another extension in its place.</summary>
    public string Path
    {
        get => _path;
        set
        {
            _path = value;
            Notify();
            Notify(nameof(FileName));
        }
    }

    public string FileName => System.IO.Path.GetFileName(Path);
    public string? OutputPath { get; set; }

    public VideoInfo? Info
    {
        get => _info;
        set
        {
            _info = value;
            Details = value is null ? "" :
                $"{value.DisplayWidth} × {value.DisplayHeight}  ·  {Format.Fps(value.Fps)} fps  ·  {Format.Time(value.Duration)}  ·  {Format.Size(value.SizeBytes)}";
            Notify();
        }
    }

    public string Details
    {
        get => _details;
        private set { _details = value; Notify(); }
    }

    public QueueStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            Notify();
            Notify(nameof(IsProcessing));
            Notify(nameof(CanRemove));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; Notify(); }
    }

    string? _message;

    /// <summary>Error details, shown as tooltip.</summary>
    public string? Message
    {
        get => _message;
        set { _message = value; Notify(); }
    }

    public double Progress
    {
        get => _progress;
        set { _progress = value; Notify(); }
    }

    public bool IsProcessing => Status == QueueStatus.Processing;
    public bool CanRemove => Status != QueueStatus.Processing;

    void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
