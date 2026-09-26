// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测:对端给的文本进异常消息之前先清一遍（终端转义序列注入）
//
// 异常消息最后会被打到终端或界面上。版本标识串、DISCONNECT 的描述、拒绝开通道的理由、
// SFTP 的状态消息都来自对端 —— 很多时候来自一个还没被认证的对端。

using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Protocol;

[TestClass]
[TestCategory("Protocol")]
public sealed class PeerTextTests
{
    // ESC ] 52 改剪贴板、ESC [ 2 J 清屏、CR 盖掉前半行、BEL、C1 的 CSI、双向覆盖符。
    private const string Hostile = "正常的中文\u001b]52;c;cm0gLXJmIH4=\u0007\u001b[2J\r伪造的提示\u009b31m‮txt.exe";

    private static void AssertClean(string message)
    {
        foreach (char c in message)
        {
            Assert.IsFalse(
                c is < ' ' or >= '\u007F' and <= '\u009F' or >= '‪' and <= '‮',
                $"消息里不该有控制字符 U+{(int)c:X4}：{message}");
        }
    }

    [TestMethod]
    public void 控制字符与双向覆盖符被换掉_可打印的中文原样保留()
    {
        string cleaned = PeerText.Sanitize(Hostile);

        AssertClean(cleaned);
        Assert.Contains("正常的中文", cleaned);
        Assert.Contains("伪造的提示", cleaned);
    }

    [TestMethod]
    public void 超长的截断()
    {
        string cleaned = PeerText.Sanitize(new string('a', 10_000), maxLength: 100);
        Assert.AreEqual(101, cleaned.Length, "100 个字符加一个省略号");
    }

    [TestMethod]
    public void SFTP状态消息进异常之前被清_原话留在ServerMessage()
    {
        SftpException error = new(SftpStatusCode.Failure, Hostile, "/tmp/\u001b[2Jx", SftpOperation.Read);

        AssertClean(error.Message);
        Assert.AreEqual(Hostile, error.ServerMessage, "原话照样交出去（文档写明是不可信输入）");
    }

    [TestMethod]
    public void 拒绝开通道的理由进异常之前被清_原话留在PeerDescription()
    {
        SshChannelException error = SshChannelException.FromOpenFailure(
            "session", (uint)SshChannelOpenFailureReason.ConnectFailed, Hostile);

        AssertClean(error.Message);
        Assert.AreEqual(Hostile, error.PeerDescription);
    }

    [TestMethod]
    public async Task 版本标识串里的转义序列不进异常消息()
    {
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();
        await using SshPacketTransport clientTransport = new(clientStream);
        await using SshPacketTransport serverTransport = new(serverStream);

        // 协议版本那一段里藏一个清屏序列 —— 那一段曾经原样拼进「对端只支持 SSH 协议 …」。
        await serverTransport.WriteLineAsync("SSH-1.\u001b[2J5-Evil");

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshVersionExchange.ExchangeAsync(clientTransport));

        Assert.AreEqual(SshFailureReason.VersionMismatch, error.Reason);
        AssertClean(error.Message);
    }
}
