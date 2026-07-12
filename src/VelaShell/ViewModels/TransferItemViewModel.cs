using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

public class TransferItemViewModel : ReactiveObject
{
    private readonly TransferTask _task;

    private int _progress;
    private string _speed = string.Empty;
    private TransferStatus _status;
    private string _timeRemaining = string.Empty;
    private long _totalSize;
    private long _transferredBytes;

    public TransferItemViewModel(TransferTask task)
    {
        _task = task ?? throw new ArgumentNullException(nameof(task));
        _status = task.Status;
        if (task.Progress == null)
        {
            return;
        }
        _totalSize = task.Progress.TotalBytes;
        _transferredBytes = task.Progress.BytesTransferred;
        _progress = task.Progress.Percentage;
        _speed = FormatSpeed(task.Progress.SpeedBytesPerSecond);
        _timeRemaining = FormatTimeRemaining(task.Progress.EstimatedTimeRemaining);
    }

    public Guid Id => _task.Id;

    public string FileName => Path.GetFileName(_task.RemotePath);

    public string Direction => _task.Type == TransferType.Upload ? "↑" : "↓";

    public int Progress
    {
        get => _progress;
        private set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    public string Speed
    {
        get => _speed;
        private set => this.RaiseAndSetIfChanged(ref _speed, value);
    }

    public string TimeRemaining
    {
        get => _timeRemaining;
        private set => this.RaiseAndSetIfChanged(ref _timeRemaining, value);
    }

    public TransferStatus Status
    {
        get => _status;
        set
        {
            this.RaiseAndSetIfChanged(ref _status, value);
            _task.Status = value;
            this.RaisePropertyChanged(nameof(IsActive));
            this.RaisePropertyChanged(nameof(IsFailed));
            this.RaisePropertyChanged(nameof(IsCompleted));
            this.RaisePropertyChanged(nameof(ProgressText));
            this.RaisePropertyChanged(nameof(InfoLine));
        }
    }

    public bool IsActive => _status is TransferStatus.InProgress or TransferStatus.Queued;

    public bool IsFailed => _status == TransferStatus.Failed;

    public bool IsCompleted => _status == TransferStatus.Completed;

    public long TotalSize
    {
        get => _totalSize;
        private set => this.RaiseAndSetIfChanged(ref _totalSize, value);
    }

    public long TransferredBytes
    {
        get => _transferredBytes;
        private set => this.RaiseAndSetIfChanged(ref _transferredBytes, value);
    }

    /// <summary>Right-hand status: "67%" while running, "完成" when done, "失败" on error.</summary>
    public string ProgressText => _status switch
    {
        TransferStatus.Completed => Strings.Get("Msg_Done"),
        TransferStatus.Failed => Strings.Get("Msg_Failed"),
        _ => $"{_progress}%"
    };

    /// <summary>Detail line per design 9Ralg: "142 MB / 212 MB  •  4.2 MB/s  •  ↑ 上传中".</summary>
    public string InfoLine
    {
        get
        {
            string action = _task.Type == TransferType.Upload ? $"↑ {Strings.Get("Msg_Uploading")}" : $"↓ {Strings.Get("Msg_Downloading")}";
            if (_status == TransferStatus.Completed)
            {
                action = _task.Type == TransferType.Upload ? $"↑ {Strings.Get("Msg_Uploaded")}" : $"↓ {Strings.Get("Msg_Downloaded")}";
            }
            else if (_status == TransferStatus.Failed)
            {
                action = Strings.Get("Msg_Failed");
            }
            return _status == TransferStatus.Completed
                       ? $"{FormatBytes(_totalSize)}  •  {Strings.Get("Msg_Completed")}  •  {action}"
                       : $"{FormatBytes(_transferredBytes)} / {FormatBytes(_totalSize)}  •  {_speed}  •  {action}";
        }
    }

    public void UpdateProgress(TransferProgress progress)
    {
        Progress = progress.Percentage;
        TransferredBytes = progress.BytesTransferred;
        TotalSize = progress.TotalBytes;
        Speed = FormatSpeed(progress.SpeedBytesPerSecond);
        TimeRemaining = FormatTimeRemaining(progress.EstimatedTimeRemaining);
        this.RaisePropertyChanged(nameof(InfoLine));
        this.RaisePropertyChanged(nameof(ProgressText));
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = Math.Min((int)Math.Floor(Math.Log(bytes, 1024)), units.Length - 1);
        double value = bytes / Math.Pow(1024, i);
        return i == 0 ? $"{bytes} B" : $"{value:F1} {units[i]}";
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return "0 B/s";
        }
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
        int i = (int)Math.Floor(Math.Log(bytesPerSecond, 1024));
        i = Math.Min(i, units.Length - 1);
        double value = bytesPerSecond / Math.Pow(1024, i);
        return i == 0
                   ? $"{(int)value} {units[0]}"
                   : $"{value:F1} {units[i]}";
    }

    public static string FormatTimeRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return string.Empty;
        }
        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        }
        if (remaining.TotalMinutes >= 1)
        {
            return $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s";
        }
        return $"{remaining.Seconds}s";
    }
}
