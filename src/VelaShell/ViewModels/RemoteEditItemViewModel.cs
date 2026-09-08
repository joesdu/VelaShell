using ReactiveUI;
using VelaShell.Core.Resources;
using VelaShell.Services;

namespace VelaShell.ViewModels;

/// <summary>
/// 传输浮窗「正在编辑」分组里的一行:一个远程文件的本地编辑会话。
/// </summary>
/// <remarks>
/// 这一行本身就是这个功能的可见性。#396 之前,自动回传是完全隐形的:用户无从知道
/// 「这个文件到底有没有被盯着」「刚才那次保存传上去了没有」「失败了的话我的改动还在不在」——
/// 出问题时能提供的只有一句"没上传"。现在这三件事各占一列。
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

    /// <summary>
    /// 状态行:失败原因 / 正在上传 / 待上传 / 最近一次上传时刻 / 尚未回传过。
    /// </summary>
    public string StatusLine => _snapshot switch
    {
        { State: RemoteEditState.Failed, LastError: { } error } => Strings.Format("Transfer_EditFailed", error),
        { State: RemoteEditState.Uploading } => Strings.Get("Transfer_EditUploading"),
        { HasPendingChange: true } => Strings.Get("Transfer_EditPending"),
        { LastUploadedAt: { } at } => Strings.Format("Transfer_EditUploadedAt", at.ToString("HH:mm:ss")),
        _ => Strings.Get("Transfer_EditWatching"),
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
