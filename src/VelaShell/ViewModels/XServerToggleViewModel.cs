using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.XServer;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.ViewModels;

/// <summary>
/// 标题栏的 X Server 按钮:一点开、再点关;运行中图标转强调色,悬停提示写出当前显示地址。
/// </summary>
/// <remarks>
/// <para>
/// 服务的 <see cref="ILocalXServer.StateChanged" /> 可能在线程池上响(VcXsrv 自己退出时、内置服务端在后台启停时),
/// 这里统一切回 UI 线程再改绑定属性。内置引擎各平台都有,所以按钮在各平台都出现。
/// </para>
/// <para>
/// 失败走错误提示,附一个「去设置」按钮,直接落到 X Server 页 —— 显示号被占、找不到 VcXsrv 一类的问题在那里改。
/// </para>
/// </remarks>
public sealed class XServerToggleViewModel : ReactiveObject
{
    private readonly ILocalXServer? _server;
    private readonly ToastHostViewModel _toasts;
    private readonly Action? _openSettings;

    /// <summary>构造。</summary>
    /// <param name="server">本机 X Server 服务;<see langword="null" /> = 不可用(按钮隐藏)。</param>
    /// <param name="toasts">失败时发提示用。</param>
    /// <param name="openSettings">「去设置」按钮的动作:打开设置并落到 X Server 页。</param>
    public XServerToggleViewModel(ILocalXServer? server, ToastHostViewModel toasts, Action? openSettings = null)
    {
        _server = server;
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _openSettings = openSettings;
        IsSupported = server?.IsSupported ?? false;
        ToggleCommand = ReactiveCommand.CreateFromTask(ToggleAsync, this.WhenAnyValue(x => x.IsStarting, starting => !starting));
        _server?.StateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    /// <summary>当前平台能不能用(不能用时标题栏按钮隐藏)。</summary>
    public bool IsSupported { get; }

    /// <summary>在运行。</summary>
    public bool IsRunning
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>正在启动(按钮暂时禁用,免得连点拉起两个)。</summary>
    public bool IsStarting
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>悬停提示:没运行时说「启动」,运行中说「停止」并带上显示地址。</summary>
    public string ToolTip
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>开 / 关。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleCommand { get; }

    /// <summary>启动(已在运行则什么都不做);失败发错误提示。启动时自动打开(设置里那一项)也走这里。</summary>
    public async Task StartAsync()
    {
        if (_server is not { IsSupported: true })
        {
            return;
        }
        IsStarting = true;
        Refresh();
        XServerStartResult result;
        try
        {
            result = await _server.StartAsync();
        }
        finally
        {
            IsStarting = false;
            Refresh();
        }

        if (result.Success)
        {
            if (_server.Display is { } display)
            {
                _toasts.Info(Strings.Format("XServer_Started", display));
            }
            return;
        }

        string message = Strings.Format("XServer_StartFailed", result.Error ?? string.Empty);
        if (_openSettings is not null)
        {
            _toasts.Error(message, Strings.Get("XServer_OpenSettings"), _openSettings);
        }
        else
        {
            _toasts.Error(message);
        }
    }

    private async Task ToggleAsync()
    {
        if (_server is null)
        {
            return;
        }
        if (_server.State == XServerState.Running)
        {
            await _server.StopAsync();
            Refresh();
            return;
        }
        await StartAsync();
    }

    private void Refresh()
    {
        IsRunning = _server?.State == XServerState.Running;
        ToolTip = IsStarting
            ? Strings.Get("XServer_TipStarting")
            : IsRunning && _server?.Display is { } display
                ? Strings.Format("XServer_TipStop", display)
                : Strings.Get("XServer_TipStart");
    }
}
