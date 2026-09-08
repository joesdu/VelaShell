using ReactiveUI;
using VelaShell.Core.Resources;
using VelaShell.Services;

namespace VelaShell.ViewModels;

/// <summary>
/// 传输浮窗「待回传」分组里的一行:一个<b>还没了结</b>的远程编辑会话。
/// </summary>
/// <remarks>
/// 只在回传失败、或有改动还没传上去时才存在 —— 编辑保存一切顺利的话,用户从头到尾
/// 看不到这一行。它守的是 #396 里最要命的那半:回传失败时本地副本是那份改动
/// <b>唯一的存身之处</b>,得让用户看得见、重试得了、找得到它在哪。
/// </remarks>
public sealed class RemoteEditItemViewModel : ReactiveObject
{
    private RemoteEditSnapshot _snapshot;

    /// <summary>用一份会话快照建行。</summary>
    /// <param name="snapshot">会话状态快照。</param>
    public RemoteEditItemViewModel(RemoteEditSnapshot snapshot) => _snapshot = snapshot;

    /// <summary>会话标识(命令参数)。</summary>
    public Guid Id => _snapshot.Id;

    /// <summary>文件名。</summary>
    public string FileName => _snapshot.FileName;

    /// <summary>远端完整路径(悬停提示)。</summary>
    public string RemotePath => _snapshot.RemotePath;

    /// <summary>本地临时副本路径(悬停提示 / 「打开所在目录」)。</summary>
    public string LocalPath => _snapshot.LocalPath;

    /// <summary>最近一次回传失败。</summary>
    public bool IsFailed => _snapshot.State == RemoteEditState.Failed;

    /// <summary>正在回传。</summary>
    public bool IsUploading => _snapshot.State == RemoteEditState.Uploading;

    /// <summary>有改动还没落到远端(自动上传关着,或刚失败)。</summary>
    public bool HasPendingChange => _snapshot.HasPendingChange;

    /// <summary>状态行:失败原因 / 正在上传 / 待上传。</summary>
    public string StatusLine => _snapshot switch
    {
        { State: RemoteEditState.Failed, LastError: { } error } => Strings.Format("Transfer_EditFailed", error),
        { State: RemoteEditState.Uploading } => Strings.Get("Transfer_EditUploading"),
        _ => Strings.Get("Transfer_EditPending"),
    };

    /// <summary>服务器名 + 远端路径,给悬停提示用。</summary>
    public string TargetLine => string.IsNullOrEmpty(_snapshot.ServerName)
                                    ? _snapshot.RemotePath
                                    : $"{_snapshot.ServerName}:{_snapshot.RemotePath}";

    /// <summary>换上新的快照并刷新绑定。必须在 UI 线程调用。</summary>
    /// <param name="snapshot">新的会话状态快照。</param>
    public void Apply(RemoteEditSnapshot snapshot)
    {
        _snapshot = snapshot;
        this.RaisePropertyChanged(nameof(FileName));
        this.RaisePropertyChanged(nameof(RemotePath));
        this.RaisePropertyChanged(nameof(LocalPath));
        this.RaisePropertyChanged(nameof(IsFailed));
        this.RaisePropertyChanged(nameof(IsUploading));
        this.RaisePropertyChanged(nameof(HasPendingChange));
        this.RaisePropertyChanged(nameof(StatusLine));
        this.RaisePropertyChanged(nameof(TargetLine));
    }
}
