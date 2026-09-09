using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// SSH agent 协议的分帧、策略闸门与指纹计算。
/// </summary>
/// <remarks>
/// 这一层是 agent 转发的**安全边界**:放行判断错一个消息号,远端就能 <c>ssh-add -D</c>
/// 清空你的 agent;长度校验松一格,远端报一个 <c>0xFFFFFFFF</c> 就能让我们替它申请 4 GiB。
/// 两类错误都不会在功能测试里露头 —— 转发照常工作,只是多了一个洞。所以逐条钉在这里。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public class SshAgentProtocolTests
{
    /// <summary>只有「列举身份」与「签名」两种消息该被转发给本机 agent。</summary>
    [TestMethod]
    [DataRow(SshAgentProtocol.RequestIdentities)]
    [DataRow(SshAgentProtocol.SignRequest)]
    public void IsAllowed_ReadOnlyAndSign_AreForwarded(byte messageType) =>
        Assert.IsTrue(SshAgentProtocol.IsAllowed(messageType));

    /// <summary>
    /// 一切**改动 agent** 的消息都必须被挡下。这是本实现与 <c>ssh -A</c> 的实质差别:
    /// 转发出去的是签名能力,不是对 agent 的完全控制。
    /// </summary>
    [TestMethod]
    [DataRow((byte)17)] // ADD_IDENTITY
    [DataRow((byte)18)] // REMOVE_IDENTITY
    [DataRow((byte)19)] // REMOVE_ALL_IDENTITIES —— 远端一条命令清空你的 agent
    [DataRow((byte)20)] // ADD_SMARTCARD_KEY
    [DataRow((byte)21)] // REMOVE_SMARTCARD_KEY
    [DataRow((byte)22)] // LOCK
    [DataRow((byte)23)] // UNLOCK
    [DataRow((byte)25)] // ADD_ID_CONSTRAINED
    [DataRow((byte)26)] // ADD_SMARTCARD_KEY_CONSTRAINED
    [DataRow((byte)27)] // EXTENSION —— 白名单存在的理由:协议还在长
    [DataRow((byte)99)] // 未来的、我们还不认识的
    public void IsAllowed_AnythingThatMutatesTheAgent_IsRefused(byte messageType) =>
        Assert.IsFalse(SshAgentProtocol.IsAllowed(messageType));

    [TestMethod]
    public void Frame_ThenReadLength_RoundTrips()
    {
        byte[] framed = SshAgentProtocol.Frame([SshAgentProtocol.RequestIdentities]);

        Assert.HasCount(5, framed);
        Assert.AreEqual(1, SshAgentProtocol.ReadLength(framed));
        Assert.AreEqual(SshAgentProtocol.RequestIdentities, framed[4]);
    }

    /// <summary>长度 0 也非法:协议里每条报文至少带一个消息号字节。</summary>
    [TestMethod]
    public void ReadLength_Zero_IsRejected() =>
        Assert.AreEqual(-1, SshAgentProtocol.ReadLength([0, 0, 0, 0]));

    /// <summary>
    /// 谎报的巨大长度必须当场判非法。没有这道闸,一个 4 字节的前缀就能让我们
    /// 替对端申请 4 GiB —— 那是一条只需要发四个字节的拒绝服务。
    /// </summary>
    [TestMethod]
    public void ReadLength_AboveCap_IsRejected()
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, uint.MaxValue);

        Assert.AreEqual(-1, SshAgentProtocol.ReadLength(prefix));
    }

    [TestMethod]
    public void ReadLength_AtCap_IsAccepted()
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, SshAgentProtocol.MaxMessageLength);

        Assert.AreEqual(SshAgentProtocol.MaxMessageLength, SshAgentProtocol.ReadLength(prefix));
    }

    /// <summary>指纹必须与 <c>ssh-add -l</c> 打出来的一致:SHA256 的 base64,去掉补位的 =。</summary>
    [TestMethod]
    public void Fingerprint_MatchesOpenSshFormat()
    {
        byte[] blob = BuildKeyBlob("ssh-ed25519", [1, 2, 3, 4]);
        string expected = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

        Assert.AreEqual(expected, SshAgentProtocol.Fingerprint(blob));
        Assert.DoesNotContain("=", SshAgentProtocol.Fingerprint(blob));
    }

    /// <summary>
    /// 从签名请求里取得指纹 —— 「远端刚才用你的哪把钥匙签了名」这句话能不能写进审计,
    /// 全看这一步。
    /// </summary>
    [TestMethod]
    public void TryGetSignRequestFingerprint_ReadsTheKeyBlob()
    {
        byte[] blob = BuildKeyBlob("ssh-rsa", [9, 9, 9]);
        byte[] payload = BuildSignRequest(blob, "data to sign"u8.ToArray(), flags: 4);

        Assert.AreEqual(SshAgentProtocol.Fingerprint(blob), SshAgentProtocol.TryGetSignRequestFingerprint(payload));
    }

    /// <summary>不是签名请求就该老实说不知道,而不是拿别的报文的头几个字节硬算一个指纹出来。</summary>
    [TestMethod]
    public void TryGetSignRequestFingerprint_OnOtherMessages_IsNull()
    {
        Assert.IsNull(SshAgentProtocol.TryGetSignRequestFingerprint([SshAgentProtocol.RequestIdentities]));
        Assert.IsNull(SshAgentProtocol.TryGetSignRequestFingerprint([]));
    }

    [TestMethod]
    public void ParseIdentities_ReadsTypeFingerprintAndComment()
    {
        byte[] ed = BuildKeyBlob("ssh-ed25519", [1]);
        byte[] rsa = BuildKeyBlob("ssh-rsa", [2]);

        IReadOnlyList<SshAgentIdentity> identities =
            SshAgentProtocol.ParseIdentities(BuildIdentitiesAnswer((ed, "me@laptop"), (rsa, "deploy key")));

        Assert.HasCount(2, identities);
        Assert.AreEqual("ssh-ed25519", identities[0].KeyType);
        Assert.AreEqual(SshAgentProtocol.Fingerprint(ed), identities[0].Fingerprint);
        Assert.AreEqual("me@laptop", identities[0].Comment);
        Assert.AreEqual("ssh-rsa", identities[1].KeyType);
        Assert.AreEqual("deploy key", identities[1].Comment);
    }

    /// <summary>
    /// 数量字段来自对端,谎报一个天文数字不该让我们空转到天荒地老 ——
    /// 按剩余字节数封顶之后,解到读不动为止就收手。
    /// </summary>
    [TestMethod]
    public void ParseIdentities_LyingCount_StopsAtTheRealData()
    {
        byte[] answer = BuildIdentitiesAnswer((BuildKeyBlob("ssh-ed25519", [1]), "only one"));
        // 把 nkeys 改成 100 万,数据却只有一条。
        BinaryPrimitives.WriteUInt32BigEndian(answer.AsSpan(1), 1_000_000);

        Assert.HasCount(1, SshAgentProtocol.ParseIdentities(answer));
    }

    [TestMethod]
    public void ParseIdentities_OnOtherMessages_IsEmpty() =>
        Assert.IsEmpty(SshAgentProtocol.ParseIdentities([SshAgentProtocol.Failure]));

    /// <summary>认不出类型时给 <c>unknown</c>,而不是抛 —— 这是对端给的字节。</summary>
    [TestMethod]
    public void KeyTypeOf_Garbage_IsUnknown() =>
        Assert.AreEqual("unknown", SshAgentProtocol.KeyTypeOf([0xFF, 0xFF, 0xFF, 0xFF]));

    // ---- 报文构造(SSH 线格式:uint32 大端长度 + 字节)----

    internal static byte[] BuildKeyBlob(string keyType, byte[] body)
    {
        var blob = new List<byte>();
        AppendString(blob, Encoding.ASCII.GetBytes(keyType));
        AppendString(blob, body);
        return [.. blob];
    }

    internal static byte[] BuildSignRequest(byte[] keyBlob, byte[] data, uint flags)
    {
        var payload = new List<byte> { SshAgentProtocol.SignRequest };
        AppendString(payload, keyBlob);
        AppendString(payload, data);
        payload.AddRange(SshAgentRelay.Uint32(flags));
        return [.. payload];
    }

    internal static byte[] BuildIdentitiesAnswer(params (byte[] Blob, string Comment)[] identities)
    {
        var payload = new List<byte> { SshAgentProtocol.IdentitiesAnswer };
        payload.AddRange(SshAgentRelay.Uint32((uint)identities.Length));
        foreach ((byte[] blob, string comment) in identities)
        {
            AppendString(payload, blob);
            AppendString(payload, Encoding.UTF8.GetBytes(comment));
        }
        return [.. payload];
    }

    private static void AppendString(List<byte> target, byte[] value)
    {
        target.AddRange(SshAgentRelay.Uint32((uint)value.Length));
        target.AddRange(value);
    }
}
