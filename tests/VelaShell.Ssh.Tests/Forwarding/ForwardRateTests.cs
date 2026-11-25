// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §5.1

using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Ssh.Tests.Forwarding;

/// <summary>吞吐的滑动窗口与令牌桶限速，用手动拨的时钟算得一清二楚。</summary>
[TestClass]
[TestCategory("Forwarding")]
public sealed class ForwardRateTests
{
    /// <summary>报的是最近三个整秒的平均，不含正在走的这一秒；窗口外的不算。</summary>
    [TestMethod]
    public void 吞吐取最近三个整秒的平均()
    {
        ManualTimeProvider time = new();
        ThroughputMeter meter = new(time);

        meter.Record(3000);                         // 第 0 秒
        time.Advance(TimeSpan.FromSeconds(1));
        meter.Record(6000);                         // 第 1 秒
        Assert.AreEqual(1000, meter.PerSecond, "正在走的第 1 秒不算：(3000) / 3");

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(3000, meter.PerSecond, "(3000 + 6000) / 3");

        time.Advance(TimeSpan.FromSeconds(2));      // 第 4 秒：窗口是 1–3 秒
        Assert.AreEqual(2000, meter.PerSecond, "第 0 秒滑出窗口：6000 / 3");

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, meter.PerSecond, "早就没动静了");
    }

    /// <summary>定时器回调来晚了，等在同一个桶上的连接合起来也只能突发一秒的量。</summary>
    [TestMethod]
    public async Task 共用令牌桶的连接在延迟唤醒后仍按速率放行()
    {
        ManualTimeProvider time = new();
        ByteRateLimiter limiter = new(1000, time);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1000, await limiter.AcquireAsync(1000, timeout.Token));

        Task<int> first = limiter.AcquireAsync(5000, timeout.Token).AsTask();
        Task<int> second = limiter.AcquireAsync(500, timeout.Token).AsTask();
        time.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual(1000, await first.WaitAsync(timeout.Token));
        await time.WaitUntilArmedAsync(TimeSpan.FromMilliseconds(500), timeout.Token);
        Assert.IsFalse(second.IsCompleted, "即使两个请求都等了很久，也不能一起透支这一秒的额度");
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.AreEqual(500, await second.WaitAsync(timeout.Token));
    }

    [TestMethod]
    public async Task 已取消的请求保留整桶额度()
    {
        ManualTimeProvider time = new();
        ByteRateLimiter limiter = new(1000, time);
        await Assert.ThrowsAsync<OperationCanceledException>(() => limiter.AcquireAsync(1000, new CancellationToken(true)).AsTask());
        ValueTask<int> next = limiter.AcquireAsync(1000, CancellationToken.None);
        Assert.IsTrue(next.IsCompletedSuccessfully);
        Assert.AreEqual(1000, await next);
    }

    [TestMethod]
    public async Task 取消等待中的连接后_其他连接可立即用已补的额度()
    {
        ManualTimeProvider time = new();
        ByteRateLimiter limiter = new(1000, time);
        using CancellationTokenSource head = new();
        using CancellationTokenSource middle = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1000, await limiter.AcquireAsync(1000, timeout.Token));
        Task<int> waiting = limiter.AcquireAsync(5000, head.Token).AsTask();
        Task<int> queued = limiter.AcquireAsync(1000, middle.Token).AsTask();
        Task<int> survivor = limiter.AcquireAsync(250, timeout.Token).AsTask();

        time.Advance(TimeSpan.FromMilliseconds(250));
        await middle.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued.WaitAsync(timeout.Token));
        Assert.IsFalse(survivor.IsCompleted);
        await head.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting.WaitAsync(timeout.Token));
        Assert.AreEqual(250, await survivor.WaitAsync(timeout.Token), "只拨过四分之一秒，这 250 字节都属于仍在搬的连接");

        Task<int> next = limiter.AcquireAsync(250, timeout.Token).AsTask();
        Assert.IsFalse(next.IsCompleted, "已经放行的 250 字节应当扣掉");
        time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.AreEqual(250, await next.WaitAsync(timeout.Token));
    }

    /// <summary>
    /// 要得再多，一次也只放行一秒的额度，剩下的下一次再要 —— 不是整块记账、整块等完再整块放行
    /// （那样单次突发就是好几秒的额度）。要得比额度少时照要的给。
    /// </summary>
    [TestMethod]
    public async Task 一次至多放行一秒的额度()
    {
        ManualTimeProvider time = new();
        ByteRateLimiter limiter = new(1000, time);

        ValueTask<int> first = limiter.AcquireAsync(5000, CancellationToken.None);
        Assert.IsTrue(first.IsCompletedSuccessfully, "额度在手：当场放行");
        Assert.AreEqual(1000, await first, "要五秒的量，只放一秒的");

        Task<int> second = limiter.AcquireAsync(5000, CancellationToken.None).AsTask();
        Assert.IsFalse(second.IsCompleted, "额度用完了：欠着、等一秒");
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1000, await second.WaitAsync(TimeSpan.FromSeconds(10)));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual(300, await limiter.AcquireAsync(300, CancellationToken.None), "比额度少的照要的给");
    }

    /// <summary>限速必须为正；不限给 null。</summary>
    [TestMethod]
    public void 限速参数不为正当场报()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LocalPortForwardOptions { MaxBytesPerSecond = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RemotePortForwardOptions { MaxBytesPerSecond = -1 });
        Assert.IsNull(new LocalPortForwardOptions { MaxBytesPerSecond = null }.MaxBytesPerSecond);
    }
}
