using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 锁定 <see cref="SshInterop.Translate" /> 的映射:调用方主动取消必须保留
/// <see cref="OperationCanceledException" />,不得被翻译为超时;各类库异常要落到对的
/// Core 中立类型上 —— 上层的自动重连与错误提示全靠这层分流。
/// </summary>
/// <remarks>
/// 上一版这里只能测取消那两条:底层库的异常类型构造函数都是 <c>internal</c>,
/// 造不出实例,映射只能靠 switch 的编译期类型检查"保证"——
/// 而那保证不了枚举值落错分支。现在异常是公开可构造的,映射能逐条断言。
/// </remarks>
[TestClass]
public sealed class SshInteropTests
{
    // ------------------------------------------------------------ 取消 / 超时

    [TestMethod]
    public void Translate_CallerCancelled_ReturnsNull_PreservingCancellationSemantics()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Exception? translated = SshInterop.Translate(new OperationCanceledException(cts.Token), cts.Token);

        Assert.IsNull(translated, "调用方主动取消时应原样上抛 OperationCanceledException,而不是翻译成超时。");
    }

    [TestMethod]
    public void Translate_CallerCancelled_TaskCanceledException_ReturnsNull()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // TaskCanceledException 派生自 OperationCanceledException,同样必须保留取消语义
        Exception? translated = SshInterop.Translate(new TaskCanceledException(), cts.Token);

        Assert.IsNull(translated);
    }

    [TestMethod]
    public void Translate_CancellationWithoutCallerCancel_MapsToTimeout()
    {
        Exception? translated = SshInterop.Translate(new OperationCanceledException(), CancellationToken.None);

        Assert.IsInstanceOfType<VelaSshOperationTimeoutException>(translated);
    }

    [TestMethod]
    public void Translate_CancellationWithLiveCallerToken_MapsToTimeout()
    {
        using var cts = new CancellationTokenSource();

        // 调用方 token 存在但未取消 → 取消来自库内部超时
        Exception? translated = SshInterop.Translate(new OperationCanceledException(), cts.Token);

        Assert.IsInstanceOfType<VelaSshOperationTimeoutException>(translated);
    }

    [TestMethod]
    public void Translate_UnknownException_ReturnsNull() =>
        Assert.IsNull(SshInterop.Translate(new InvalidOperationException("boom")));

    [TestMethod]
    public void Translate_PreservesInnerException()
    {
        var original = new OperationCanceledException("timed out");

        Exception? translated = SshInterop.Translate(original);

        Assert.IsNotNull(translated);
        Assert.AreSame(original, translated.InnerException);
    }

    // ------------------------------------------------------------ 建链失败的分流

    /// <summary>
    /// <b>「连不上」与「认证不过」必须分开。</b>
    /// </summary>
    /// <remarks>
    /// 主窗口的自动重连只该对前者生效:连不上值得再试一次,认证不过再试一百次也一样,
    /// 而且每次都会在服务端留一条失败登录。
    /// </remarks>
    [TestMethod]
    [DataRow(SshFailureReason.TcpRefused)]
    [DataRow(SshFailureReason.DnsFailure)]
    [DataRow(SshFailureReason.TcpUnreachable)]
    [DataRow(SshFailureReason.ProxyRefused)]
    public void Translate_ConnectFailure_MapsToConnectionException(SshFailureReason reason)
    {
        Exception? translated = SshInterop.Translate(
            new SshConnectException(reason, SshPhase.Dialing, "nope"));

        Assert.IsInstanceOfType<VelaSshConnectionException>(translated);
        Assert.IsNotInstanceOfType<VelaSshOperationTimeoutException>(translated);
    }

    [TestMethod]
    [DataRow(SshFailureReason.Timeout)]
    [DataRow(SshFailureReason.TcpTimeout)]
    [DataRow(SshFailureReason.KeepAliveTimeout)]
    public void Translate_ConnectTimeout_MapsToTimeoutException(SshFailureReason reason)
    {
        Exception? translated = SshInterop.Translate(
            new SshConnectException(reason, SshPhase.Dialing, "too slow"));

        Assert.IsInstanceOfType<VelaSshOperationTimeoutException>(translated);
    }

    /// <summary>认证失败要带上**逐条尝试记录**,而不只是一句"认证失败"。</summary>
    /// <remarks>
    /// 没有这份记录时,用户看不出是密钥被跳过了、服务端根本不接受这种方法、
    /// 还是口令真的错了 —— 三种情况的下一步完全不同。
    /// </remarks>
    [TestMethod]
    public void Translate_AuthenticationFailure_MapsToAuthException_WithAttempts()
    {
        SshAuthenticationException original = new(
            SshFailureReason.AuthenticationMethodExhausted,
            "认证失败。",
            [new SshAuthAttempt("publickey", "~/.ssh/id_ed25519", SshAuthOutcome.Failure, ["publickey"], "服务端不接受这把钥")],
            ["publickey", "keyboard-interactive"],
            partialSuccessAchieved: false);

        Exception? translated = SshInterop.Translate(original);

        Assert.IsInstanceOfType<VelaSshAuthenticationException>(translated);
        Assert.Contains("id_ed25519", translated!.Message,
            "逐条尝试记录必须进到消息里 —— 它是用户唯一能据以判断下一步的东西");
    }

    /// <summary>
    /// 算法协商失败要把**双方的完整名单**摊开。
    /// </summary>
    /// <remarks>
    /// 上一版为了同一件事要另开一条 TCP 回探对端的 KEXINIT(384 行),
    /// 因为上游没把对端名单暴露出来。现在异常自带,这里只要确认没把它丢掉。
    /// </remarks>
    [TestMethod]
    public void Translate_Negotiation_IncludesBothAlgorithmLists()
    {
        SshNegotiationException original = new(
            SshNegotiationCategory.EncryptionServerToClient,
            ["aes128-ctr"],
            ["aes256-gcm@openssh.com", "chacha20-poly1305@openssh.com"],
            "SSH-2.0-OpenSSH_7.4");

        Exception? translated = SshInterop.Translate(original);

        Assert.IsInstanceOfType<VelaSshConnectionException>(translated);
        Assert.Contains("aes128-ctr", translated!.Message, "对端提供了什么必须说出来");
        Assert.Contains("aes256-gcm@openssh.com", translated.Message, "本端支持什么也必须说出来");
        Assert.Contains("OpenSSH_7.4", translated.Message, "对端版本串是判断「这台设备太老」的依据");
    }

    // ------------------------------------------------------------ SFTP 的分流

    /// <summary>
    /// SFTP 要分出「没这个文件」与「没权限」—— 上层据此决定是提示用户还是静默跳过。
    /// </summary>
    [TestMethod]
    public void Translate_SftpNotFound_MapsToPathNotFound()
    {
        Exception? translated = SshInterop.Translate(
            new SftpException(SftpStatusCode.NoSuchFile, "No such file", "/tmp/nope", "打开文件"));

        Assert.IsInstanceOfType<VelaSftpPathNotFoundException>(translated);
    }

    [TestMethod]
    public void Translate_SftpPermissionDenied_MapsToPermissionDenied()
    {
        Exception? translated = SshInterop.Translate(
            new SftpException(SftpStatusCode.PermissionDenied, "Permission denied", "/root/x", "打开文件"));

        Assert.IsInstanceOfType<VelaSftpPermissionDeniedException>(translated);
    }

    /// <summary>
    /// 码 4(Failure)承载了绝大多数真实错误,**服务端原话是唯一能区分它们的信息**。
    /// </summary>
    /// <remarks>
    /// 「目录非空」「文件已存在」「磁盘满」「配额超限」全是同一个状态码 ——
    /// 把服务端那段文本丢掉,用户就只剩一句"操作失败"。
    /// </remarks>
    [TestMethod]
    public void Translate_SftpGenericFailure_KeepsServerMessage()
    {
        Exception? translated = SshInterop.Translate(
            new SftpException(SftpStatusCode.Failure, "Disk quota exceeded", "/home/joe/big.bin", "写入"));

        Assert.IsInstanceOfType<VelaSftpOperationException>(translated);
        Assert.Contains("Disk quota exceeded", translated!.Message);
    }
}
