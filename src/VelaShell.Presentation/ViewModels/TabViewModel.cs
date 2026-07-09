using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using ReactiveUI;

namespace VelaShell.Presentation.ViewModels;

public class TabViewModel : ReactiveObject
{
    private string _title;
    private SessionStatus _connectionStatus;
    private bool _isActive;
    private bool _hasBellAlert;

    public TabViewModel()
    {
        _title = Strings.NewTab;
        _connectionStatus = SessionStatus.Disconnected;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    public SessionStatus ConnectionStatus
    {
        get => _connectionStatus;
        set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => this.RaiseAndSetIfChanged(ref _isActive, value);
    }

    /// <summary>后台标签收到 BEL 时点亮的提醒标记(设置 → 终端 → 标签闪烁提醒);
    /// 切换到该标签时由宿主清除。</summary>
    public bool HasBellAlert
    {
        get => _hasBellAlert;
        set => this.RaiseAndSetIfChanged(ref _hasBellAlert, value);
    }
}
