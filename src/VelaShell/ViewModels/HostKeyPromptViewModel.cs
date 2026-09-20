using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.ViewModels;

/// <summary>
/// 主机密钥确认弹窗的视图模型:首次连接遇到未知主机密钥、或密钥发生变更时,
/// 向用户展示指纹并提供“永久信任 / 仅本次信任 / 取消”三种处置。
/// </summary>
public class HostKeyPromptViewModel : ReactiveObject
{
    /// <summary>用主机连接信息与密钥校验结果构造弹窗视图模型,并据此初始化三个处置命令。</summary>
    /// <param name="host">目标主机地址。</param>
    /// <param name="port">目标主机端口。</param>
    /// <param name="keyType">本次主机密钥的算法类型。</param>
    /// <param name="fingerprint">本次主机密钥的指纹。</param>
    /// <param name="verificationResult">本次指纹相对 known_hosts 的校验结果。</param>
    /// <param name="knownFingerprint">known_hosts 里已记录的旧指纹;首次连接时为 null。</param>
    public HostKeyPromptViewModel(
        string host,
        int port,
        string keyType,
        string fingerprint,
        HostKeyVerification verificationResult,
        string? knownFingerprint = null)
    {
        Host = host;
        Port = port;
        KeyType = keyType;
        Fingerprint = fingerprint;
        VerificationResult = verificationResult;
        IsChanged = verificationResult == HostKeyVerification.Changed;
        KnownFingerprint = knownFingerprint ?? string.Empty;

        // 与主流 SSH 客户端一致的三选项:永久信任(写入 known_hosts)/
        // 仅本次信任(本次运行有效,不落盘)/ 取消(拒绝并中止连接)。
        TrustPermanentlyCommand = ReactiveCommand.Create(() => { Result = HostKeyDecision.TrustPermanently; });
        TrustOnceCommand = ReactiveCommand.Create(() => { Result = HostKeyDecision.TrustOnce; });
        CancelCommand = ReactiveCommand.Create(() => { Result = HostKeyDecision.Reject; });
    }

    /// <summary>目标主机地址(主机名或 IP)。</summary>
    public string Host { get; }

    /// <summary>目标主机的 SSH 端口。</summary>
    public int Port { get; }

    /// <summary>主机密钥的算法类型(如 ssh-ed25519、ssh-rsa)。</summary>
    public string KeyType { get; }

    /// <summary>待确认的主机密钥指纹。</summary>
    public string Fingerprint { get; }

    /// <summary>known_hosts 里已记录的旧指纹;首次连接时为空串。</summary>
    public string KnownFingerprint { get; }

    /// <summary>是否有旧指纹可摆出来对照(= 指纹变更且记录读得到)。</summary>
    public bool HasKnownFingerprint => KnownFingerprint.Length > 0;

    /// <summary>本次主机密钥的校验结果(未知 / 已变更等)。</summary>
    public HostKeyVerification VerificationResult { get; }

    /// <summary>主机密钥是否相较 known_hosts 记录发生了变更(可能预示中间人攻击)。</summary>
    public bool IsChanged { get; }

    /// <summary>展示给用户的告警文案:密钥变更与首次未知分别给出不同提示。</summary>
    public string WarningText => IsChanged
                                     ? Strings.HostKeyChanged
                                     : Strings.HostKeyUnknown;

    /// <summary>
    /// 告警下面那段"该怎么判断"的说明。变更时点名两种可能(服务器重装/换机 vs 中间人),
    /// 首次连接时提示与管理员核对 —— 只喊一句“变了”对用户毫无帮助。
    /// </summary>
    public string AdviceText => IsChanged
                                    ? Strings.Get("HostKeyChangedAdvice")
                                    : Strings.Get("HostKeyUnknownAdvice");

    /// <summary>用户的最终处置结果;未做选择时为 null。</summary>
    public HostKeyDecision? Result
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>“永久信任”命令:接受该密钥并写入 known_hosts。</summary>
    public ReactiveCommand<RxVoid, RxVoid> TrustPermanentlyCommand { get; }

    /// <summary>“仅本次信任”命令:接受该密钥但不落盘,仅本次运行有效。</summary>
    public ReactiveCommand<RxVoid, RxVoid> TrustOnceCommand { get; }

    /// <summary>“取消”命令:拒绝该密钥并中止连接。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }
}
