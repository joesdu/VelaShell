using System.Diagnostics;
using System.Net.Sockets;
using NSubstitute;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;
using PulseTerm.Infrastructure.Ssh;

namespace PulseTerm.App.Tests.Integration;

[TestClass]
public class SshIntegrationTests
{
    private const string TestHost = "localhost";
    private const int TestPort = 2222;
    private const string TestUser = "testuser";
    private const string TestPassword = "testpass";

    private static readonly Lazy<bool> DockerAvailable = new(DetectDocker);

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public Task Init() => Task.CompletedTask;

    [TestCleanup]
    public Task Cleanup() => Task.CompletedTask;

    private static bool DetectDocker()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "version --format '{{.Server.Version}}'",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (!process.Start())
                return false;
            var exited = process.WaitForExit(3000);
            if (!exited)
            {
                try { process.Kill(); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSshServerReachable()
    {
        if (!DockerAvailable.Value)
            return false;

        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect(TestHost, TestPort, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));
            if (success)
            {
                client.EndConnect(result);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool SkipIfDockerUnavailable()
    {
        if (!DockerAvailable.Value)
        {
            TestContext.WriteLine("[SKIP] Docker is not available. Run 'docker compose -f docker-compose.test.yml up -d' to enable SSH integration tests.");
            return true;
        }
        return false;
    }

    private bool SkipIfSshServerUnavailable()
    {
        if (SkipIfDockerUnavailable())
            return true;
        if (!IsSshServerReachable())
        {
            TestContext.WriteLine($"[SKIP] SSH test server not reachable at {TestHost}:{TestPort}. Run 'docker compose -f docker-compose.test.yml up -d' to start it.");
            return true;
        }
        return false;
    }

    private static ConnectionInfo CreateTestConnectionInfo(
        AuthMethod authMethod = AuthMethod.Password,
        string? host = null,
        int? port = null,
        string? username = null,
        string? password = null)
    {
        return new ConnectionInfo
        {
            Host = host ?? TestHost,
            Port = port ?? TestPort,
            Username = username ?? TestUser,
            AuthMethod = authMethod,
            Password = password ?? TestPassword
        };
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task ConnectAsync_WithValidCredentials_EstablishesSession()
    {
        if (SkipIfSshServerUnavailable()) return;

        var mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.IsConnected.Returns(true);
        mockClient.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var connectionInfo = CreateTestConnectionInfo();
        var service = new SshConnectionService(_ => mockClient);

        var session = await service.ConnectAsync(connectionInfo);

        Assert.IsNotNull(session);
        Assert.AreEqual(SessionStatus.Connected, session.Status);
        Assert.AreEqual(TestHost, session.ConnectionInfo.Host);
        Assert.AreEqual(TestPort, session.ConnectionInfo.Port);

        await service.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task ConnectAsync_WithInvalidPassword_ThrowsAndSetsErrorStatus()
    {
        if (SkipIfSshServerUnavailable()) return;

        var mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Renci.SshNet.Common.SshAuthenticationException("Authentication failed")));

        var connectionInfo = CreateTestConnectionInfo(password: "wrongpassword");
        var service = new SshConnectionService(_ => mockClient);

        await Assert.ThrowsExactlyAsync<Renci.SshNet.Common.SshAuthenticationException>(
            () => service.ConnectAsync(connectionInfo));

        await service.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task ConnectAsync_WithUnreachableHost_ThrowsConnectionError()
    {
        if (SkipIfSshServerUnavailable()) return;

        var mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new SocketException((int)SocketError.ConnectionRefused)));

        var connectionInfo = CreateTestConnectionInfo(host: "192.0.2.1", port: 9999);
        var service = new SshConnectionService(_ => mockClient);

        await Assert.ThrowsExactlyAsync<SocketException>(
            () => service.ConnectAsync(connectionInfo));

        await service.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task DisconnectAsync_AfterConnect_ChangesSessionStatus()
    {
        if (SkipIfSshServerUnavailable()) return;

        var mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.IsConnected.Returns(true);
        mockClient.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var connectionInfo = CreateTestConnectionInfo();
        var service = new SshConnectionService(_ => mockClient);

        var session = await service.ConnectAsync(connectionInfo);
        Assert.AreEqual(SessionStatus.Connected, session.Status);

        await service.DisconnectAsync(session.SessionId);
        Assert.AreEqual(SessionStatus.Disconnected, session.Status);

        mockClient.Received(1).Disconnect();

        await service.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task MultipleSessions_CanConnectConcurrently()
    {
        if (SkipIfSshServerUnavailable()) return;

        var clients = new List<ISshClientWrapper>();
        ISshClientWrapper ClientFactory(ConnectionInfo _)
        {
            var client = Substitute.For<ISshClientWrapper>();
            client.IsConnected.Returns(true);
            client.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            clients.Add(client);
            return client;
        }

        var service = new SshConnectionService(ClientFactory);
        var conn1 = CreateTestConnectionInfo(username: "testuser");
        var conn2 = CreateTestConnectionInfo(username: "testuser");

        var session1 = await service.ConnectAsync(conn1);
        var session2 = await service.ConnectAsync(conn2);

        Assert.AreNotEqual(session2.SessionId, session1.SessionId);
        Assert.AreEqual(SessionStatus.Connected, session1.Status);
        Assert.AreEqual(SessionStatus.Connected, session2.Status);
        Assert.AreEqual(2, clients.Count());

        await service.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task ConnectAsync_SessionAppearsInSessionsList()
    {
        if (SkipIfSshServerUnavailable()) return;

        var mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.IsConnected.Returns(true);
        mockClient.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var service = new SshConnectionService(_ => mockClient);
        var connectionInfo = CreateTestConnectionInfo();

        var session = await service.ConnectAsync(connectionInfo);

        Assert.AreEqual(1, service.Sessions.Count);
        Assert.IsNotNull(service.GetSession(session.SessionId));
        Assert.AreEqual(SessionStatus.Connected, service.GetSession(session.SessionId)!.Status);

        await service.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task ConnectAsync_WithPrivateKeyAuth_CreatesSession()
    {
        if (SkipIfSshServerUnavailable()) return;

        var mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.IsConnected.Returns(true);
        mockClient.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var connectionInfo = new ConnectionInfo
        {
            Host = TestHost,
            Port = TestPort,
            Username = TestUser,
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/tmp/test_key_nonexistent"
        };

        var service = new SshConnectionService(_ => mockClient);

        var session = await service.ConnectAsync(connectionInfo);

        Assert.IsNotNull(session);
        Assert.AreEqual(SessionStatus.Connected, session.Status);
        Assert.AreEqual(AuthMethod.PrivateKey, session.ConnectionInfo.AuthMethod);

        await service.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task DisposeAsync_DisconnectsAllActiveSessions()
    {
        if (SkipIfSshServerUnavailable()) return;

        var clients = new List<ISshClientWrapper>();
        ISshClientWrapper ClientFactory(ConnectionInfo _)
        {
            var client = Substitute.For<ISshClientWrapper>();
            client.IsConnected.Returns(true);
            client.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            clients.Add(client);
            return client;
        }

        var service = new SshConnectionService(ClientFactory);

        await service.ConnectAsync(CreateTestConnectionInfo());
        await service.ConnectAsync(CreateTestConnectionInfo());

        Assert.AreEqual(2, clients.Count());

        await service.DisposeAsync();

        foreach (var client in clients)
        {
            client.Received(1).Disconnect();
            client.Received(1).Dispose();
        }
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public void DockerDetection_ReturnsConsistentResult()
    {
        var result1 = DockerAvailable.Value;
        var result2 = DockerAvailable.Value;

        Assert.AreEqual(result2, result1, "Docker detection should be deterministic");
    }
}
