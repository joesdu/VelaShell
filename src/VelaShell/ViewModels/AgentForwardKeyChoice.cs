using System.Security.Cryptography;
using ReactiveUI;

namespace VelaShell.ViewModels;

/// <summary>连接配置「只转发指定密钥」清单里的一行。</summary>
/// <remarks>
/// 候选来自三处,按指纹去重:本机 agent 里现有的钥、~/.ssh 下的公钥、以及这条配置里已经存过的钥
/// (后者可能已经不在前两处 —— 仍列出来并保持勾选,免得打开再保存就把它悄悄丢了)。
/// </remarks>
public sealed class AgentForwardKeyChoice : ReactiveObject
{
    private AgentForwardKeyChoice(string publicKeyLine, string keyType, string fingerprint, string label, string source)
    {
        PublicKeyLine = publicKeyLine;
        KeyType = keyType;
        Fingerprint = fingerprint;
        Label = label;
        Source = source;
    }

    /// <summary>OpenSSH 公钥行,保存时原样写进配置。</summary>
    public string PublicKeyLine { get; }

    /// <summary>密钥类型(<c>ssh-ed25519</c> 等)。</summary>
    public string KeyType { get; }

    /// <summary><c>SHA256:…</c> 指纹,去重与显示都用它。</summary>
    public string Fingerprint { get; }

    /// <summary>给人看的名字:agent 注释、~/.ssh 里的文件名,或公钥行里的注释。</summary>
    public string Label { get; }

    /// <summary>从哪来(agent / ~/.ssh / 仅在配置里)。</summary>
    public string Source { get; }

    /// <summary>是否转发这把钥。</summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>从公钥行建一行;解析不了时返回 <see langword="null" />。</summary>
    /// <param name="publicKeyLine"><c>类型 base64 [注释]</c>。</param>
    /// <param name="label">名字;<see langword="null" /> 时取公钥行里的注释,再没有就用类型。</param>
    /// <param name="source">来源说明。</param>
    public static AgentForwardKeyChoice? TryCreate(string publicKeyLine, string? label, string source)
    {
        string[] parts = publicKeyLine.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }
        string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        string name = !string.IsNullOrWhiteSpace(label) ? label
            : parts.Length > 2 ? parts[2]
            : parts[0];
        return new AgentForwardKeyChoice(publicKeyLine.Trim(), parts[0], fingerprint, name, source);
    }
}
