using System.Diagnostics;
using System.Net.Sockets;
using VelaShell.Core.Data;
using VelaShell.Core.Resources;
using VelaShell.Core.XServer;
using VelaShell.Ssh.Transport;
using VelaShell.XServer.Server;
using AppXServerOptions = VelaShell.Core.Models.XServerOptions;
using LibXServerOptions = VelaShell.XServer.Host.XServerOptions;

namespace VelaShell.Infrastructure.XServer;

/// <summary>
/// <see cref="ILocalXServer" /> 的内置实现:在进程里跑 <see cref="X11Server" />(<c>src/VelaShell.XServer</c>),
/// 顶层窗口由 <see cref="IEmbeddedXServerHost" />(界面层)画成原生窗口。
/// </summary>
/// <remarks>
/// <para>
/// 不拉外部进程、不装任何东西,各平台都能用。监听环回上的 TCP <c>6000+N</c> 与(类 Unix 上)<c>/tmp/.X11-unix/XN</c>,
/// 本机别的 X 程序照样能用 <c>DISPLAY=localhost:N</c> 连进来;SSH 的 x11 通道则经
/// <see cref="XServerDisplayResolution.Connector" /> 直接接进服务端,不绕本机端口。
/// </para>
/// <para>
/// 状态变化(<see cref="StateChanged" />)在调用启动 / 停止的那个线程上触发,界面侧自己切回 UI 线程。
/// </para>
/// </remarks>
public sealed class BuiltInLocalXServer : ILocalXServer, IAsyncDisposable, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly Func<IEmbeddedXServerHost?> _host;
    private readonly Func<int, CancellationToken, Task<bool>> _isDisplayInUse;
    private readonly Func<CancellationToken, Task<bool>> _hasOtherDisplay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();

    private X11Server? _server;
    private IEmbeddedXServerHost? _attached;
    private int? _displayNumber;
    private XServerState _state = XServerState.Stopped;
    private bool _disposed;

    /// <summary>构造。</summary>
    /// <param name="settings">设置服务(显示号、剪贴板、自动启动)。</param>
    /// <param name="host">取宿主;<see langword="null" /> 表示界面层没有提供,启动会失败。</param>
    public BuiltInLocalXServer(ISettingsService settings, Func<IEmbeddedXServerHost?> host)
        : this(settings, host, XDisplayProbe.IsInUseAsync, HasOtherDisplayAsync)
    {
    }

    /// <summary>可注入显示探测(单测用)。</summary>
    internal BuiltInLocalXServer(
        ISettingsService settings,
        Func<IEmbeddedXServerHost?> host,
        Func<int, CancellationToken, Task<bool>> isDisplayInUse,
        Func<CancellationToken, Task<bool>> hasOtherDisplay)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _isDisplayInUse = isDisplayInUse;
        _hasOtherDisplay = hasOtherDisplay;
    }

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public XServerState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public int? DisplayNumber
    {
        get
        {
            lock (_stateLock)
            {
                return _state == XServerState.Running ? _displayNumber : null;
            }
        }
    }

    /// <inheritdoc />
    public string? Display => DisplayNumber is { } number ? XServerCommandLine.DisplayAddress(number) : null;

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <summary>内置引擎没有可执行文件可找。</summary>
    public string? FindExecutable(string? configuredPath) => null;

    /// <inheritdoc />
    public async Task<XServerStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State == XServerState.Running)
            {
                return XServerStartResult.Ok;
            }
            AppXServerOptions options = (await _settings.GetSnapshotAsync().ConfigureAwait(false)).XServer;
            return await StartCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<XServerStartResult> StartCoreAsync(AppXServerOptions options, CancellationToken cancellationToken)
    {
        if (_host() is not { } host)
        {
            return XServerStartResult.Fail(Strings.Get("XServer_ErrNoHost"));
        }

        int display;
        if (options.DisplayNumber >= 0)
        {
            display = options.DisplayNumber;
            if (await _isDisplayInUse(display, cancellationToken).ConfigureAwait(false))
            {
                return XServerStartResult.Fail(Strings.Format("XServer_ErrDisplayInUse", display));
            }
        }
        else if (await VcXsrvLocalXServer.SelectFreeDisplayAsync(_isDisplayInUse, cancellationToken).ConfigureAwait(false) is { } free)
        {
            display = free;
        }
        else
        {
            return XServerStartResult.Fail(Strings.Format("XServer_ErrNoFreeDisplay", AppXServerOptions.MaxDisplayNumber));
        }

        SetState(XServerState.Starting, display);
        X11Server server = new(new LibXServerOptions
        {
            DisplayNumber = display,
            SyncClipboard = options.Clipboard,
            SyncPrimary = options.Clipboard && options.CopyOnSelection,
            Log = static line => Trace.WriteLine($"[XServer] {line}"),
        }, host);
        try
        {
            // 先让宿主把显示器布局、DPI、键盘布局告诉服务端,再开门 —— 第一个客户端拿到的就是对的屏幕与键位表。
            host.UseKeyboardLayout(options.KeyboardLayout);
            await host.AttachAsync(server, cancellationToken).ConfigureAwait(false);
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or InvalidOperationException)
        {
            host.Detach();
            await server.DisposeAsync().ConfigureAwait(false);
            SetStopped();
            if (ex is OperationCanceledException)
            {
                throw;
            }
            return XServerStartResult.Fail(ex is SocketException
                ? Strings.Format("XServer_ErrDisplayInUse", display)
                : Strings.Format("XServer_ErrLaunch", ex.Message));
        }

        lock (_stateLock)
        {
            _server = server;
            _attached = host;
            _state = XServerState.Running;
        }
        Trace.WriteLine($"[XServer] built-in server listening on :{display}");
        RaiseStateChanged();
        return XServerStartResult.Ok;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        X11Server? server;
        IEmbeddedXServerHost? host;
        lock (_stateLock)
        {
            server = _server;
            host = _attached;
            _server = null;
            _attached = null;
        }
        if (server is null)
        {
            return;
        }
        host?.Detach();
        await server.DisposeAsync().ConfigureAwait(false);
        SetStopped();
    }

    /// <inheritdoc />
    public async Task<XServerDisplayResolution> ResolveForwardingDisplayAsync(CancellationToken cancellationToken = default)
    {
        if (Current() is { } running)
        {
            return running;
        }

        AppXServerOptions options = (await _settings.GetSnapshotAsync().ConfigureAwait(false)).XServer;
        if (!options.AutoStartForX11Forwarding || await _hasOtherDisplay(cancellationToken).ConfigureAwait(false))
        {
            return XServerDisplayResolution.None;
        }

        XServerStartResult result = await StartAsync(cancellationToken).ConfigureAwait(false);
        return result.Success && Current() is { } started ? started : new(Display: null, result.Error);
    }

    /// <summary>在运行时给出显示地址与连接器。</summary>
    private XServerDisplayResolution? Current()
    {
        X11Server? server;
        int? display;
        lock (_stateLock)
        {
            server = _state == XServerState.Running ? _server : null;
            display = _displayNumber;
        }
        return server is null || display is null
            ? null
            : new(XServerCommandLine.DisplayAddress(display.Value), Connector: ct => ConnectAsync(server, ct));
    }

    /// <summary>一条直接接进服务端的双工流:一端交给服务端的 <see cref="X11Server.ServeAsync" />,另一端交给 SSH。</summary>
    private static ValueTask<Stream> ConnectAsync(X11Server server, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream clientSide) = InMemoryTransport.CreatePair();
        _ = ServeAsync(server, serverSide);
        return ValueTask.FromResult<Stream>(clientSide);
    }

    private static async Task ServeAsync(X11Server server, InMemoryDuplexStream stream)
    {
        try
        {
            // ServeAsync 不拥有流:连接结束(客户端断开、服务端停下)后在这里释放,SSH 那一端随之读到 EOF。
            await server.ServeAsync(stream, isLocal: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or OperationCanceledException)
        {
            // 服务端已停,或连接中途断了。
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>本机是否已经有别的 X 显示在用:Windows 上看 <c>localhost:0</c>,其它平台看 <c>DISPLAY</c>。</summary>
    private static async Task<bool> HasOtherDisplayAsync(CancellationToken cancellationToken) =>
        OperatingSystem.IsWindows()
            ? await XDisplayProbe.IsTcpListeningAsync(0, cancellationToken).ConfigureAwait(false)
            : !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));

    private void SetState(XServerState state, int display)
    {
        lock (_stateLock)
        {
            _state = state;
            _displayNumber = display;
        }
        RaiseStateChanged();
    }

    private void SetStopped()
    {
        lock (_stateLock)
        {
            _state = XServerState.Stopped;
            _displayNumber = null;
        }
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[XServer] StateChanged handler failed: {ex}");
        }
    }

    /// <summary>退出时停掉服务端、关掉它的窗口。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>同步释放(容器按同步方式收尾时):最多等 3 秒让服务端收工。</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _ = DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
    }
}
