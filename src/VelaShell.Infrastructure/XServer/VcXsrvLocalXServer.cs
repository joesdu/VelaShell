using System.Diagnostics;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.XServer;
using VelaShell.Infrastructure.Diagnostics;

namespace VelaShell.Infrastructure.XServer;

/// <summary>
/// <see cref="ILocalXServer" /> 的 VcXsrv 实现:拉起用户装好的 VcXsrv,管到它退出为止。
/// </summary>
/// <remarks>
/// <para>
/// <b>只管自己拉起的那一个进程。</b>用户在外面另开的 VcXsrv / X410 不碰:自动选显示号时它们占着的号会被跳过,
/// SSH 自动启动时发现 6000 端口已有 X 服务端就不插手(见 <see cref="ResolveForwardingDisplayAsync" />)。
/// </para>
/// <para>
/// <b>「在运行」以端口为准,不以进程为准。</b>进程起来到开始监听之间有一段(首次运行要建字体缓存,
/// 能到好几秒),这段时间里把显示交给 SSH 转发,第一个 X 客户端就会连个空。所以启动要等到
/// <c>6000+N</c> 真能连上才算数。
/// </para>
/// <para>
/// 退出时由容器释放(<see cref="Dispose" />)把进程杀掉 —— 它是 VelaShell 拉起的,不该比 VelaShell 活得久。
/// </para>
/// </remarks>
public sealed class VcXsrvLocalXServer : ILocalXServer, IDisposable
{
    /// <summary>等 VcXsrv 开始监听的上限。首次运行要建字体缓存,给宽一点。</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

    private readonly ISettingsService _settings;
    private readonly Func<int, CancellationToken, Task<bool>> _isDisplayInUse;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();

    private Process? _process;
    private XServerState _state = XServerState.Stopped;
    private bool _disposed;

    /// <summary>用设置服务构造。</summary>
    public VcXsrvLocalXServer(ISettingsService settings)
        : this(settings, XDisplayProbe.IsTcpListeningAsync)
    {
    }

    /// <summary>可注入端口探测(单测用)。</summary>
    internal VcXsrvLocalXServer(ISettingsService settings, Func<int, CancellationToken, Task<bool>> isDisplayInUse)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _isDisplayInUse = isDisplayInUse;
    }

    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsWindows();

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
                return _state == XServerState.Running ? field : null;
            }
        }

        private set;
    }

    /// <inheritdoc />
    public string? Display => DisplayNumber is { } number ? XServerCommandLine.DisplayAddress(number) : null;

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public string? FindExecutable(string? configuredPath) =>
        IsSupported ? VcXsrvLocator.Find(configuredPath) : null;

    /// <inheritdoc />
    public async Task<XServerStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return XServerStartResult.Fail(Strings.Get("XServer_ErrUnsupported"));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State == XServerState.Running)
            {
                return XServerStartResult.Ok;
            }

            XServerOptions options = (await _settings.GetSnapshotAsync().ConfigureAwait(false)).XServer;
            return await StartCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<XServerStartResult> StartCoreAsync(XServerOptions options, CancellationToken cancellationToken)
    {
        if (VcXsrvLocator.Find(options.ExecutablePath) is not { } executable)
        {
            return XServerStartResult.Fail(string.IsNullOrWhiteSpace(options.ExecutablePath)
                ? Strings.Get("XServer_ErrNotFound")
                : Strings.Format("XServer_ErrConfiguredNotFound", options.ExecutablePath.Trim()));
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
        else if (await SelectFreeDisplayAsync(_isDisplayInUse, cancellationToken).ConfigureAwait(false) is { } free)
        {
            display = free;
        }
        else
        {
            return XServerStartResult.Fail(Strings.Format("XServer_ErrNoFreeDisplay", XServerOptions.MaxDisplayNumber));
        }

        string logFile = Path.Combine(DiagnosticLog.Directory ?? Path.GetTempPath(), "vcxsrv.log");
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            // VcXsrv 按自己所在目录找 fonts / xkbdata,工作目录放那里最稳。
            WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty,
        };
        foreach (string argument in XServerCommandLine.Build(options, display, logFile))
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.Exited += OnProcessExited;
            if (!process.Start())
            {
                process.Dispose();
                return XServerStartResult.Fail(Strings.Format("XServer_ErrLaunch", executable));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return XServerStartResult.Fail(Strings.Format("XServer_ErrLaunch", ex.Message));
        }

        Trace.WriteLine($"[XServer] started {executable} (pid {process.Id}) {string.Join(' ', start.ArgumentList)}");
        SetState(XServerState.Starting, process, display);

        try
        {
            return await WaitUntilListeningAsync(process, display, logFile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    private async Task<XServerStartResult> WaitUntilListeningAsync(
        Process process, int display, string logFile, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < ReadyTimeout)
        {
            if (process.HasExited)
            {
                return XServerStartResult.Fail(Strings.Format("XServer_ErrExited", process.ExitCode, logFile));
            }
            if (await _isDisplayInUse(display, cancellationToken).ConfigureAwait(false))
            {
                // 进程在等待期间被 Stop 掉 / 自己退了,OnProcessExited 已经把状态清了 —— 别把它改回来。
                lock (_stateLock)
                {
                    if (!ReferenceEquals(_process, process))
                    {
                        return XServerStartResult.Fail(Strings.Format("XServer_ErrExited", -1, logFile));
                    }
                    _state = XServerState.Running;
                }
                RaiseStateChanged();
                return XServerStartResult.Ok;
            }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        Kill(process);
        return XServerStartResult.Fail(Strings.Format("XServer_ErrTimeout", (int)ReadyTimeout.TotalSeconds, logFile));
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Process? process;
            lock (_stateLock)
            {
                process = _process;
            }
            if (process is null)
            {
                return;
            }
            Kill(process);
            using CancellationTokenSource limit = new(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 杀不掉就算了:状态照样清掉,别让按钮卡在「运行中」。
            }
            ClearIfCurrent(process);
            process.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<XServerDisplayResolution> ResolveForwardingDisplayAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return XServerDisplayResolution.None;
        }
        if (Display is { } running)
        {
            return new(running);
        }

        XServerOptions options = (await _settings.GetSnapshotAsync().ConfigureAwait(false)).XServer;
        if (!options.AutoStartForX11Forwarding
            || VcXsrvLocator.Find(options.ExecutablePath) is null
            || await _isDisplayInUse(0, cancellationToken).ConfigureAwait(false))
        {
            return XServerDisplayResolution.None;
        }

        XServerStartResult result = await StartAsync(cancellationToken).ConfigureAwait(false);
        return result.Success && Display is { } started
            ? new(started)
            : new(Display: null, result.Error);
    }

    /// <summary>自动模式:从 0 起挑第一个没人在听的显示号。</summary>
    internal static async Task<int?> SelectFreeDisplayAsync(
        Func<int, CancellationToken, Task<bool>> isDisplayInUse, CancellationToken cancellationToken)
    {
        for (int display = 0; display <= XServerOptions.MaxDisplayNumber; display++)
        {
            if (!await isDisplayInUse(display, cancellationToken).ConfigureAwait(false))
            {
                return display;
            }
        }
        return null;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is Process process)
        {
            Trace.WriteLine($"[XServer] process exited (code {SafeExitCode(process)})");
            ClearIfCurrent(process);
        }
    }

    private void SetState(XServerState state, Process process, int display)
    {
        lock (_stateLock)
        {
            _state = state;
            _process = process;
            DisplayNumber = display;
        }
        RaiseStateChanged();
    }

    private void ClearIfCurrent(Process process)
    {
        lock (_stateLock)
        {
            if (!ReferenceEquals(_process, process))
            {
                return;
            }
            _process = null;
            DisplayNumber = null;
            _state = XServerState.Stopped;
        }
        // 这里不 Dispose:进程自己退出时,启动等待(WaitUntilListeningAsync)可能还在读它的 HasExited。
        // 句柄由 SafeProcessHandle 的终结器兜底;主动停止的那条路径在 StopAsync 里当场释放。
        process.Exited -= OnProcessExited;
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
            // 订阅方的异常不能打断进程管理(Exited 回调在线程池上,抛出去就是进程级崩溃)。
            Trace.WriteLine($"[XServer] StateChanged handler failed: {ex}");
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 恰好自己退了,或已经没有权限碰它。
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    /// <summary>退出时把自己拉起的 VcXsrv 关掉。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Process? process;
        lock (_stateLock)
        {
            process = _process;
            _process = null;
            DisplayNumber = null;
            _state = XServerState.Stopped;
        }
        if (process is not null)
        {
            process.Exited -= OnProcessExited;
            Kill(process);
            process.Dispose();
        }
    }
}
