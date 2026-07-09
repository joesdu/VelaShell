using NSubstitute;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;
using PulseTerm.Infrastructure.Ssh;

namespace PulseTerm.Core.Tests.Ssh;

[TestClass]
[TestCategory("SshConnection")]
public class SshConnectionServiceTests
{
    [TestMethod]
    public async Task ConnectAsync_WithPassword_ReturnsConnectedSession()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.IsConnected.Returns(true);

        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };

        var service = new SshConnectionService(_ => mockClientWrapper);
        var session = await service.ConnectAsync(connectionInfo);

        Assert.IsNotNull(session);
        Assert.AreEqual(SessionStatus.Connected, session.Status);
        Assert.AreEqual(connectionInfo, session.ConnectionInfo);
        await mockClientWrapper.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ConnectAsync_WithPrivateKey_ReturnsConnectedSession()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.IsConnected.Returns(true);

        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/path/to/key"
        };

        var service = new SshConnectionService(_ => mockClientWrapper);
        var session = await service.ConnectAsync(connectionInfo);

        Assert.IsNotNull(session);
        Assert.AreEqual(SessionStatus.Connected, session.Status);
        await mockClientWrapper.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ConnectAsync_AuthFailure_ThrowsSshAuthenticationException()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
            .Do(_ => throw new Renci.SshNet.Common.SshAuthenticationException("Authentication failed"));

        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "baduser",
            AuthMethod = AuthMethod.Password,
            Password = "wrongpass"
        };

        var service = new SshConnectionService(_ => mockClientWrapper);

        await Assert.ThrowsExactlyAsync<Renci.SshNet.Common.SshAuthenticationException>(
            async () => await service.ConnectAsync(connectionInfo));
    }

    [TestMethod]
    public async Task ConnectAsync_ConnectionRefused_ThrowsSshConnectionException()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
            .Do(_ => throw new Renci.SshNet.Common.SshConnectionException("Connection refused"));

        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 9999,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };

        var service = new SshConnectionService(_ => mockClientWrapper);

        await Assert.ThrowsExactlyAsync<Renci.SshNet.Common.SshConnectionException>(
            async () => await service.ConnectAsync(connectionInfo));
    }

    [TestMethod]
    public async Task DisconnectAsync_ExistingSession_DisconnectsSuccessfully()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.IsConnected.Returns(true);

        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };

        var service = new SshConnectionService(_ => mockClientWrapper);
        var session = await service.ConnectAsync(connectionInfo);

        await service.DisconnectAsync(session.SessionId);

        mockClientWrapper.Received(1).Disconnect();
        Assert.AreEqual(SessionStatus.Disconnected, session.Status);
    }

    [TestMethod]
    public async Task ConnectAsync_ConcurrentSessions_BothSucceed()
    {
        var mockClient1 = Substitute.For<ISshClientWrapper>();
        mockClient1.IsConnected.Returns(true);

        var mockClient2 = Substitute.For<ISshClientWrapper>();
        mockClient2.IsConnected.Returns(true);

        var clients = new Queue<ISshClientWrapper>(new[] { mockClient1, mockClient2 });
        var service = new SshConnectionService(_ => clients.Dequeue());

        var connectionInfo1 = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "user1",
            AuthMethod = AuthMethod.Password,
            Password = "pass1"
        };

        var connectionInfo2 = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "user2",
            AuthMethod = AuthMethod.Password,
            Password = "pass2"
        };

        var session1 = await service.ConnectAsync(connectionInfo1);
        var session2 = await service.ConnectAsync(connectionInfo2);

        Assert.IsNotNull(session1);
        Assert.IsNotNull(session2);
        Assert.AreNotEqual(session2.SessionId, session1.SessionId);
        Assert.AreEqual(2, service.Sessions.Count);
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public async Task ConnectAsync_SlowConnection_DoesNotBlockOtherConnections()
    {
        // 第一条连接的握手阻塞在 TCS 上(模拟高延迟/不可达主机);第二条应立即连上,
        // 不必等第一条超时。验证已移除全局串行锁(回归:此前会串行阻塞)。
        var gate = new TaskCompletionSource();

        var slowClient = Substitute.For<ISshClientWrapper>();
        slowClient.IsConnected.Returns(true);
        slowClient.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ => gate.Task);

        var fastClient = Substitute.For<ISshClientWrapper>();
        fastClient.IsConnected.Returns(true);
        fastClient.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var clients = new Queue<ISshClientWrapper>([slowClient, fastClient]);
        var service = new SshConnectionService(_ => clients.Dequeue());

        var info = new ConnectionInfo
        {
            Host = "localhost",
            Port = 22,
            Username = "u",
            AuthMethod = AuthMethod.Password,
            Password = "p",
        };

        var slow = service.ConnectAsync(info);
        var fast = await service.ConnectAsync(info).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(SessionStatus.Connected, fast.Status);
        Assert.IsFalse(slow.IsCompleted, "慢连接仍应在进行中,不应因串行而完成/被跳过。");

        gate.SetResult();
        var slowSession = await slow.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(SessionStatus.Connected, slowSession.Status);
    }

    [TestMethod]
    public async Task ConnectAsync_FactoryReceivesConnectionInfo()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.IsConnected.Returns(true);

        ConnectionInfo? receivedInfo = null;
        var service = new SshConnectionService(info =>
        {
            receivedInfo = info;
            return mockClientWrapper;
        });

        var connectionInfo = new ConnectionInfo
        {
            Host = "myhost.example.com",
            Port = 2222,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };

        await service.ConnectAsync(connectionInfo);

        Assert.AreEqual(connectionInfo, receivedInfo);
    }

    [TestMethod]
    public async Task GetClient_AfterConnect_ReturnsClient()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.IsConnected.Returns(true);

        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };

        var service = new SshConnectionService(_ => mockClientWrapper);
        var session = await service.ConnectAsync(connectionInfo);

        var client = service.GetClient(session.SessionId);
        Assert.AreSame(mockClientWrapper, client);
    }

    [TestMethod]
    public void GetClient_UnknownSessionId_ReturnsNull()
    {
        var service = new SshConnectionService(_ => Substitute.For<ISshClientWrapper>());

        var client = service.GetClient(Guid.NewGuid());
        Assert.IsNull(client);
    }

    [TestMethod]
    public async Task ConnectAsync_Failure_DisposesClient()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
            .Do(_ => throw new Renci.SshNet.Common.SshConnectionException("Connection refused"));

        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 9999,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };

        var service = new SshConnectionService(_ => mockClientWrapper);

        await Assert.ThrowsExactlyAsync<Renci.SshNet.Common.SshConnectionException>(
            async () => await service.ConnectAsync(connectionInfo));

        mockClientWrapper.Received(1).Dispose();
    }

    [TestMethod]
    public async Task ConnectAsync_Failure_RemovesSessionFromList()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
            .Do(_ => throw new Renci.SshNet.Common.SshConnectionException("Connection refused"));

        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 9999,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };

        var service = new SshConnectionService(_ => mockClientWrapper);

        await Assert.ThrowsExactlyAsync<Renci.SshNet.Common.SshConnectionException>(
            async () => await service.ConnectAsync(connectionInfo));

        Assert.AreEqual(0, service.Sessions.Count);
    }

    [TestMethod]
    public async Task DisposeAsync_DisconnectsAndDisposesAllClients()
    {
        var mockClient1 = Substitute.For<ISshClientWrapper>();
        mockClient1.IsConnected.Returns(true);

        var mockClient2 = Substitute.For<ISshClientWrapper>();
        mockClient2.IsConnected.Returns(true);

        var clients = new Queue<ISshClientWrapper>(new[] { mockClient1, mockClient2 });
        var service = new SshConnectionService(_ => clients.Dequeue());

        var connectionInfo1 = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "user1",
            AuthMethod = AuthMethod.Password,
            Password = "pass1"
        };

        var connectionInfo2 = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "user2",
            AuthMethod = AuthMethod.Password,
            Password = "pass2"
        };

        await service.ConnectAsync(connectionInfo1);
        await service.ConnectAsync(connectionInfo2);

        await service.DisposeAsync();

        mockClient1.Received(1).Disconnect();
        mockClient1.Received(1).Dispose();
        mockClient2.Received(1).Disconnect();
        mockClient2.Received(1).Dispose();
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public async Task ConnectAsync_CancellationToken_ThrowsTimeoutException()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
            .Do(callInfo =>
            {
                throw new OperationCanceledException("The operation was canceled.");
            });

        var connectionInfo = new ConnectionInfo
        {
            Host = "slow.example.com",
            Port = 22,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };

        var service = new SshConnectionService(_ => mockClientWrapper);

        var ex = await Assert.ThrowsExactlyAsync<TimeoutException>(
            async () => await service.ConnectAsync(connectionInfo));

        StringAssert.Contains(ex.Message, "slow.example.com");
        StringAssert.Contains(ex.Message, "timed out");
        Assert.AreEqual(0, service.Sessions.Count);
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public async Task RapidConnectDisconnect_NoRaceCondition()
    {
        var mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.IsConnected.Returns(true);

        var service = new SshConnectionService(_ => mockClient);

        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };

        var session = await service.ConnectAsync(connectionInfo);
        await service.DisconnectAsync(session.SessionId);

        Assert.AreEqual(SessionStatus.Disconnected, session.Status);
        Assert.IsNull(service.GetClient(session.SessionId));
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public async Task DisconnectAsync_AlreadyDisconnected_DoesNotThrow()
    {
        var mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.IsConnected.Returns(true);

        var service = new SshConnectionService(_ => mockClient);

        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };

        var session = await service.ConnectAsync(connectionInfo);
        await service.DisconnectAsync(session.SessionId);
        await service.DisconnectAsync(session.SessionId);

        Assert.AreEqual(SessionStatus.Disconnected, session.Status);
    }
}
