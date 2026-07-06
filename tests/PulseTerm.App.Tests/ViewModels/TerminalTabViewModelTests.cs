using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;
using PulseTerm.Terminal;

namespace PulseTerm.App.Tests.ViewModels;

[TestClass]
public class TerminalTabViewModelTests
{
    private readonly ITerminalEmulator _terminalEmulator;
    private readonly IShellStreamWrapper _shellStream;
    private readonly TerminalTabViewModel _vm;

    public TerminalTabViewModelTests()
    {
        _terminalEmulator = Substitute.For<ITerminalEmulator>();
        _shellStream = Substitute.For<IShellStreamWrapper>();
        _vm = new TerminalTabViewModel(_terminalEmulator, _shellStream);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Constructor_SetsDefaultTitle()
    {
        Assert.IsFalse(string.IsNullOrEmpty(_vm.Title));
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Constructor_SetsDefaultConnectionStatus_ToDisconnected()
    {
        Assert.AreEqual(SessionStatus.Disconnected, _vm.ConnectionStatus);
        Assert.IsFalse(_vm.IsConnected);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void ConnectionStatus_Connected_SetsIsConnectedTrue()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;

        Assert.IsTrue(_vm.IsConnected);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void ConnectionStatus_Disconnected_SetsIsConnectedFalse()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;
        _vm.ConnectionStatus = SessionStatus.Disconnected;

        Assert.IsFalse(_vm.IsConnected);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void IncrementReconnectAttempt_IncrementsCounter()
    {
        Assert.AreEqual(0, _vm.ReconnectAttempts);

        _vm.IncrementReconnectAttempt();
        Assert.AreEqual(1, _vm.ReconnectAttempts);

        _vm.IncrementReconnectAttempt();
        Assert.AreEqual(2, _vm.ReconnectAttempts);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void ResetReconnectAttempts_ResetsToZero()
    {
        _vm.IncrementReconnectAttempt();
        _vm.IncrementReconnectAttempt();
        Assert.AreEqual(2, _vm.ReconnectAttempts);

        _vm.ResetReconnectAttempts();

        Assert.AreEqual(0, _vm.ReconnectAttempts);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void CanReconnect_UnderMax_ReturnsTrue()
    {
        Assert.IsTrue(_vm.CanReconnect);

        _vm.IncrementReconnectAttempt();
        Assert.IsTrue(_vm.CanReconnect);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void CanReconnect_AtMax_ReturnsFalse()
    {
        for (var i = 0; i < _vm.MaxReconnectAttempts; i++)
            _vm.IncrementReconnectAttempt();

        Assert.IsFalse(_vm.CanReconnect);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Dispose_DisposesBridgeAndTerminalEmulator()
    {
        _vm.Dispose();

        _terminalEmulator.Received(1).Dispose();
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Dispose_CalledTwice_OnlyDisposesOnce()
    {
        _vm.Dispose();
        _vm.Dispose();

        _terminalEmulator.Received(1).Dispose();
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Constructor_InitializesAllCommands()
    {
        Assert.IsNotNull(_vm.SearchCommand);
        Assert.IsNotNull(_vm.CopyCommand);
        Assert.IsNotNull(_vm.SplitCommand);
        Assert.IsNotNull(_vm.ToggleBroadcastCommand);
        Assert.IsNotNull(_vm.OpenTunnelCommand);
        Assert.IsNotNull(_vm.OpenQuickCommandsCommand);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Constructor_StoresTerminalEmulatorAndShellStream()
    {
        Assert.AreSame(_terminalEmulator, _vm.TerminalEmulator);
        Assert.AreSame(_shellStream, _vm.ShellStream);
        Assert.IsNotNull(_vm.Bridge);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Id_IsUniquePerInstance()
    {
        var vm2 = new TerminalTabViewModel(
            Substitute.For<ITerminalEmulator>(),
            Substitute.For<IShellStreamWrapper>());

        Assert.AreNotEqual(vm2.Id, _vm.Id);
        Assert.AreNotEqual(Guid.Empty, _vm.Id);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void ConnectingConstructor_HasNoTransportYet()
    {
        var vm = new TerminalTabViewModel(_terminalEmulator);

        Assert.IsNull(vm.Bridge);
        Assert.IsNull(vm.ShellStream);
        Assert.AreEqual(SessionStatus.Disconnected, vm.ConnectionStatus);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Start_WithoutTransport_IsNoOp()
    {
        var vm = new TerminalTabViewModel(_terminalEmulator);

        vm.Start(); // must not throw with no transport attached

        Assert.IsNull(vm.Bridge);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void AttachTransport_WiresBridgeAndShellStream()
    {
        var vm = new TerminalTabViewModel(_terminalEmulator);
        var stream = Substitute.For<IShellStreamWrapper>();

        vm.AttachTransport(stream);

        Assert.IsNotNull(vm.Bridge);
        Assert.AreSame(stream, vm.ShellStream);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void AttachTransport_Null_Throws()
    {
        var vm = new TerminalTabViewModel(_terminalEmulator);

        Assert.ThrowsExactly<ArgumentNullException>(() => vm.AttachTransport(null!));
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void AttachTransport_AfterDisposed_Throws()
    {
        var vm = new TerminalTabViewModel(_terminalEmulator);
        vm.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(
            () => vm.AttachTransport(Substitute.For<IShellStreamWrapper>()));
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void DetachTransport_ClearsBridgeAndShellStream()
    {
        _vm.DetachTransport();

        Assert.IsNull(_vm.Bridge);
        Assert.IsNull(_vm.ShellStream);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void MarkDisconnected_SetsStatus_AndRaisesEvent()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;
        var raised = false;
        _vm.Disconnected += (_, _) => raised = true;

        _vm.MarkDisconnected();

        Assert.AreEqual(SessionStatus.Disconnected, _vm.ConnectionStatus);
        Assert.IsFalse(_vm.IsConnected);
        Assert.IsTrue(raised);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void MarkDisconnected_WhenAlreadyDisconnected_DoesNotRaise()
    {
        var raised = false;
        _vm.Disconnected += (_, _) => raised = true;

        _vm.MarkDisconnected(); // already Disconnected from construction

        Assert.IsFalse(raised);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void RequestReconnect_OnlyFiresWhenDisconnected()
    {
        var count = 0;
        _vm.ReconnectRequested += (_, _) => count++;

        _vm.ConnectionStatus = SessionStatus.Connected;
        _vm.RequestReconnect(); // must be ignored while connected
        Assert.AreEqual(0, count);

        _vm.ConnectionStatus = SessionStatus.Disconnected;
        _vm.RequestReconnect();
        Assert.AreEqual(1, count);
    }
}
