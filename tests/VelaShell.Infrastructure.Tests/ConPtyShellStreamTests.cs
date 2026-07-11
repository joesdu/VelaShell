using System.Text;
using VelaShell.Infrastructure.Pty;

namespace VelaShell.Infrastructure.Tests;

[TestClass]
[TestCategory("ConPty")]
public class ConPtyShellStreamTests
{
    /// <summary>端到端冒烟:拉起 cmd.exe,验证可在无头环境确定的契约 ——
    /// (1) conhost 握手序列经输出管道到达(管道/伪控制台挂载正常);
    /// (2) 写入 exit 经输入管道送达子进程(输入通路正常);
    /// (3) 子进程退出后读端归一化为 EOF(生命周期正常)。
    /// 注意:本机(Windows 预览版 conhost)对无头测试进程不出屏幕帧 —— 帧渲染依赖
    /// 真实终端侧的完整 VT 协商,由 GUI 实测覆盖,不在单测断言。</summary>
    [TestMethod]
    [Timeout(30000)]
    public async Task ConPty_SpawnsShell_HandshakesAndSignalsEof()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("ConPTY 仅在 Windows 上可用。");
            return;
        }

        using var stream = ConPtyShellStream.Start(
            "cmd.exe", workingDirectory: null, columns: 80, rows: 25);

        var collected = new StringBuilder();
        byte[] buffer = new byte[4096];
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        bool sawEof = false;
        bool sentExit = false;

        while (DateTime.UtcNow < deadline)
        {
            Task<int> readTask = stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
            if (await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(20))) != readTask)
                break;
            int read = await readTask;
            if (read == 0)
            {
                sawEof = true;
                break; // 进程退出 → EOF
            }
            collected.Append(Encoding.UTF8.GetString(buffer, 0, read));

            if (!sentExit && collected.Length > 0)
            {
                sentExit = true;
                byte[] exit = Encoding.ASCII.GetBytes("exit\r");
                await stream.WriteAsync(exit, 0, exit.Length, CancellationToken.None);
            }
        }

        StringAssert.Contains(collected.ToString(), "\x1b[", "应收到 conhost 的 VT 握手序列。");
        Assert.IsTrue(sentExit, "应收到过输出并回写 exit。");
        Assert.IsTrue(sawEof, "exit 后子进程退出,读端应给出 EOF。");
    }

    [TestMethod]
    public void ConPty_Dispose_KillsProcess_AndIsIdempotent()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("ConPTY 仅在 Windows 上可用。");
            return;
        }

        var stream = ConPtyShellStream.Start("cmd.exe", workingDirectory: null, columns: 80, rows: 25);
        Assert.IsTrue(stream.CanRead);

        stream.Dispose();
        stream.Dispose(); // 幂等

        Assert.IsFalse(stream.CanRead);
        Assert.IsFalse(stream.CanWrite);
    }
}
