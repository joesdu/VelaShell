using System.Reactive;
using ReactiveUI;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.ViewModels;

public class HostKeyPromptViewModel : ReactiveObject
{
    public HostKeyPromptViewModel(
        string host,
        int port,
        string keyType,
        string fingerprint,
        HostKeyVerification verificationResult)
    {
        Host = host;
        Port = port;
        KeyType = keyType;
        Fingerprint = fingerprint;
        VerificationResult = verificationResult;
        IsChanged = verificationResult == HostKeyVerification.Changed;

        // 与主流 SSH 客户端一致的三选项:永久信任(写入 known_hosts)/
        // 仅本次信任(本次运行有效,不落盘)/ 取消(拒绝并中止连接)。
        TrustPermanentlyCommand = ReactiveCommand.Create(() => { Result = HostKeyDecision.TrustPermanently; });
        TrustOnceCommand = ReactiveCommand.Create(() => { Result = HostKeyDecision.TrustOnce; });
        CancelCommand = ReactiveCommand.Create(() => { Result = HostKeyDecision.Reject; });
    }

    public string Host { get; }

    public int Port { get; }

    public string KeyType { get; }

    public string Fingerprint { get; }

    public HostKeyVerification VerificationResult { get; }

    public bool IsChanged { get; }

    public string WarningText => IsChanged
                                     ? Strings.HostKeyChanged
                                     : Strings.HostKeyUnknown;

    public HostKeyDecision? Result
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> TrustPermanentlyCommand { get; }

    public ReactiveCommand<Unit, Unit> TrustOnceCommand { get; }

    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
}
