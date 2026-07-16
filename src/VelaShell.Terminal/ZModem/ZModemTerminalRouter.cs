using VelaShell.Core.Ssh;
using VelaShell.Core.ZModem.Abstractions;
using VelaShell.Core.ZModem.Diagnostics;
using VelaShell.Core.ZModem.Model;
using VelaShell.Core.ZModem.Protocol;

namespace VelaShell.Terminal.ZModem;

/// <summary>路由器当前所处的状态。</summary>
public enum ZModemRoutingState
{
    /// <summary>常态:字节正常喂入 VT 终端,同时监视 ZMODEM 引导。</summary>
    Normal,

    /// <summary>ZMODEM 会话进行中:字节全部转交引擎,不喂终端。</summary>
    InSession
}

/// <summary>一次字节路由的结果:应喂入终端的字节(会话期间为空)。</summary>
/// <param name="TerminalBytes">应喂入 VT 终端的字节。</param>
/// <param name="SessionStarted">本次调用是否刚触发了一个 ZMODEM 会话。</param>
public readonly record struct ZModemRouteResult(byte[] TerminalBytes, bool SessionStarted);

/// <summary>
/// 终端侧 ZMODEM 路由器:插在桥的读循环与终端喂入之间。常态下把输出字节透传给终端并
/// 监视 ZMODEM 引导;一旦命中,切入会话态——后续入站字节改喂 <see cref="ShellStreamByteDuplex" />,
/// 由后台任务上运行的 <see cref="ZModemReceiver" />(远端 <c>sz</c>)或 <see cref="ZModemSender" />
/// (远端 <c>rz</c>)消费,期间终端停止喂入。会话结束后自动复位回常态。
/// 设计为传输无关,SSH / ConPTY / 串口 / Telnet 通用。
/// </summary>
public sealed class ZModemTerminalRouter(
    IShellStreamWrapper shellStream,
    Func<IZModemFileSink> sinkFactory,
    Func<IZModemFileSource>? sourceFactory = null,
    ZModemOptions? options = null,
    IZModemSessionObserver? observer = null)
{
    private readonly IShellStreamWrapper _shellStream =
        shellStream ?? throw new ArgumentNullException(nameof(shellStream));
    private readonly Func<IZModemFileSink> _sinkFactory =
        sinkFactory ?? throw new ArgumentNullException(nameof(sinkFactory));
    private readonly Func<IZModemFileSource>? _sourceFactory = sourceFactory;
    private readonly ZModemOptions _options = options ?? ZModemOptions.Default;
    private readonly IZModemSessionObserver? _observer = observer;
    private readonly ZModemDetector _detector = new();
    private readonly Lock _gate = new();

    private ZModemRoutingState _state = ZModemRoutingState.Normal;
    private ShellStreamByteDuplex? _duplex;
    private CancellationTokenSource? _sessionCts;

    /// <summary>当前是否处于 ZMODEM 会话中。</summary>
    public bool IsInSession
    {
        get
        {
            lock (_gate)
            {
                return _state == ZModemRoutingState.InSession;
            }
        }
    }

    /// <summary>会话结束(成功 / 失败 / 取消)时触发,便于宿主刷新 UI 与恢复终端焦点。</summary>
    public event Action<ZModemSession>? SessionEnded;

    /// <summary>
    /// 处理一段来自读循环的原始输出字节,返回应喂入 VT 终端的字节。
    /// 会话期间返回空数组;检测到引导时启动会话并把引导及其后字节转交引擎。
    /// </summary>
    /// <param name="data">读循环刚读到的输出字节。</param>
    /// <returns>路由结果(待喂终端字节 + 是否刚启动会话)。</returns>
    public ZModemRouteResult ProcessIncoming(ReadOnlyMemory<byte> data)
    {
        lock (_gate)
        {
            if (_state == ZModemRoutingState.InSession)
            {
                // 会话进行中:全部转交引擎,终端不喂。
                ZModemTrace.LogBytes("RX->engine", data.Span);
                _duplex?.Push(data);
                return new([], false);
            }

            ZModemDetectResult detect = _detector.Process(data.Span);
            if (!detect.Detected)
            {
                return new(detect.TerminalBytes, false);
            }
            ZModemTrace.Log($"DETECT trigger={detect.Trigger} terminal={detect.TerminalBytes.Length}B protocol={detect.ProtocolBytes.Length}B");
            ZModemTrace.LogBytes("RX->engine(seed)", detect.ProtocolBytes);

            // 远端跑了 rz 但宿主没接线上传能力:不接管,原样喂终端(总比把终端吞掉强)。
            if (detect.Trigger == ZModemTrigger.Send && _sourceFactory is null)
            {
                byte[] passthrough = new byte[detect.TerminalBytes.Length + detect.ProtocolBytes.Length];
                detect.TerminalBytes.CopyTo(passthrough, 0);
                detect.ProtocolBytes.CopyTo(passthrough, detect.TerminalBytes.Length);
                return new(passthrough, false);
            }

            // 命中 ZMODEM 引导:切入会话态,把引导及其后字节喂给引擎。
            StartSession(detect.Trigger, detect.ProtocolBytes);
            return new(detect.TerminalBytes, true);
        }
    }

    private void StartSession(ZModemTrigger trigger, byte[] initialBytes)
    {
        // 调用方已持有 _gate。
        _state = ZModemRoutingState.InSession;
        var duplex = new ShellStreamByteDuplex(_shellStream);
        _duplex = duplex;
        var cts = new CancellationTokenSource();
        _sessionCts = cts;

        if (initialBytes.Length > 0)
        {
            duplex.Push(initialBytes);
        }

        // 引擎跑在后台任务上(读循环线程只负责搬字节,绝不在其上阻塞跑协议)。
        _ = Task.Run(async () =>
        {
            ZModemSession session;
            try
            {
                session = trigger == ZModemTrigger.Receive
                    ? await new ZModemReceiver(duplex, _sinkFactory(), _options, _observer)
                        .ReceiveAsync(cts.Token).ConfigureAwait(false)
                    : await new ZModemSender(duplex, _sourceFactory!(), _options, _observer)
                        .SendAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ZModemTrace.Log($"ENGINE THREW: {ex}");
                session = new ZModemSession
                {
                    Direction = trigger == ZModemTrigger.Receive
                        ? ZModemTransferDirection.Receive
                        : ZModemTransferDirection.Send,
                    Status = ZModemTransferStatus.Failed
                };
            }
            finally
            {
                await duplex.DisposeAsync().ConfigureAwait(false);
            }
            ZModemTrace.Log($"SESSION END status={session.Status} items={session.Items.Count}");
            EndSession();
            SessionEnded?.Invoke(session);
        });
    }

    private void EndSession()
    {
        lock (_gate)
        {
            _state = ZModemRoutingState.Normal;
            _duplex = null;
            _sessionCts?.Dispose();
            _sessionCts = null;
            // 复位检测器,丢弃可能残留的扣留字节(会话字节不应回流终端)。
            _ = _detector.Flush();
        }
    }

    /// <summary>请求取消进行中的会话(用户中止 / 标签关闭)。无会话时为空操作。</summary>
    public void CancelActiveSession()
    {
        lock (_gate)
        {
            _sessionCts?.Cancel();
            _duplex?.CompleteInbound();
        }
    }
}
