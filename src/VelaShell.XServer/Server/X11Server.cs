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
    /// <summary>对宿主的回调都经它排队,执行线程放锁之后再调(见 RunLoopAsync)。</summary>
    private readonly IXServerHost _host;
    private readonly DeferredHost _deferredHost;

    /// <summary>经 TCP / Unix 套接字接进来的连接(收工时等它们结束)。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _connections = new();
    private readonly Channel<WorkItem> _work = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<uint, XResource> _resources = [];
    private readonly Dictionary<int, XClient> _clients = [];
    private readonly FontCatalog _fonts = new();
    private readonly Keymap _keymap = new();
    private readonly Dictionary<XWindow, List<XRect>> _damage = [];
    private readonly Dictionary<XWindow, XTopLevelWindow> _topLevelHandles = [];
    private readonly List<WorkItem> _deferred = [];
    /// <summary>像素锁与宿主的让行计数(见 <see cref="PixelGate" />)。</summary>
    private readonly PixelGate _pixelGate = new();

    private readonly Task _loopTask;
    private TcpListener? _listener;
    private Task? _acceptTask;
    private XClient? _serverGrabber;
    private int _disposed;

    /// <summary>用选项与宿主构造;宿主为 null 时不显示任何东西(无头,测试与诊断用)。</summary>
    public X11Server(XServerOptions? options = null, IXServerHost? host = null)
    {
        _options = options ?? new XServerOptions();
        _deferredHost = new DeferredHost(host ?? NullHost.Instance);
        _host = _deferredHost;
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

    /// <summary>执行一批工作项期间持有的锁;宿主读像素时也拿它(<see cref="XTopLevelWindow.ReadPixels" /> 已经拿了)。</summary>
    public object PixelLock => _pixelGate.Lock;

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
            TrackConnection(ServeAndDisposeAsync(tcp, local, cancellationToken));
        }
    }

    private async Task ServeAndDisposeAsync(TcpClient tcp, bool local, CancellationToken cancellationToken)
    {
        using (tcp)
        {
            try
            {
                await ServeAsync(tcp.GetStream(), local, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // 服务端正在收工。
            }
        }
    }

    /// <summary>登记一个接进来的连接任务,结束时自动摘掉。</summary>
    private void TrackConnection(Task connection)
    {
        _connections.TryAdd(connection, 0);
        _ = connection.ContinueWith(t => _connections.TryRemove(t, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ 执行循环

    /// <summary>一项工作:客户端的一条请求(<see cref="Request" />),或者一段要在执行线程上跑的代码。</summary>
    private readonly record struct WorkItem(XClient? Client, Action? Action, byte[]? Request = null);

    /// <summary>把一件事排进执行线程。可以在任意线程上调。</summary>
    internal void Post(XClient? client, Action action) => _work.Writer.TryWrite(new WorkItem(client, action));

    /// <summary>把客户端的一条请求排进执行线程(不为每条请求分配闭包)。</summary>
    private void PostRequest(XClient client, byte[] request) => _work.Writer.TryWrite(new WorkItem(client, null, request));

    /// <summary>执行线程一次持锁最多跑这么久,然后放锁让宿主读像素(宿主的 UI 线程在 CopyPixels 里等这把锁)。</summary>
    private static readonly long LockBudgetTicks = Stopwatch.Frequency / 250;   // 4 毫秒

    /// <summary>排进执行线程并等它做完(连接建立等少数需要结果的地方用)。</summary>
    internal Task<T> InvokeAsync<T>(Func<T> func)
    {
        TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = _work.Writer.TryWrite(new WorkItem(null, () =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));
        if (!queued)
        {
            tcs.TrySetCanceled();   // 执行循环已经收工
        }
        return tcs.Task;
    }

    private async Task RunLoopAsync()
    {
        ChannelReader<WorkItem> reader = _work.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_lifetime.Token).ConfigureAwait(false))
            {
                // lock 不公平:刚放锁就再拿,等着读像素的宿主线程可能一直抢不到。宿主在等就先让它读完。
                _pixelGate.YieldToHost();
                lock (PixelLock)
                {
                    long deadline = Stopwatch.GetTimestamp() + LockBudgetTicks;
                    while (reader.TryRead(out WorkItem item))
                    {
                        RunItem(item);
                        if (Stopwatch.GetTimestamp() >= deadline || _pixelGate.HostWaiting)
                        {
                            break;
                        }
                    }
                }
                // 宿主回调一律在放锁之后调:回调里同步等 UI 线程、而 UI 线程正在 CopyPixels 里等这把锁,就是死锁。
                FlushDamage();
                _deferredHost.Flush();
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
            if (item.Request is { } request)
            {
                ExecuteRequest(item.Client!, request);
            }
            else
            {
                item.Action!();
            }
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
            client.Abort();
        }
        DetachShmSegments(_resources.Values);
        try
        {
            // 接进来的连接随 _lifetime 取消而收工;给它们一点时间关掉套接字。
            await Task.WhenAll(_connections.Keys).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 收工阶段的异常不关心。
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
