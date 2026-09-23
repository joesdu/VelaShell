// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 1 节「Protocol Formats」(请求按序执行)、
//   「GrabServer」(独占期间不处理其他连接的请求)
//   架构:velashell-docs/zh/xserver/design/architecture.md §5(线程模型)

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using VelaShell.XServer.Drawing;
using VelaShell.XServer.Fonts;
using VelaShell.XServer.Host;
using VelaShell.XServer.Input;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

/// <summary>
/// 一个 X11 服务端。
/// </summary>
/// <remarks>
/// <para>
/// <b>一个执行线程</b>执行全部客户端请求、宿主注入的输入与收尾工作 —— X 的语义是全局串行的,
/// 所有可变状态只在这个线程上被碰,因此不加锁(架构 §5)。唯一的例外是像素:宿主在别的线程上读
/// 顶层窗口的像素,执行线程在执行一批工作项期间持有 <see cref="PixelLock" />。
/// </para>
/// <para>
/// 连接可以来自 TCP(<see cref="StartAsync" />,端口 6000+N),也可以直接交一条流进来
/// (<see cref="ServeAsync" />)—— 后者让宿主不必经本机端口:SSH 的 x11 通道本身就是一条双工流。
/// </para>
/// </remarks>
public sealed partial class X11Server : IAsyncDisposable
{
    internal const uint RootWindowId = 0x00000100;
    internal const uint DefaultColormapId = 0x00000020;
    internal const uint RootVisualId = 0x00000021;
    internal const uint ArgbVisualId = 0x00000022;

    private readonly XServerOptions _options;
    private readonly IXServerHost _host;
    private readonly Channel<WorkItem> _work = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<uint, XResource> _resources = [];
    private readonly Dictionary<int, XClient> _clients = [];
    private readonly FontCatalog _fonts = new();
    private readonly Keymap _keymap = new();
    private readonly Dictionary<XWindow, Region> _damage = [];
    private readonly Dictionary<XWindow, XTopLevelWindow> _topLevelHandles = [];
    private readonly List<WorkItem> _deferred = [];

    private readonly Task _loopTask;
    private TcpListener? _listener;
    private Task? _acceptTask;
    private XClient? _serverGrabber;
    private int _disposed;

    /// <summary>用选项与宿主构造;宿主为 null 时不显示任何东西(无头,测试与诊断用)。</summary>
    public X11Server(XServerOptions? options = null, IXServerHost? host = null)
    {
        _options = options ?? new XServerOptions();
        _host = host ?? NullHost.Instance;
        Root = CreateRootWindow();
        _resources[Root.Id] = Root;
        InitMonitors();
        RebuildRandRModes();
        _resources[DefaultColormapId] = new XColormap(DefaultColormapId, null, RootVisualId);
        InitAtoms();
        InitExtensions();
        InitXSettings();
        InitSyncCounters();
        InitXkbRulesNames();
        InitEwmh();
        _pointerWindow = Root;
        _focus = Root;   // 初始焦点是 PointerRoot(与 X.Org 一致;窗口管理器 —— 这里是宿主 —— 之后再把焦点给具体的顶层)
        _loopTask = Task.Run(RunLoopAsync);
    }

    /// <summary>执行一批工作项期间持有的锁;宿主读像素时也拿它(<see cref="XTopLevelWindow.CopyPixels" /> 已经拿了)。</summary>
    public object PixelLock { get; } = new();

    /// <summary>TCP 监听的端口(6000 + 显示号);没监听时为 0。</summary>
    public int Port { get; private set; }

    /// <summary>给 X 客户端用的显示地址(形如 <c>localhost:0.0</c>)。</summary>
    public string Display => $"localhost:{_options.DisplayNumber}.0";

    internal XWindow Root { get; }

    internal XServerOptions Options => _options;

    /// <summary>服务端时间(毫秒,32 位回绕)—— 事件里的 time 字段。</summary>
    internal uint Now => unchecked((uint)_clock.ElapsedMilliseconds);

    /// <summary>
    /// 开始监听:TCP 6000+N(<see cref="XServerOptions.ListenTcp" />)与 Unix 套接字(<see cref="XServerOptions.UnixSocketPath" />)。
    /// TCP 端口被占用时抛 <see cref="SocketException" />;Unix 套接字建不起来只记日志。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.ListenTcp)
        {
            TcpListener listener = new(_options.ListenAddress, 6000 + _options.DisplayNumber);
            listener.Start();
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _acceptTask = AcceptLoopAsync(listener, _lifetime.Token);
        }
        StartUnixListeners(_lifetime.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            tcp.NoDelay = true;
            bool local = tcp.Client.RemoteEndPoint is IPEndPoint { Address: var address } && IPAddress.IsLoopback(address);
            _ = ServeAndDisposeAsync(tcp, local, cancellationToken);
        }
    }

    private async Task ServeAndDisposeAsync(TcpClient tcp, bool local, CancellationToken cancellationToken)
    {
        using (tcp)
        {
            await ServeAsync(tcp.GetStream(), local, cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ 执行循环

    private readonly record struct WorkItem(XClient? Client, Action Action);

    /// <summary>把一件事排进执行线程。可以在任意线程上调。</summary>
    internal void Post(XClient? client, Action action) => _work.Writer.TryWrite(new WorkItem(client, action));

    /// <summary>排进执行线程并等它做完(连接建立等少数需要结果的地方用)。</summary>
    internal Task<T> InvokeAsync<T>(Func<T> func)
    {
        TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(null, () =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private async Task RunLoopAsync()
    {
        ChannelReader<WorkItem> reader = _work.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_lifetime.Token).ConfigureAwait(false))
            {
                lock (PixelLock)
                {
                    int budget = 256;   // 一批最多这么多项,然后放锁让宿主读像素
                    while (budget-- > 0 && reader.TryRead(out WorkItem item))
                    {
                        RunItem(item);
                    }
                }
                FlushDamage();
            }
        }
        catch (OperationCanceledException)
        {
            // 收工。
        }
    }

    private void RunItem(WorkItem item)
    {
        // SYNC 的 Await 期间,这个客户端之后的请求暂存,条件成立时放回。
        if (_syncWaits.Count != 0 && DeferIfWaiting(item))
        {
            return;
        }
        // GrabServer 期间,别人的请求原样暂存,Ungrab 后按原顺序放回(协议「GrabServer」)。
        if (_serverGrabber is { } grabber && item.Client is { } client && !ReferenceEquals(client, grabber) && !client.Closed)
        {
            _deferred.Add(item);
            return;
        }
        try
        {
            item.Action();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[X11Server] work item failed: {ex}");
        }
    }

    /// <summary>GrabServer 结束(或持有者断开):把暂存的请求按原顺序重新排进去。</summary>
    private void ReleaseServerGrab()
    {
        _serverGrabber = null;
        List<WorkItem> pending = [.. _deferred];
        _deferred.Clear();
        foreach (WorkItem item in pending)
        {
            RunItem(item);
        }
    }

    // ------------------------------------------------------------------ 资源

    internal T? Lookup<T>(uint id) where T : XResource =>
        _resources.TryGetValue(id, out XResource? r) ? r as T : null;

    internal void AddResource(XClient client, XResource resource)
    {
        if (!client.OwnsId(resource.Id) || _resources.ContainsKey(resource.Id))
        {
            throw new Protocol.XProtocolError(Protocol.XErrorCode.IDChoice, resource.Id);
        }
        _resources[resource.Id] = resource;
    }

    internal void RemoveResource(uint id) => _resources.Remove(id);

    private XWindow CreateRootWindow() => new(RootWindowId, null, null)
    {
        Width = _options.ScreenWidth,
        Height = _options.ScreenHeight,
        Depth = 24,
        Visual = RootVisualId,
        Colormap = DefaultColormapId,
        BackgroundPixel = 0,
        Mapped = true,
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _listener?.Stop();
        StopUnixListeners();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _work.Writer.TryComplete();
        foreach (Task? task in (Task?[])[_acceptTask, _loopTask])
        {
            if (task is null)
            {
                continue;
            }
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 收工。
            }
        }
        foreach (XClient client in _clients.Values)
        {
            client.Closed = true;
            client.Output.Writer.TryComplete();
        }
        _lifetime.Dispose();
    }

    /// <summary>没有宿主时的空实现。</summary>
    private sealed class NullHost : IXServerHost
    {
        public static readonly NullHost Instance = new();

        public void TopLevelMapped(XTopLevelWindow window)
        {
        }

        public void TopLevelUnmapped(XTopLevelWindow window)
        {
        }

        public void TopLevelChanged(XTopLevelWindow window)
        {
        }

        public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage)
        {
        }

        public void CursorChanged(XTopLevelWindow? window, int cursorGlyph)
        {
        }

        public void Bell(int percent)
        {
        }

        public void ClipboardChanged(string text)
        {
        }
    }
}
