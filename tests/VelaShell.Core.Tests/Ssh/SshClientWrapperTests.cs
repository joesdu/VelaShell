using NSubstitute;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace VelaShell.Core.Tests.Ssh;

[TestClass]
[TestCategory("SshConnection")]
public class SshClientWrapperTests
{
    [TestMethod]
    public void IsConnected_WhenClientConnected_ReturnsTrue()
    {
        var mockClient = Substitute.For<SshClient>(
            Substitute.For<ConnectionInfo>("localhost", "user", new PasswordAuthenticationMethod("user", "pass")));
        mockClient.IsConnected.Returns(true);

        var wrapper = new SshClientWrapper(mockClient);

        Assert.IsTrue(wrapper.IsConnected);
    }

    [TestMethod]
    public void Disconnect_CallsClientDisconnect()
    {
        var mockClient = Substitute.For<SshClient>(
            Substitute.For<ConnectionInfo>("localhost", "user", new PasswordAuthenticationMethod("user", "pass")));

        var wrapper = new SshClientWrapper(mockClient);
        wrapper.Disconnect();

        mockClient.Received(1).Disconnect();
    }

    [TestMethod]
    public void Dispose_DisposesClient()
    {
        var mockClient = Substitute.For<SshClient>(
            Substitute.For<ConnectionInfo>("localhost", "user", new PasswordAuthenticationMethod("user", "pass")));

        var wrapper = new SshClientWrapper(mockClient);
        wrapper.Dispose();

        mockClient.Received(1).Dispose();
    }
}
