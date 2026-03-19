using FluentAssertions;
using PulseTerm.Core.Resources;
using PulseTerm.Presentation.ViewModels;

namespace PulseTerm.Presentation.Tests.ViewModels;

public sealed class StatusBarViewModelTests
{
    [Fact]
    public void Constructor_SetsDefaultValues()
    {
        var vm = new StatusBarViewModel();

        vm.StatusText.Should().Be(Strings.Ready);
        vm.ConnectionInfo.Should().BeEmpty();
        vm.Status.Should().Be(Strings.Disconnected);
        vm.Latency.Should().BeEmpty();
        vm.TerminalType.Should().Be("xterm-256color");
        vm.WindowSize.Should().Be("80×24");
        vm.Encoding.Should().Be("UTF-8");
        vm.Uptime.Should().BeEmpty();
        vm.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void SetStatus_Connected_UpdatesIsConnected()
    {
        var vm = new StatusBarViewModel();

        vm.Status = Strings.Connected;

        vm.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task StartUptimeTimer_UpdatesUptimeProperty()
    {
        var vm = new StatusBarViewModel();

        vm.StartUptimeTimer();
        await Task.Delay(1500);

        vm.Uptime.Should().NotBeEmpty();
        vm.StopUptimeTimer();
    }
}
