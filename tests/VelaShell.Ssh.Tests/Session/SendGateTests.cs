// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §8.2(发送闸门)
//           RFC 4253 §7.1

using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Tests.Session;

[TestClass]
[TestCategory("Session")]
public sealed class SendGateTests
{
    private static SendGate<string> NewGate(long maxStash = SendGate<string>.DefaultMaxStashBytes) => new(maxStash);

    // ------------------------------------------------------------ 分区判定

    [TestMethod]
    public void 传输层消息是编号一到四十九()
    {
        // 分区依据 RFC 4250 §4.1.2。边界弄错的后果是重协商期间
        // 要么挡掉了 KEX 报文（死锁），要么放行了通道数据（协议违规）。
        Assert.IsTrue(SendGate<string>.IsTransportMessage((SshMessageNumber)1));
        Assert.IsTrue(SendGate<string>.IsTransportMessage(SshMessageNumber.KexInit));      // 20
        Assert.IsTrue(SendGate<string>.IsTransportMessage(SshMessageNumber.NewKeys));      // 21
        Assert.IsTrue(SendGate<string>.IsTransportMessage((SshMessageNumber)30));          // KEX 方法专用
        Assert.IsTrue(SendGate<string>.IsTransportMessage((SshMessageNumber)49));

        Assert.IsFalse(SendGate<string>.IsTransportMessage(default));
        Assert.IsFalse(SendGate<string>.IsTransportMessage(SshMessageNumber.UserAuthRequest)); // 50
        Assert.IsFalse(SendGate<string>.IsTransportMessage(SshMessageNumber.ChannelData));     // 94
    }

    // ------------------------------------------------------------ 常态放行

    [TestMethod]
    public void 闸门开着时一律放行()
    {
        SendGate<string> gate = NewGate();
        Assert.IsTrue(gate.IsOpen);
        Assert.AreEqual(SendGateAdmission.Send, gate.Admit("a", SshMessageNumber.ChannelData, 100));
        Assert.AreEqual(SendGateAdmission.Send, gate.Admit("b", SshMessageNumber.UserAuthRequest, 100));
        Assert.AreEqual(0, gate.StashedCount);
    }

    // ------------------------------------------------------------ 重协商

    [TestMethod]
    public void 关闸后传输层消息仍然放行()
    {
        // 密钥交换本身就是靠这些报文完成的 —— 挡掉它们就是死锁。
        SendGate<string> gate = NewGate();
        gate.Close();

        Assert.AreEqual(SendGateAdmission.Send, gate.Admit("kexinit", SshMessageNumber.KexInit, 10));
        Assert.AreEqual(SendGateAdmission.Send, gate.Admit("ecdh", (SshMessageNumber)30, 10));
        Assert.AreEqual(SendGateAdmission.Send, gate.Admit("newkeys", SshMessageNumber.NewKeys, 10));
        Assert.AreEqual(0, gate.StashedCount);
    }

    [TestMethod]
    public void 关闸后其余消息被暂存而不是丢弃()
    {
        // 丢弃会让对端看到一个被截断的流，而且双方都无从察觉。
        SendGate<string> gate = NewGate();
        gate.Close();

        Assert.AreEqual(SendGateAdmission.Stashed, gate.Admit("data", SshMessageNumber.ChannelData, 100));
        Assert.AreEqual(SendGateAdmission.Stashed, gate.Admit("req", SshMessageNumber.ChannelRequest, 50));
        Assert.AreEqual(2, gate.StashedCount);
        Assert.AreEqual(150, gate.StashedBytes);
    }

    [TestMethod]
    public void 关闸期间取不出暂存的帧()
    {
        SendGate<string> gate = NewGate();
        gate.Close();
        gate.Admit("data", SshMessageNumber.ChannelData, 10);

        Assert.IsFalse(gate.TryTakeStashed(out string? frame, out _));
        Assert.IsNull(frame);
    }

    [TestMethod]
    public void 开闸后暂存的帧按原顺序流出()
    {
        // 保序是硬要求：通道数据的顺序是语义的一部分。
        SendGate<string> gate = NewGate();
        gate.Close();
        gate.Admit("1", SshMessageNumber.ChannelData, 1);
        gate.Admit("2", SshMessageNumber.ChannelData, 2);
        gate.Admit("3", SshMessageNumber.ChannelData, 3);
        gate.Open();

        List<string> order = [];
        while (gate.TryTakeStashed(out string? f, out int len))
        {
            order.Add($"{f}:{len}");
        }

        Assert.AreSequenceEqual(new[] { "1:1", "2:2", "3:3" }, order);
        Assert.AreEqual(0, gate.StashedBytes, "排空后字节计量必须归零");
    }

    [TestMethod]
    public void 暂存区未排空时新帧继续排队()
    {
        // 开闸不等于立刻恢复直通 —— 只要暂存区还有东西，新帧就得排在它们后面，
        // 否则会插队到更早的数据前面去。
        SendGate<string> gate = NewGate();
        gate.Close();
        gate.Admit("old", SshMessageNumber.ChannelData, 10);
        gate.Open();

        Assert.AreEqual(SendGateAdmission.Stashed, gate.Admit("new", SshMessageNumber.ChannelData, 10));

        Assert.IsTrue(gate.TryTakeStashed(out string? first, out _));
        Assert.AreEqual("old", first);
        Assert.IsTrue(gate.TryTakeStashed(out string? second, out _));
        Assert.AreEqual("new", second);

        // 排空之后恢复直通
        Assert.AreEqual(SendGateAdmission.Send, gate.Admit("after", SshMessageNumber.ChannelData, 10));
    }

    // ------------------------------------------------------------ 背压

    [TestMethod]
    public void 暂存区满时拒收而不是丢弃()
    {
        // 返回 StashFull 表示「这一帧没有被收下」，调用方必须等 —— 背压由此传到上游。
        SendGate<string> gate = NewGate(maxStash: 100);
        gate.Close();

        Assert.AreEqual(SendGateAdmission.Stashed, gate.Admit("a", SshMessageNumber.ChannelData, 60));
        Assert.AreEqual(SendGateAdmission.Stashed, gate.Admit("b", SshMessageNumber.ChannelData, 60));
        Assert.IsTrue(gate.IsStashFull);

        Assert.AreEqual(SendGateAdmission.StashFull, gate.Admit("c", SshMessageNumber.ChannelData, 1));
        Assert.AreEqual(2, gate.StashedCount, "被拒的帧不得进入暂存区");
    }

    [TestMethod]
    public void 暂存区满时传输层消息仍然放行()
    {
        // 这一条是死锁防线：暂存区满了正是因为重协商没完成，
        // 而完成它靠的正是传输层消息。挡掉它们，连接就再也出不来了。
        SendGate<string> gate = NewGate(maxStash: 10);
        gate.Close();
        gate.Admit("fill", SshMessageNumber.ChannelData, 20);
        Assert.IsTrue(gate.IsStashFull);

        Assert.AreEqual(SendGateAdmission.Send, gate.Admit("newkeys", SshMessageNumber.NewKeys, 5));
    }

    [TestMethod]
    public void 取出之后腾出空间()
    {
        SendGate<string> gate = NewGate(maxStash: 100);
        gate.Close();
        gate.Admit("a", SshMessageNumber.ChannelData, 100);
        Assert.IsTrue(gate.IsStashFull);

        gate.Open();
        Assert.IsTrue(gate.TryTakeStashed(out _, out int len));
        Assert.AreEqual(100, len);
        Assert.IsFalse(gate.IsStashFull);
        Assert.AreEqual(0, gate.StashedBytes);
    }

    // ------------------------------------------------------------ 幂等与中止

    [TestMethod]
    public void 重复关闸是空操作()
    {
        // 双方同时发起重协商是合法的，会导致 Close 被调用两次。
        SendGate<string> gate = NewGate();
        gate.Close();
        gate.Close();
        Assert.IsFalse(gate.IsOpen);

        gate.Open();
        Assert.IsTrue(gate.IsOpen);
        gate.Open();
        Assert.IsTrue(gate.IsOpen);
    }

    [TestMethod]
    public void 中止时交出全部暂存帧()
    {
        // 交给调用方，让它把等待这些帧的操作以异常收尾 —— 而不是静默丢掉。
        SendGate<string> gate = NewGate();
        gate.Close();
        gate.Admit("a", SshMessageNumber.ChannelData, 1);
        gate.Admit("b", SshMessageNumber.ChannelData, 2);

        IReadOnlyList<string> drained = gate.DrainForAbort();

        Assert.AreSequenceEqual(new[] { "a", "b" }, [.. drained]);
        Assert.AreEqual(0, gate.StashedCount);
        Assert.AreEqual(0, gate.StashedBytes);
    }

    [TestMethod]
    public void 中止时暂存区为空则返回空列表() => Assert.IsEmpty(NewGate().DrainForAbort());

    // ------------------------------------------------------------ 参数校验

    [TestMethod]
    public void 暂存上限必须为正()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SendGate<string>(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SendGate<string>(-1));
    }

    [TestMethod]
    public void 帧长度不能为负()
    {
        SendGate<string> gate = NewGate();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => gate.Admit("a", SshMessageNumber.ChannelData, -1));
    }
}
