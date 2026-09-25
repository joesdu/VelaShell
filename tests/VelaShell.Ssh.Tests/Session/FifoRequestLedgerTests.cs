// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/05-connection.md §5.1、§6.4;velashell-docs/zh/ssh/spec/07-forwarding.md §4.2

using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Tests.Session;

[TestClass]
[TestCategory("Session")]
public sealed class FifoRequestLedgerTests
{
    [TestMethod]
    public void 应答按登记顺序交付()
    {
        FifoRequestLedger<int> ledger = new();
        Task<int> first = ledger.Register();
        Task<int> second = ledger.Register();

        Assert.IsTrue(ledger.TryComplete(1));
        Assert.IsTrue(ledger.TryComplete(2));
        Assert.IsFalse(ledger.TryComplete(3), "没有人在等的应答就是 FIFO 失步");

        Assert.AreEqual(1, first.Result);
        Assert.AreEqual(2, second.Result);
    }

    [TestMethod]
    public void 内联回调在交付应答的线程上当场执行()
    {
        // 远程转发请求端口 0 时，服务端回完应答紧接着就可能开回连，而认领回连要用应答里的端口。
        // 回调必须在 TryComplete 返回之前、在交付应答的那个线程（接收循环）上跑完 ——
        // 等任务的续体去记的话，续体在线程池上，接收循环那时可能已经在分发那条回连了。
        FifoRequestLedger<int> ledger = new();
        int seen = 0;
        int seenOnThread = -1;
        Task<int> reply = ledger.Register(result =>
        {
            seen = result;
            seenOnThread = Environment.CurrentManagedThreadId;
        });

        Assert.IsTrue(ledger.TryComplete(42));

        Assert.AreEqual(42, seen, "TryComplete 返回时回调已经跑完了");
        Assert.AreEqual(Environment.CurrentManagedThreadId, seenOnThread);
        Assert.AreEqual(42, reply.Result);
    }

    [TestMethod]
    public void 关账时回调拿到关账的结果且回调出错不连累等待者()
    {
        FifoRequestLedger<int> ledger = new();
        int seen = 0;
        Task<int> throwing = ledger.Register(_ => throw new InvalidOperationException("登记方的 bug"));
        Task<int> watching = ledger.Register(result => seen = result);

        ledger.Close(-1);

        Assert.AreEqual(-1, throwing.Result, "回调抛了，等应答的人照样拿到结果，不会永远挂着");
        Assert.AreEqual(-1, watching.Result);
        Assert.AreEqual(-1, seen);

        // 关账之后登记的，当场拿到关账结果，回调也当场跑。
        int late = 0;
        Assert.AreEqual(-1, ledger.Register(result => late = result).Result);
        Assert.AreEqual(-1, late);
    }
}
