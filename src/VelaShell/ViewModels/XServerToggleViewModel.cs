using System.Collections.ObjectModel;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.XServer;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.ViewModels;

/// <summary>
/// 标题栏的 X Server 按钮:没在运行时一点就开;运行中图标转强调色,悬停提示写出当前显示地址。内置引擎运行中时一点开出浮层
/// (规范 §4A.2):连着的 X 程序 —— 叫什么、来自哪个会话、几个窗口、占多少内存 —— 能逐个断开、能「解除卡住」、能停掉整个
/// X Server;外部引擎(VcXsrv)列不出程序,照旧一点开、一点关。某个程序独占 X Server 太久时发一条带「断开它」的提示。
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
    private CancellationTokenSource? _refresh;

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
        IObservable<bool> notStarting = this.WhenAnyValue(x => x.IsStarting, starting => !starting);
        ToggleCommand = ReactiveCommand.CreateFromTask(ToggleAsync, notStarting);
        ButtonCommand = ReactiveCommand.CreateFromTask(PressAsync, notStarting);
        ClosePanelCommand = ReactiveCommand.Create(() => { IsPanelOpen = false; });
        StopCommand = ReactiveCommand.CreateFromTask(StopFromPanelAsync);
        BreakGrabsCommand = ReactiveCommand.CreateFromTask(BreakGrabsAsync);
        DisconnectCommand = ReactiveCommand.CreateFromTask<XServerClientItemViewModel>(DisconnectAsync);
        _server?.StateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _server?.ServerGrabStalled += (_, notice) => Dispatcher.UIThread.Post(() => ShowGrabStall(notice));
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

    /// <summary>悬停提示:没运行时说「启动」;运行中带上显示地址,内置引擎说「点开看连着的程序」,VcXsrv 说「点击停止」。</summary>
    public string ToolTip
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>开 / 关(命令面板的 <c>tools.xserver</c>)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleCommand { get; }

    /// <summary>
    /// 标题栏按钮:内置引擎运行中时开 / 收浮层,其余时候同 <see cref="ToggleCommand" />(没在运行就启动;VcXsrv 运行中就停)。
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> ButtonCommand { get; }

    /// <summary>引擎列得出连着的 X 程序(内置引擎):标题栏按钮运行中时开浮层。</summary>
    public bool CanManageClients => _server?.CanManageClients == true;

    /// <summary>浮层开着(内置引擎运行中才开得出来;停了随即收起)。开着时每隔 <see cref="RefreshInterval" /> 刷新一次程序清单。</summary>
    public bool IsPanelOpen
    {
        get;
        set
        {
            bool open = value && IsRunning && CanManageClients;
            if (field == open)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, open);
            _refresh?.Cancel();
            _refresh = null;
            if (open)
            {
                _refresh = new CancellationTokenSource();
                _ = RefreshLoopAsync(_refresh.Token);
            }
        }
    }

    /// <summary>浮层开着时刷新程序清单的间隔(内存、窗口数会变,程序会来会走);测试可以调。</summary>
    internal TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>浮层里的程序清单(服务端的顺序:先连上的在前)。</summary>
    public ObservableCollection<XServerClientItemViewModel> Clients { get; } = [];

    /// <summary>清单不空(空的时候浮层写「还没有 X 程序连着」)。</summary>
    public bool HasClients
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>浮层标题旁的显示地址(<c>localhost:0.0</c>);没在运行时为空。</summary>
    public string Display
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>收起浮层。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ClosePanelCommand { get; }

    /// <summary>浮层里的「停止 X Server」:与 <see cref="ToggleCommand" /> 停服同一条路(有程序连着先确认)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> StopCommand { get; }

    /// <summary>浮层里的「解除卡住」:解除所有 X 程序的抓取与独占(<see cref="ILocalXServer.BreakGrabs" />)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> BreakGrabsCommand { get; }

    /// <summary>断开清单里的一个程序(先确认,见 <see cref="ConfirmDisconnectAsync" />)。</summary>
    public ReactiveCommand<XServerClientItemViewModel, RxVoid> DisconnectCommand { get; }

    /// <summary>
    /// 停之前请用户确认(参数是会断开的 X 程序连接数,只在大于 0 时问);返回 false 就不停。确认框只有视图层拿得到,由它注入;
    /// 没注入时直接停。原先一点就停,所有会话的 X 程序一起断开,也不说会断几个。
    /// </summary>
    public Func<int, Task<bool>>? ConfirmStopAsync { get; set; }

    /// <summary>
    /// 断开一个程序之前请用户确认(参数是程序名);返回 false 就不断。确认框只有视图层拿得到,由它注入;没注入时直接断。
    /// </summary>
    public Func<string, Task<bool>>? ConfirmDisconnectAsync { get; set; }

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

    /// <summary>停掉(没在运行时什么也不做)。主窗口真正关闭时调:X 窗口随之收掉,应用才退得出去。</summary>
    public Task StopAsync() => _server is { State: XServerState.Running } server ? server.StopAsync() : Task.CompletedTask;

    /// <summary>按服务端此刻的样子刷新程序清单:按键就地更新、补上新来的、去掉走了的(不整个重建,悬停时冒出来的按钮不会一闪就没)。</summary>
    public async Task RefreshClientsAsync()
    {
        IReadOnlyList<XServerClient> clients = _server is { CanManageClients: true } server && IsRunning
            ? await server.GetClientsAsync()
            : [];
        HashSet<string> keys = [.. clients.Select(c => c.Key)];
        for (int i = Clients.Count - 1; i >= 0; i--)
        {
            if (!keys.Contains(Clients[i].Key))
            {
                Clients.RemoveAt(i);
            }
        }
        for (int i = 0; i < clients.Count; i++)
        {
            int at = IndexOf(clients[i].Key);
            if (at < 0)
            {
                Clients.Insert(i, new XServerClientItemViewModel(clients[i]));
                continue;
            }
            Clients[at].Update(clients[i]);
            if (at != i)
            {
                Clients.Move(at, i);
            }
        }
        HasClients = Clients.Count > 0;

        int IndexOf(string key)
        {
            for (int i = 0; i < Clients.Count; i++)
            {
                if (Clients[i].Key == key)
                {
                    return i;
                }
            }
            return -1;
        }
    }

    /// <summary>
    /// 某个程序独占 X Server 太久(远端进程挂住、SSH 断网而连接没断时,所有 X 程序都冻着):发一条警告,带「断开它」。
    /// 同一个程序再报一次时就地更新那一条(秒数变了),不再堆一条。
    /// </summary>
    internal void ShowGrabStall(XServerGrabStallNotice notice)
    {
        if (_server is not { } server)
        {
            return;
        }
        string name = notice.Name.Length > 0 ? notice.Name : Strings.Format("XServer_PanelUnnamed", notice.Id);
        string message = Strings.Format("XServer_GrabStalled", name, notice.Source ?? Strings.Get("XServer_PanelLocal"),
            (int)notice.Held.TotalSeconds);
        _toasts.Show(new ToastViewModel(ToastSeverity.Warning, message, Strings.Get("XServer_GrabStalledDisconnect"),
            () => server.DisconnectClient(notice.ClientKey))
        {
            MergeKey = $"xserver-grab-stall:{notice.ClientKey}",
        });
    }

    /// <summary>换语言后重算悬停提示与清单里拼好的文字(它们是 C# 侧拼好存着的,不随 {loc:Localize} 刷新)。</summary>
    public void RefreshLocalizedText()
    {
        Refresh();
        if (IsPanelOpen)
        {
            _ = RefreshClientsAsync();
        }
    }

    private async Task PressAsync()
    {
        if (CanManageClients && _server?.State == XServerState.Running)
        {
            IsPanelOpen = !IsPanelOpen;
            return;
        }
        await ToggleAsync();
    }

    private async Task ToggleAsync()
    {
        if (_server is null)
        {
            return;
        }
        if (_server.State == XServerState.Running)
        {
            if (ConfirmStopAsync is { } confirm && await _server.CountConnectedClientsAsync() is > 0 and var clients
                && !await confirm(clients))
            {
                return;
            }
            await _server.StopAsync();
            Refresh();
            return;
        }
        await StartAsync();
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshClientsAsync();
                await Task.Delay(RefreshInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 浮层收起了
        }
    }

    private async Task StopFromPanelAsync()
    {
        if (_server?.State == XServerState.Running)
        {
            await ToggleAsync();
        }
        if (_server?.State != XServerState.Running)
        {
            IsPanelOpen = false;
        }
    }

    private async Task BreakGrabsAsync()
    {
        _server?.BreakGrabs();
        _toasts.Info(Strings.Get("XServer_BreakGrabsDone"));
        await RefreshClientsAsync();
    }

    private async Task DisconnectAsync(XServerClientItemViewModel client)
    {
        if (_server is null || (ConfirmDisconnectAsync is { } confirm && !await confirm(client.Name)))
        {
            return;
        }
        _server.DisconnectClient(client.Key);
        await RefreshClientsAsync();
    }

    private void Refresh()
    {
        IsRunning = _server?.State == XServerState.Running;
        Display = IsRunning ? _server?.Display ?? string.Empty : string.Empty;
        ToolTip = IsStarting
            ? Strings.Get("XServer_TipStarting")
            : IsRunning && _server?.Display is { } display
                ? Strings.Format(CanManageClients ? "XServer_TipPanel" : "XServer_TipStop", display)
                : Strings.Get("XServer_TipStart");
        if (!IsRunning)
        {
            IsPanelOpen = false;
            Clients.Clear();
            HasClients = false;
        }
    }
}
