using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;

namespace VelaShell.Core.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public sealed class StandaloneSftpServiceContractTests
{
    [TestMethod]
    public async Task SerializedService_OwnsOneSession_AndRepeatedCleanupClosesItOnce()
    {
        ISftpService inner = Substitute.For<ISftpService>();
        var sessionId = Guid.NewGuid();
        var service = new SerializedSftpService(inner, sessionId);

        Assert.AreEqual(sessionId, service.SessionId);
        await service.CloseAsync();
        await service.CloseSessionAsync(sessionId);
        await service.DisposeAsync();

        await inner.Received(1).CloseSessionAsync(sessionId, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task SerializedService_DelegatesLocalRemoteAndRemoteLocalTransfersForOwnedSession()
    {
        ISftpService inner = Substitute.For<ISftpService>();
        var sessionId = Guid.NewGuid();
        var service = new SerializedSftpService(inner, sessionId);

        await service.UploadFileAsync(sessionId, "local.txt", "/remote/local.txt");
        await service.DownloadFileAsync(sessionId, "/remote/remote.txt", "remote.txt");

        await inner.Received(1).UploadFileAsync(
            sessionId,
            "local.txt",
            "/remote/local.txt",
            Arg.Any<IProgress<TransferProgress>?>(),
            Arg.Any<CancellationToken>()
        );
        await inner.Received(1).DownloadFileAsync(
            sessionId,
            "/remote/remote.txt",
            "remote.txt",
            Arg.Any<IProgress<TransferProgress>?>(),
            Arg.Any<CancellationToken>()
        );
    }
}
