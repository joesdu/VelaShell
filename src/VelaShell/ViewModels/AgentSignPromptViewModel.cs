using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.ViewModels;

/// <summary>
/// agent 转发「逐次确认」弹窗的视图模型:远端要用本机 agent 里的某把钥签名时,
/// 摆出是哪条会话、哪把钥,给「拒绝 / 允许一次 / 本次会话内允许」三种处置。
/// </summary>
public class AgentSignPromptViewModel : ReactiveObject
{
    /// <summary>用一次签名请求构造。</summary>
    /// <param name="request">远端的这次签名请求。</param>
    public AgentSignPromptViewModel(AgentSignRequest request)
    {
        Target = request.Target;
        KeyType = request.KeyType;
        Fingerprint = request.Fingerprint;
        Comment = request.Comment;
        TimeoutText = Strings.Format("AgentSign_TimeoutHint", (int)Math.Ceiling(request.Timeout.TotalSeconds));

        DenyCommand = ReactiveCommand.Create(() => { Result = AgentSignDecision.Deny; });
        AllowOnceCommand = ReactiveCommand.Create(() => { Result = AgentSignDecision.AllowOnce; });
        AllowForSessionCommand = ReactiveCommand.Create(() => { Result = AgentSignDecision.AllowForSession; });
    }

    /// <summary>哪条会话在要:<c>用户@主机:端口</c>。</summary>
    public string Target { get; }

    /// <summary>密钥类型。</summary>
    public string KeyType { get; }

    /// <summary>密钥指纹。</summary>
    public string Fingerprint { get; }

    /// <summary>agent 里的注释(通常是私钥文件路径);可能为空。</summary>
    public string Comment { get; }

    /// <summary>有没有注释可显示。</summary>
    public bool HasComment => Comment.Length > 0;

    /// <summary>「N 秒内不作答将自动拒绝」。</summary>
    public string TimeoutText { get; }

    /// <summary>用户的裁决;未作答时为 null。</summary>
    public AgentSignDecision? Result
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>拒签。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DenyCommand { get; }

    /// <summary>只允许这一次。</summary>
    public ReactiveCommand<RxVoid, RxVoid> AllowOnceCommand { get; }

    /// <summary>这条会话里这把钥之后都不再问。</summary>
    public ReactiveCommand<RxVoid, RxVoid> AllowForSessionCommand { get; }
}
