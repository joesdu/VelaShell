using System.Collections.ObjectModel;
using ReactiveUI;
using VelaShell.Core.Models;

namespace VelaShell.Presentation.ViewModels;

public sealed class SessionTreeNodeViewModel(Guid id, string name, bool isGroup) : ReactiveObject
{
    private int _groupColorIndex;
    private SessionStatus _status = SessionStatus.Disconnected;

    public Guid Id { get; } = id;

    public bool IsGroup { get; } = isGroup;

    public string Name
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = name;

    public bool IsExpanded
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = isGroup;

    /// <summary>与 TreeViewItem 的选中态双向同步;选中行按设计 FrJPu 高亮(bg-active + accent 名称)。</summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 未分组会话直接挂在树根(设计 FrJPu:不再有“未分组”目录),此时行内缩进
    /// 与分组行对齐而不是子项缩进。
    /// </summary>
    public bool IsRootLevel
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 状态圆点与标签(设计 FrJPu):Connected→绿点+「活跃」,Connecting→黄点+
    /// 「连接中」,Error→红点+「离线」,Disconnected→红点、无标签。
    /// </summary>
    public SessionStatus Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref _status, value);
            this.RaisePropertyChanged(nameof(IsConnected));
            this.RaisePropertyChanged(nameof(IsConnecting));
            this.RaisePropertyChanged(nameof(IsError));
            this.RaisePropertyChanged(nameof(HasStatusTag));
            this.RaisePropertyChanged(nameof(StatusTagText));
        }
    }

    public bool IsConnected => _status == SessionStatus.Connected;

    public bool IsConnecting => _status == SessionStatus.Connecting;

    public bool IsError => _status == SessionStatus.Error;

    public bool HasStatusTag => _status != SessionStatus.Disconnected;

    public string StatusTagText => _status switch
    {
        SessionStatus.Connected => "活跃",
        SessionStatus.Connecting => "连接中",
        SessionStatus.Error => "离线",
        _ => string.Empty
    };

    /// <summary>
    /// 分组文件夹图标的配色序号(设计 FrJPu 按 warning/info/accent 轮换),
    /// 由树加载时按分组顺序赋值。
    /// </summary>
    public int GroupColorIndex
    {
        get => _groupColorIndex;
        set
        {
            if (_groupColorIndex == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref _groupColorIndex, value);
            this.RaisePropertyChanged(nameof(IsFolderInfo));
            this.RaisePropertyChanged(nameof(IsFolderAccent));
        }
    }

    public bool IsFolderInfo => _groupColorIndex == 1;

    public bool IsFolderAccent => _groupColorIndex == 2;

    public ObservableCollection<SessionTreeNodeViewModel> Children { get; } = [];
}
