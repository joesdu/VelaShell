using NSubstitute;
using Renci.SshNet;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Core.Tests.Ssh;

[TestClass]
[TestCategory("SshConnection")]
public class SshClientWrapperTests
{
    [TestMethod]
    public void IsConnected_WhenClientConnected_ReturnsTrue()
    {
        SshClient? mockClient = Substitute.For<SshClient>(Substitute.For<ConnectionInfo>("localhost", "user", new PasswordAuthenticationMethod("user", "pass")));
        mockClient.IsConnected.Returns(true);
        var wrapper = new SshClientWrapper(mockClient);
        Assert.IsTrue(wrapper.IsConnected);
    }

    [TestMethod]
    public void Disconnect_CallsClientDisconnect()
    {
        SshClient? mockClient = Substitute.For<SshClient>(Substitute.For<ConnectionInfo>("localhost", "user", new PasswordAuthenticationMethod("user", "pass")));
        var wrapper = new SshClientWrapper(mockClient);
        wrapper.Disconnect();
        mockClient.Received(1).Disconnect();
    }

    [TestMethod]
    public void Dispose_DisposesClient()
    {
        SshClient? mockClient = Substitute.For<SshClient>(Substitute.For<ConnectionInfo>("localhost", "user", new PasswordAuthenticationMethod("user", "pass")));
        var wrapper = new SshClientWrapper(mockClient);
        wrapper.Dispose();
        mockClient.Received(1).Dispose();
    }
}
