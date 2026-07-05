using System.Reactive.Linq;
using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Models;
using PulseTerm.Presentation.Services;

namespace PulseTerm.App.Tests.ViewModels;

[TestClass]
public sealed class ConnectionProfileViewModelTests
{
    [TestMethod]
    [DataRow("pä中文ss123", "pss123")]
    [DataRow("secret!", "secret!")]
    [DataRow("密码", "")]
    public void Password_StripsNonAsciiCharacters(string input, string expected)
    {
        var vm = new ConnectionProfileViewModel { Password = input };
        Assert.AreEqual(expected, vm.Password);
    }

    [TestMethod]
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

        Assert.AreSame(expected, result);
        await workflow.Received(1).SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task TestConnectionCommand_StoresSuccessState()
    {
        var workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
            .Returns(new ConnectionTestResult(true));

        var vm = CreateValidViewModel(workflow);

        await vm.TestConnectionCommand.Execute().FirstAsync();

        Assert.IsTrue(vm.LastTestSucceeded == true);
        Assert.IsNull(vm.ErrorMessage);
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
