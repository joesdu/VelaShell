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
        TrustCommand = ReactiveCommand.Create(() => { Result = true; });
        RejectCommand = ReactiveCommand.Create(() => { Result = false; });
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

    public bool? Result
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> TrustCommand { get; }

    public ReactiveCommand<Unit, Unit> RejectCommand { get; }
}
