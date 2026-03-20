using System.Reactive.Linq;
using FluentAssertions;
using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Models;
using PulseTerm.Presentation.Services;

namespace PulseTerm.App.Tests.ViewModels;

public sealed class ConnectionProfileViewModelTests
{
    [Fact]
    public async Task SaveCommand_UsesWorkflowServiceAndReturnsSavedProfile()
    {
        var workflow = Substitute.For<IConnectionWorkflowService>();
        var expected = new SessionProfile
        {
            Name = "prod",
            Host = "prod.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret"
        };

        workflow.SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var vm = CreateValidViewModel(workflow);

        var result = await vm.SaveCommand.Execute().FirstAsync();

        result.Should().BeSameAs(expected);
        await workflow.Received(1).SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestConnectionCommand_StoresSuccessState()
    {
        var workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
            .Returns(new ConnectionTestResult(true));

        var vm = CreateValidViewModel(workflow);

        await vm.TestConnectionCommand.Execute().FirstAsync();

        vm.LastTestSucceeded.Should().BeTrue();
        vm.ErrorMessage.Should().BeNull();
    }

    private static ConnectionProfileViewModel CreateValidViewModel(IConnectionWorkflowService workflow)
    {
        return new ConnectionProfileViewModel(connectionWorkflowService: workflow)
        {
            Name = "prod",
            Host = "prod.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret"
        };
    }
}
