using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using VelaShell.Core.Data;
using VelaShell.Terminal;
using VelaShell.Terminal.Rendering;
using VelaShell.ViewModels;

namespace VelaShell.Services;

internal enum PasswordFillResult { Queued, Expired, Unavailable, InvalidPassword, ReadFailed }

/// <summary>一次用户主动发起的填入请求;失效后不能因切回原标签而恢复。</summary>
internal sealed class SharedPasswordFillRequest : IDisposable
{
    private readonly TerminalTabViewModel _tab;
    private readonly VelaTerminalControl _control;
    private readonly SshTerminalBridge? _bridge;
    private readonly long _connectionGeneration;
    private readonly long _inputGeneration;
    private readonly Func<bool> _isActive;
    private readonly (int Row, int Col, int Screen, int Encoding, string Line) _prompt;
    private readonly bool _submitsWithEnter;
    private bool _expired;
    private bool _sending;

    internal SharedPasswordFillRequest(TerminalTabViewModel tab, Func<bool> isActive)
    {
        Dispatcher.UIThread.VerifyAccess();
        _tab = tab;
        _control = (VelaTerminalControl)tab.TerminalEmulator.Control;
        _bridge = tab.Bridge;
        _inputGeneration = _bridge?.InputGeneration ?? 0;
        _connectionGeneration = tab.ConnectionGeneration;
        _isActive = isActive;
        _prompt = CapturePrompt();
        _submitsWithEnter = IsPasswordPromptAtCursor(_control) && !_control.IsAtShellPrompt;
        _control.TypedInput += OnInput;
        _control.OutputUpdated += OnOutput;
        _bridge?.InputQueued += OnInputQueued;
    }

    private (int, int, int, int, string) CapturePrompt() =>
        (_control.CursorRow, _control.CursorCol, _control.ScreenGeneration, _control.SessionEncoding.CodePage,
            _control.GetBufferLine(_control.CursorRow));

    /// <summary>
    /// 填入之后替用户按不按回车 —— 开列表那一刻就定下,列表顶部的提示照它说(提示行一变请求就作废,定了不会过时)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只在光标行认得出是一条密码提示时按(与自动弹列表同一判据,<see cref="IsPasswordPromptAtCursor" />)。
    /// 自动弹出的列表本来就只开在这种行上;命令面板的手动入口却开在光标此刻所在的任何一行 ——
    /// 普通 shell 提示符、vim 的插入模式、会回显的程序。在那些地方按下回车,密码就成了一条命令:
    /// 进 ~/.bash_history、被「command not found」原样回显、被会话日志和录制记下(裸词不在遮盖范围里)。
    /// 只填不按,敲错了地方还能 Ctrl+U 收回。
    /// </para>
    /// <para>
    /// 认得出也不按:shell 集成说对端 shell 正停在它自己的提示符上(<see cref="VelaTerminalControl.IsAtShellPrompt" />)——
    /// 那时读输入的是 shell,不是要口令的程序。
    /// </para>
    /// </remarks>
    internal bool SubmitsWithEnter => _submitsWithEnter;

    /// <summary>
    /// 光标所在这一行是不是一条认得出的密码提示:整段逻辑行(窄窗格里折开的 git 提示)或光标行本身认得出都算 ——
    /// 与 <c>TerminalTabView</c> 自动弹列表用的是同一个判据。
    /// </summary>
    private static bool IsPasswordPromptAtCursor(VelaTerminalControl control) =>
        InteractivePromptDetector.IsPasswordFillPrompt(control.GetLogicalLineEndingAt(control.CursorRow))
        || InteractivePromptDetector.IsPasswordFillPrompt(control.GetBufferLine(control.CursorRow));

    /// <summary>
    /// 键入、别的出站输入或提示行变化让请求作废的那一刻(UI 线程)。宿主据此收起列表 —— 否则空口令上的 Backspace
    /// 这类不改变跟踪输入的按键会留下一个点哪项都只回「已过期」的列表。
    /// </summary>
    /// <remarks>
    /// 不经键入的出站输入(同步输入转来的、防空闲的 NUL、程序注入)经桥的 <see cref="SshTerminalBridge.InputQueued" /> 报来,
    /// 同样作废 —— 曾经只在点选时由 <see cref="IsValid" /> 拦下,列表开着、点哪项都只回「已过期」。
    /// 已经点选、正在读取口令时例外:这时不收列表,由 <see cref="FillAsync" /> 回「已过期」并在列表上说明什么都没发出去
    /// (悄悄收口会让人以为已经填上了)。
    /// </remarks>
    internal event Action? Expired;

    /// <summary>光标所在的提示行已经不是开列表时那一行(位置、内容、屏幕或编码变了)。</summary>
    internal bool PromptChanged => CapturePrompt() != _prompt;

    private void OnInput(byte[] _) => Expire();

    /// <summary>桥上有输入入队:防空闲在计时器线程上,先封送回 UI 线程再判。</summary>
    private void OnInputQueued()
    {
        // 按入队那一刻算「是不是正在读口令」:封送到 UI 线程时 FillAsync 可能已经回完「已过期」,
        // 那时再收列表,刚写上的说明就跟着没了。
        bool duringFill = Volatile.Read(ref _sending);
        if (Dispatcher.UIThread.CheckAccess())
        {
            OnOutboundInput(duringFill);
        }
        else
        {
            Dispatcher.UIThread.Post(() => OnOutboundInput(duringFill));
        }
    }

    private void OnOutboundInput(bool duringFill)
    {
        // 读取口令期间来的(含填入自己那一次入队)不收列表,交给 FillAsync 的复核去回「已过期」。
        // 代次没变说明这一下早于开列表(封送晚到了),不算。
        if (duringFill || _bridge is not { } bridge || bridge.InputGeneration == _inputGeneration)
        {
            return;
        }
        Expire();
    }

    private void OnOutput()
    {
        if (PromptChanged)
        {
            Expire();
        }
    }

    private void Expire()
    {
        if (_expired)
        {
            return; // 已作废(含已释放、已填入):不再惊动宿主。
        }
        _expired = true;
        Expired?.Invoke();
    }

    internal void Invalidate() => _expired = true;

    internal bool IsValid => !_expired && _tab.IsConnected && _tab.ShellStream is { CanWrite: true } && _bridge is not null
        && ReferenceEquals(_bridge, _tab.Bridge) && _connectionGeneration == _tab.ConnectionGeneration
        && _bridge.InputGeneration == _inputGeneration
        && _isActive() && !PromptChanged;

    internal async Task<PasswordFillResult> FillAsync(ISharedCredentialRepository repository, Guid id)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_sending || !IsValid)
        {
            return PasswordFillResult.Expired;
        }
        _sending = true;
        try
        {
            string? password;
            try
            {
                password = await repository.GetPasswordAsync(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return PasswordFillResult.ReadFailed;
            }
            // 无 await 地复核并入队,不能把解密期间切换到的新会话当成发送目标。
            if (!IsValid)
            {
                return PasswordFillResult.Expired;
            }
            if (string.IsNullOrEmpty(password))
            {
                return PasswordFillResult.Unavailable;
            }
            if (password.Any(char.IsControl))
            {
                return PasswordFillResult.InvalidPassword;
            }
            byte[] bytes;
            try
            {
                var encoding = (Encoding)_control.SessionEncoding.Clone();
                encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
                byte[] secret = encoding.GetBytes(password);
                // 认得出的密码提示上选中即提交:正文后面紧跟一次回车(与此刻按 Enter 发出的字节相同,换行模式下是 CR LF),
                // 两者拼成一段、同一次入队。分两次发的话,用户选完顺手敲的字符会插在密码与回车之间,
                // 被当成口令的一部分交上去。别处只填不按(见 SubmitsWithEnter);读取期间 shell 集成改口说
                // 已回到 shell 提示符的,同样不按 —— 宁可少按一次,也不把密码当命令执行。
                byte[] enter = _submitsWithEnter && !_control.IsAtShellPrompt ? _control.EncodeEnterKey() : [];
                bytes = new byte[secret.Length + enter.Length];
                secret.CopyTo(bytes, 0);
                enter.CopyTo(bytes, secret.Length);
                CryptographicOperations.ZeroMemory(secret);
            }
            catch (EncoderFallbackException)
            {
                return PasswordFillResult.InvalidPassword;
            }
            if (!IsValid)
            {
                CryptographicOperations.ZeroMemory(bytes);
                return PasswordFillResult.Expired;
            }
            // 不经 TypedInput:密码不得进入广播、补全跟踪、输入诊断或剪贴板。桥持有 bytes,不能立即擦除。
            if (!_bridge!.TrySendRaw(bytes, _inputGeneration))
            {
                CryptographicOperations.ZeroMemory(bytes);
                _expired = true;
                return PasswordFillResult.Expired;
            }
            _expired = true;
            return PasswordFillResult.Queued;
        }
        finally
        {
            _sending = false;
        }
    }

    public void Dispose()
    {
        _expired = true;
        _control.TypedInput -= OnInput;
        _control.OutputUpdated -= OnOutput;
        _bridge?.InputQueued -= OnInputQueued;
    }
}
