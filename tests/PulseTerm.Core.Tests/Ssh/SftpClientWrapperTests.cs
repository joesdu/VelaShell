using NSubstitute;
using PulseTerm.Core.Ssh;
using PulseTerm.Infrastructure.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace PulseTerm.Core.Tests.Ssh;

[TestClass]
[TestCategory("SshConnection")]
public class SftpClientWrapperTests
{
    [TestMethod]
    public void IsConnected_WhenClientConnected_ReturnsTrue()
    {
        var mockClient = Substitute.For<SftpClient>(
            Substitute.For<ConnectionInfo>("localhost", "user", new PasswordAuthenticationMethod("user", "pass")));
        mockClient.IsConnected.Returns(true);

        var wrapper = new SftpClientWrapper(mockClient);

        Assert.IsTrue(wrapper.IsConnected);
    }

    [TestMethod]
    public void Disconnect_CallsClientDisconnect()
    {
        var mockClient = Substitute.For<SftpClient>(
            Substitute.For<ConnectionInfo>("localhost", "user", new PasswordAuthenticationMethod("user", "pass")));

        var wrapper = new SftpClientWrapper(mockClient);
        wrapper.Disconnect();

        mockClient.Received(1).Disconnect();
    }

    [TestMethod]
    public void Dispose_DisposesClient()
    {
        var mockClient = Substitute.For<SftpClient>(
            Substitute.For<ConnectionInfo>("localhost", "user", new PasswordAuthenticationMethod("user", "pass")));

        var wrapper = new SftpClientWrapper(mockClient);
        wrapper.Dispose();

        mockClient.Received(1).Dispose();
    }
}
