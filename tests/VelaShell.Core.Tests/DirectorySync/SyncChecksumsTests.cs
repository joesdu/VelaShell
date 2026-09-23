using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using VelaShell.Core.DirectorySync;
using VelaShell.Core.Sftp;

namespace VelaShell.Core.Tests.DirectorySync;

[TestClass]
public sealed class SyncChecksumsTests
{
    private static readonly SyncOptions Checksum = new()
    {
        Criteria = SyncCriteria.Checksum | SyncCriteria.Time | SyncCriteria.Size,
        IgnoreCase = false,
    };

    private readonly Guid _session = Guid.NewGuid();
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vela-sync-sha-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响断言。
        }
    }

    [TestMethod]
    public async Task OnlyFilesOnBothSidesWithTheSameSize_AreHashed()
    {
        IReadOnlyList<SyncItem> local = await LocalAsync(("same.txt", "abc"), ("resized.txt", "abcd"), ("only-local.txt", "z"));
        SyncItem[] remote = [Remote("same.txt", 3), Remote("resized.txt", 9), Remote("only-remote.txt", 1)];
        ISftpService sftp = Substitute.For<ISftpService>();
        var asked = new List<string>();
        sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                IReadOnlyList<string> paths = call.ArgAt<IReadOnlyList<string>>(1);
                asked.AddRange(paths);
                return Task.FromResult<IReadOnlyDictionary<string, string?>>(paths.ToDictionary(p => p, _ => (string?)Hash("abc")));
            });

        SyncChecksumOutcome outcome = await SyncChecksums.ApplyAsync(local, remote, Checksum, sftp, _session);

        Assert.AreSequenceEqual(["/r/same.txt"], [.. asked], message: "大小不同已经证明内容不同,不必再读两遍");
        Assert.AreEqual(1, outcome.Candidates);
        Assert.AreEqual(1, outcome.Verified);
        Assert.IsNull(outcome.RemoteFailure);
        Assert.AreEqual(Hash("abc"), outcome.Local.Single(i => i.RelativePath == "same.txt").Sha256);
        Assert.AreEqual(Hash("abc"), outcome.Remote.Single(i => i.RelativePath == "same.txt").Sha256);
        Assert.IsNull(outcome.Local.Single(i => i.RelativePath == "resized.txt").Sha256);
    }

    [TestMethod]
    public async Task RemoteUnsupported_FallsBackForEverything_AndStopsAsking()
    {
        IReadOnlyList<SyncItem> local = await LocalAsync(("a.txt", "abc"));
        // 超过一批的量:不支持之后不该再一批批地撞。
        var remote = new List<SyncItem> { Remote("a.txt", 3) };
        var manyLocal = local.ToList();
        for (int i = 0; i < SyncChecksums.MaxBatchFiles + 5; i++)
        {
            manyLocal.Add(new($"f{i}", Path.Combine(_root, $"f{i}"), false, 1, DateTime.UtcNow));
            remote.Add(Remote($"f{i}", 1));
        }
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyDictionary<string, string?>>(new NotSupportedException("no sha256sum")));

        SyncChecksumOutcome outcome = await SyncChecksums.ApplyAsync(manyLocal, remote, Checksum, sftp, _session);

        Assert.AreEqual("no sha256sum", outcome.RemoteFailure);
        Assert.AreEqual(0, outcome.Verified);
        Assert.AreEqual(outcome.Candidates, outcome.FellBack);
        Assert.IsTrue(outcome.Local.All(i => i.Sha256 is null));
        await sftp.Received(1).ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task AFileTheServerCouldNotHash_FallsBackAlone()
    {
        IReadOnlyList<SyncItem> local = await LocalAsync(("ok.txt", "abc"), ("locked.txt", "xyz"));
        SyncItem[] remote = [Remote("ok.txt", 3), Remote("locked.txt", 3)];
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>
            {
                ["/r/ok.txt"] = Hash("abc"),
                ["/r/locked.txt"] = null,
            }));

        SyncChecksumOutcome outcome = await SyncChecksums.ApplyAsync(local, remote, Checksum, sftp, _session);

        Assert.AreEqual(2, outcome.Candidates);
        Assert.AreEqual(1, outcome.Verified);
        Assert.IsNull(outcome.RemoteFailure);
        Assert.IsNull(outcome.Local.Single(i => i.RelativePath == "locked.txt").Sha256);
    }

    [TestMethod]
    public async Task Cache_AvoidsHashingTheSameUnchangedFilesTwice()
    {
        IReadOnlyList<SyncItem> local = await LocalAsync(("a.txt", "abc"));
        SyncItem[] remote = [Remote("a.txt", 3)];
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?> { ["/r/a.txt"] = Hash("abc") }));
        var cache = new SyncChecksumCache();

        await SyncChecksums.ApplyAsync(local, remote, Checksum, sftp, _session, cache);
        SyncChecksumOutcome second = await SyncChecksums.ApplyAsync(local, remote, Checksum, sftp, _session, cache);

        Assert.AreEqual(1, second.Verified);
        await sftp.Received(1).ComputeSha256Async(_session, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task WithoutTheChecksumCriterion_NothingIsHashed()
    {
        IReadOnlyList<SyncItem> local = await LocalAsync(("a.txt", "abc"));
        ISftpService sftp = Substitute.For<ISftpService>();

        SyncChecksumOutcome outcome = await SyncChecksums.ApplyAsync(
            local, [Remote("a.txt", 3)], Checksum with { Criteria = SyncCriteria.Time | SyncCriteria.Size }, sftp, _session);

        Assert.AreEqual(0, outcome.Candidates);
        Assert.AreSame(local, outcome.Local);
        await sftp.DidNotReceiveWithAnyArgs().ComputeSha256Async(default, default!, default);
    }

    [TestMethod]
    public void Batches_RespectTheFileCountLimit()
    {
        SyncItem[] items = [.. Enumerable.Range(0, SyncChecksums.MaxBatchFiles * 2 + 7).Select(i => Remote($"f{i}", 1))];

        int[] sizes = [.. SyncChecksums.Batches(items).Select(b => b.Count)];

        Assert.AreSequenceEqual([SyncChecksums.MaxBatchFiles, SyncChecksums.MaxBatchFiles, 7], sizes);
    }

    [TestMethod]
    public async Task HashLocalFile_IsLowercaseHexSha256()
    {
        string path = Path.Combine(_root, "x.bin");
        await File.WriteAllTextAsync(path, "hello");

        Assert.AreEqual(Hash("hello"), await SyncChecksums.HashLocalFileAsync(path));
    }

    private async Task<IReadOnlyList<SyncItem>> LocalAsync(params (string Name, string Content)[] files)
    {
        foreach ((string name, string content) in files)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, name), content);
        }
        return (await DirectoryTreeScanner.ScanLocalAsync(_root, SyncFileMask.Empty)).Items;
    }

    private static SyncItem Remote(string relative, long size) =>
        new(relative, "/r/" + relative, false, size, DateTime.UtcNow);

    private static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
