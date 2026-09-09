using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VelaShell.Core.Ssh;

/// <summary>
/// SSH agent 协议(draft-miller-ssh-agent)里本项目需要的那一小块:**分帧、消息分类与密钥指纹**。
/// <para>
/// 之所以要自己认得这个协议 —— 转发一条 agent 通道原本只需要把字节来回搬 —— 是因为
/// <b>裸转发等于把本机 agent 的全部控制权交给远端</b>。`ssh -A` 就是这么做的,而这正是
/// agent 转发多年来的那条著名警告:远端上任何能读到 <c>SSH_AUTH_SOCK</c> 的人(含 root)
/// 不只能借你的钥匙签名,还能 <c>ssh-add -D</c> 把你的 agent 清空、<c>ssh-add -x</c> 把它锁上。
/// </para>
/// <para>
/// 于是这里逐帧看一眼消息号:<b>只放行「列举身份」与「签名」两种</b>(<see cref="RequestIdentities" />
/// / <see cref="SignRequest" />),其余一律就地回一个 <see cref="Failure" />,连本机 agent 都不惊动。
/// 转发出去的是**签名能力**,不是对 agent 的完全控制 —— 这是本实现与 <c>ssh -A</c> 的实质差别,
/// 也是它值得存在的理由之一。
/// </para>
/// <para>本类只做纯字节运算,不碰任何流,便于逐条构造报文做单元测试。</para>
/// </summary>
public static class SshAgentProtocol
{
    // ---- 消息号(draft-miller-ssh-agent §5.1)----

    /// <summary>SSH_AGENT_FAILURE:通用失败应答。</summary>
    public const byte Failure = 5;

    /// <summary>SSH_AGENT_SUCCESS:通用成功应答。</summary>
    public const byte Success = 6;

    /// <summary>SSH_AGENTC_REQUEST_IDENTITIES:请求列举 agent 持有的公钥。<b>放行</b>。</summary>
    public const byte RequestIdentities = 11;

    /// <summary>SSH_AGENT_IDENTITIES_ANSWER:上一条的应答,携带公钥与注释列表。</summary>
    public const byte IdentitiesAnswer = 12;

    /// <summary>SSH_AGENTC_SIGN_REQUEST:请求用指定公钥对应的私钥签一段数据。<b>放行</b>。</summary>
    public const byte SignRequest = 13;

    /// <summary>SSH_AGENT_SIGN_RESPONSE:上一条的应答,携带签名。</summary>
    public const byte SignResponse = 14;

    /// <summary>
    /// 单条报文的长度上限(256 KiB),与 OpenSSH 的 <c>AGENT_MAX_LEN</c> 同口径。
    /// <para>
    /// 这个上限不是调优而是**闸门**:长度字段来自远端,不封顶的话对端报一个
    /// <c>0xFFFFFFFF</c> 就能让我们替它申请 4 GiB。超限即断开该条连接。
    /// </para>
    /// </summary>
    public const int MaxMessageLength = 256 * 1024;

    /// <summary>报文长度前缀的字节数(uint32 大端)。</summary>
    public const int LengthPrefixSize = 4;

    /// <summary>只此两种消息号会被转发给本机 agent;其余就地拒绝。</summary>
    /// <param name="messageType">报文的第一个字节(消息号)。</param>
    /// <returns>放行为 <see langword="true" />。</returns>
    /// <remarks>
    /// 白名单而非黑名单:协议还在演进(扩展消息 27 之后又加过若干),黑名单会在下一次
    /// 扩展时默默漏一个过去,而这里漏过去的每一个都是本机 agent 的一次额外权限。
    /// </remarks>
    public static bool IsAllowed(byte messageType) =>
        messageType is RequestIdentities or SignRequest;

    /// <summary>把一个不带长度前缀的负载包成完整报文(4 字节大端长度 + 负载)。</summary>
    /// <param name="payload">报文负载,第一个字节为消息号。</param>
    /// <returns>可直接写入流的完整报文。</returns>
    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        byte[] framed = new byte[LengthPrefixSize + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)payload.Length);
        payload.CopyTo(framed.AsSpan(LengthPrefixSize));
        return framed;
    }

    /// <summary>单字节应答(如 <see cref="Failure" />)的完整报文。</summary>
    /// <param name="messageType">应答的消息号。</param>
    /// <returns>可直接写入流的完整报文。</returns>
    public static byte[] FrameSingle(byte messageType) => Frame([messageType]);

    /// <summary>
    /// 读长度前缀。返回负载长度;越界(0 或 &gt; <see cref="MaxMessageLength" />)时返回 -1。
    /// </summary>
    /// <param name="prefix">恰好 4 字节的长度前缀。</param>
    /// <returns>负载字节数;非法时为 -1。</returns>
    /// <remarks>长度 0 同样非法:协议里每条报文至少有一个消息号字节。</remarks>
    public static int ReadLength(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < LengthPrefixSize)
        {
            return -1;
        }
        uint length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        return length is 0 or > MaxMessageLength ? -1 : (int)length;
    }

    /// <summary>
    /// 从 <see cref="SignRequest" /> 报文里取出被请求签名的那把公钥的指纹
    /// (<c>SHA256:…</c>,与 <c>ssh-add -l</c> 打出来的一致)。
    /// </summary>
    /// <param name="payload">完整的 SIGN_REQUEST 负载(含消息号)。</param>
    /// <returns>指纹;报文不是签名请求或格式不符时返回 <see langword="null" />。</returns>
    /// <remarks>
    /// 有了它,「远端刚才用你的哪把钥匙签了一次名」才写得进审计日志 —— 否则转发过去的
    /// 就是一个完全不透明的黑盒,出了事连"用的哪把钥匙"都答不上来。
    /// </remarks>
    public static string? TryGetSignRequestFingerprint(ReadOnlySpan<byte> payload)
    {
        // 负载布局:byte type | string key_blob | string data | uint32 flags
        if (payload.Length < 1 + 4 || payload[0] != SignRequest)
        {
            return null;
        }
        return TryReadString(payload[1..], out ReadOnlySpan<byte> keyBlob, out _)
                   ? Fingerprint(keyBlob)
                   : null;
    }

    /// <summary>
    /// 解析 <see cref="IdentitiesAnswer" />,给出 agent 当前持有的公钥列表。
    /// </summary>
    /// <param name="payload">完整的 IDENTITIES_ANSWER 负载(含消息号)。</param>
    /// <returns>身份列表;报文不是身份应答或格式不符时返回空表。</returns>
    /// <remarks>
    /// 密钥管理页要显示「agent 里现在有几把钥匙」,以及转发起来之后要在审计里说清
    /// 「这一条会话把哪几把钥匙暴露给了对端」—— 两处都靠它。
    /// </remarks>
    public static IReadOnlyList<SshAgentIdentity> ParseIdentities(ReadOnlySpan<byte> payload)
    {
        // 负载布局:byte type | uint32 nkeys | nkeys × (string key_blob, string comment)
        if (payload.Length < 1 + 4 || payload[0] != IdentitiesAnswer)
        {
            return [];
        }
        uint count = BinaryPrimitives.ReadUInt32BigEndian(payload[1..]);
        ReadOnlySpan<byte> rest = payload[5..];
        // 数量字段同样来自对端:先按剩余字节数封顶,免得一个谎报的 nkeys 让我们
        // 空转百万次循环(每条至少要 8 字节的两个长度前缀)。
        int capped = (int)Math.Min(count, (uint)(rest.Length / 8 + 1));
        var identities = new List<SshAgentIdentity>(Math.Min(capped, 64));
        for (int i = 0; i < capped; i++)
        {
            if (!TryReadString(rest, out ReadOnlySpan<byte> blob, out rest)
                || !TryReadString(rest, out ReadOnlySpan<byte> comment, out rest))
            {
                break;
            }
            identities.Add(new(KeyTypeOf(blob), Fingerprint(blob), Utf8OrEmpty(comment)));
        }
        return identities;
    }

    /// <summary>公钥 blob 的 OpenSSH 指纹(<c>SHA256:</c> + base64,按惯例去掉补位的 <c>=</c>)。</summary>
    /// <param name="keyBlob">SSH 线格式的公钥 blob。</param>
    /// <returns>形如 <c>SHA256:abc…</c> 的指纹。</returns>
    public static string Fingerprint(ReadOnlySpan<byte> keyBlob)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(keyBlob, hash);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    /// <summary>公钥 blob 的首个字符串字段即密钥类型(<c>ssh-ed25519</c>、<c>ssh-rsa</c>…)。</summary>
    /// <param name="keyBlob">SSH 线格式的公钥 blob。</param>
    /// <returns>密钥类型;解析不出时为 <c>unknown</c>。</returns>
    public static string KeyTypeOf(ReadOnlySpan<byte> keyBlob) =>
        TryReadString(keyBlob, out ReadOnlySpan<byte> type, out _) && type.Length > 0
            ? Utf8OrEmpty(type)
            : "unknown";

    /// <summary>读一个 SSH 线格式字符串(uint32 长度 + 字节),并给出剩余部分。</summary>
    private static bool TryReadString(ReadOnlySpan<byte> input, out ReadOnlySpan<byte> value, out ReadOnlySpan<byte> rest)
    {
        value = default;
        rest = default;
        if (input.Length < 4)
        {
            return false;
        }
        uint length = BinaryPrimitives.ReadUInt32BigEndian(input);
        if (length > (uint)(input.Length - 4))
        {
            return false;
        }
        value = input.Slice(4, (int)length);
        rest = input[(4 + (int)length)..];
        return true;
    }

    /// <summary>把字节按 UTF-8 解出来;非法序列以替换字符收场,绝不抛 —— 这是对端给的数据。</summary>
    private static string Utf8OrEmpty(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);
}

/// <summary>本机 SSH agent 持有的一把密钥(只有公开信息:私钥永远不出 agent)。</summary>
/// <param name="KeyType">密钥类型,如 <c>ssh-ed25519</c>。</param>
/// <param name="Fingerprint">OpenSSH 指纹(<c>SHA256:…</c>)。</param>
/// <param name="Comment">agent 里登记的注释,通常是密钥文件路径或邮箱。</param>
public sealed record SshAgentIdentity(string KeyType, string Fingerprint, string Comment);
