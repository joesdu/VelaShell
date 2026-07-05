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
}
