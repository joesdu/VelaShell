using System.Collections.ObjectModel;
using PulseTerm.Core.Models;
using ReactiveUI;

namespace PulseTerm.Presentation.ViewModels;

public sealed class SessionTreeNodeViewModel : ReactiveObject
{
    private string _name;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isRootLevel;
    private SessionStatus _status;
    private int _groupColorIndex;

    public SessionTreeNodeViewModel(Guid id, string name, bool isGroup)
    {
        Id = id;
        _name = name;
        IsGroup = isGroup;
        _isExpanded = isGroup;
        _status = SessionStatus.Disconnected;
        Children = new ObservableCollection<SessionTreeNodeViewModel>();
    }

    public Guid Id { get; }

    public bool IsGroup { get; }

    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    /// <summary>与 TreeViewItem 的选中态双向同步;选中行按设计 FrJPu 高亮(bg-active + accent 名称)。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    /// <summary>未分组会话直接挂在树根(设计 FrJPu:不再有“未分组”目录),此时行内缩进
    /// 与分组行对齐而不是子项缩进。</summary>
    public bool IsRootLevel
    {
        get => _isRootLevel;
        set => this.RaiseAndSetIfChanged(ref _isRootLevel, value);
    }

    /// <summary>状态圆点与标签(设计 FrJPu):Connected→绿点+「活跃」,Connecting→黄点+
    /// 「连接中」,Error→红点+「离线」,Disconnected→红点、无标签。</summary>
    public SessionStatus Status
    {
        get => _status;
        set
        {
            if (_status == value)
                return;

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
        _ => string.Empty,
    };

    /// <summary>分组文件夹图标的配色序号(设计 FrJPu 按 warning/info/accent 轮换),
    /// 由树加载时按分组顺序赋值。</summary>
    public int GroupColorIndex
    {
        get => _groupColorIndex;
        set
        {
            if (_groupColorIndex == value)
                return;

            this.RaiseAndSetIfChanged(ref _groupColorIndex, value);
            this.RaisePropertyChanged(nameof(IsFolderInfo));
            this.RaisePropertyChanged(nameof(IsFolderAccent));
        }
    }

    public bool IsFolderInfo => _groupColorIndex == 1;

    public bool IsFolderAccent => _groupColorIndex == 2;

    public ObservableCollection<SessionTreeNodeViewModel> Children { get; }
}
