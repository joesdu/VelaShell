using System.Text;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using VelaShell.Core.Ssh;

namespace VelaShell.Terminal.Tests;

[TestClass]
[TestCategory("TerminalBridge")]
public class TerminalBridgeTests
{
    private readonly ITerminalEmulator _terminal;
    private readonly IShellStreamWrapper _shellStream;

    public TerminalBridgeTests()
    {
        _terminal = Substitute.For<ITerminalEmulator>();
        _shellStream = Substitute.For<IShellStreamWrapper>();
    }

    [TestMethod]
    public void Constructor_NullTerminal_ThrowsArgumentNullException()
    {
        var act = () => new SshTerminalBridge(null!, _shellStream);
        var ex = Assert.ThrowsExactly<ArgumentNullException>(act);
        Assert.AreEqual("terminal", ex.ParamName);
    }

    [TestMethod]
    public void Constructor_NullShellStream_ThrowsArgumentNullException()
    {
        var act = () => new SshTerminalBridge(_terminal, null!);
        var ex = Assert.ThrowsExactly<ArgumentNullException>(act);
        Assert.AreEqual("shellStream", ex.ParamName);
    }

    [TestMethod]
    public void Start_CalledTwice_ThrowsInvalidOperationException()
    {
        _shellStream.CanRead.Returns(false);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Start();

        var act = () => bridge.Start();
        var ex = Assert.ThrowsExactly<InvalidOperationException>(act);
        StringAssert.Contains(ex.Message, "already started");
    }

    [TestMethod]
    public void UserInput_WritesToShellStream()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);

        var testData = Encoding.UTF8.GetBytes("hello");

        _terminal.UserInput += Raise.Event<Action<byte[]>>(testData);

        // WriteUserInputAsync is fire-and-forget, need to let it complete
        Thread.Sleep(100);

        _shellStream.Received().WriteAsync(
            testData,
            0,
            testData.Length,
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public void UserInput_FlushesAfterWrite()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);

        var testData = Encoding.UTF8.GetBytes("hello");
        _terminal.UserInput += Raise.Event<Action<byte[]>>(testData);

        Thread.Sleep(100);

        _shellStream.Received().Flush();
    }

    [TestMethod]
    public void Start_DoesNotPrimeShell_SoTheInitialPromptIsNotDuplicated()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Start();

        Thread.Sleep(100);

        // The server already emits its banner + prompt on connect; sending an extra newline
        // would produce a duplicate prompt line, so Start must not write anything.
        _shellStream.DidNotReceive().WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public void UserInput_WhenDisposed_DoesNotWriteToShellStream()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);

        var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Dispose();

        _shellStream.DidNotReceive().WriteAsync(
            Arg.Any<byte[]>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public void UserInput_WhenStreamCannotWrite_DoesNotWrite()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(false);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);

        var testData = Encoding.UTF8.GetBytes("hello");
        _terminal.UserInput += Raise.Event<Action<byte[]>>(testData);

        Thread.Sleep(100);

        _shellStream.DidNotReceive().WriteAsync(
            Arg.Any<byte[]>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public void ReadLoop_WhenCanReadFalse_ExitsImmediately()
    {
        _shellStream.CanRead.Returns(false);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Start();

        // Task.Run in Start() needs time to enter and exit the loop
        Thread.Sleep(200);

        _shellStream.DidNotReceive().ReadAsync(
            Arg.Any<byte[]>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public void ReadLoop_WhenReadReturnsZero_ExitsGracefully()
    {
        _shellStream.CanRead.Returns(true);
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Start();

        Thread.Sleep(200);

        _terminal.DidNotReceive().Feed(Arg.Any<byte[]>());
    }

    [TestMethod]
    public void ReadLoop_WhenExceptionOccurs_FiresErrorEvent()
    {
        var expectedException = new IOException("connection lost");
        Exception? capturedError = null;

        _shellStream.CanRead.Returns(true);
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(expectedException);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Error += ex => capturedError = ex;
        bridge.Start();

        Thread.Sleep(500);

        Assert.IsNotNull(capturedError);
        Assert.AreSame(expectedException, capturedError);
    }

    [TestMethod]
    public void Dispose_CancelsReadLoopAndDisposesStream()
    {
        _shellStream.CanRead.Returns(true);

        // ReadAsync blocks forever until CancellationToken fires
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var ct = callInfo.ArgAt<CancellationToken>(3);
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            });

        var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Start();

        Thread.Sleep(100);

        bridge.Dispose();

        _shellStream.Received().Dispose();
    }

    [TestMethod]
    public void ReadLoop_WhenRemoteCloses_FiresClosed()
    {
        _shellStream.CanRead.Returns(true);
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0)); // EOF => remote closed the channel

        var closed = false;
        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Closed += () => closed = true;
        bridge.Start();

        Thread.Sleep(200);

        Assert.IsTrue(closed);
    }

    [TestMethod]
    public void Dispose_DoesNotFireClosed()
    {
        _shellStream.CanRead.Returns(true);
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var ct = callInfo.ArgAt<CancellationToken>(3);
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            });

        var closed = false;
        var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Closed += () => closed = true;
        bridge.Start();

        Thread.Sleep(100);
        bridge.Dispose(); // intentional teardown must not look like a remote close

        Thread.Sleep(100);

        Assert.IsFalse(closed);
    }

    [TestMethod]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        _shellStream.CanRead.Returns(false);

        var bridge = new SshTerminalBridge(_terminal, _shellStream);

        bridge.Dispose();
        bridge.Dispose();
    }

    [TestMethod]
    public void UserInput_WriteThrowsObjectDisposed_DoesNotPropagate()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ObjectDisposedException("stream"));

        Exception? capturedError = null;
        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Error += ex => capturedError = ex;

        var testData = Encoding.UTF8.GetBytes("hello");
        _terminal.UserInput += Raise.Event<Action<byte[]>>(testData);

        Thread.Sleep(200);

        // ObjectDisposedException is swallowed per WriteUserInputAsync contract
        Assert.IsNull(capturedError);
    }

    [TestMethod]
    public void UserInput_WriteThrowsGenericException_FiresErrorEvent()
    {
        var expectedException = new InvalidOperationException("write failed");
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(expectedException);

        Exception? capturedError = null;
        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Error += ex => capturedError = ex;

        var testData = Encoding.UTF8.GetBytes("hello");
        _terminal.UserInput += Raise.Event<Action<byte[]>>(testData);

        Thread.Sleep(200);

        Assert.IsNotNull(capturedError);
        Assert.AreSame(expectedException, capturedError);
    }
}
