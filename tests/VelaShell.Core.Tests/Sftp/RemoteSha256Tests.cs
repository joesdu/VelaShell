using VelaShell.Core.Sftp;

namespace VelaShell.Core.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public sealed class RemoteSha256Tests
{
    private const string HashA = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string HashB = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";

    [TestMethod]
    public void BuildCommand_RunsUnderShAndQuotesEveryPath()
    {
        string command = RemoteSha256.BuildCommand(["/srv/a b.txt", "/srv/it's.txt", "/srv/$HOME;rm"]);

        Assert.StartsWith("sh -c '", command);
        Assert.Contains("'/srv/a b.txt'", command);
        Assert.Contains("'/srv/it'\\''s.txt'", command, "内嵌单引号必须写成 '\\''");
        Assert.Contains("'/srv/$HOME;rm'", command, "单引号里的 $ 与 ; 不会被展开或断句");
    }

    [TestMethod]
    public void Parse_MapsBothOutputFormats_AndLowercasesTheDigest()
    {
        string output = $"{HashA}  /srv/a.txt\n{HashB} */srv/b.bin\n";

        IReadOnlyDictionary<string, string?> result = RemoteSha256.Parse(output, "", 0, ["/srv/a.txt", "/srv/b.bin"]);

        Assert.AreEqual(HashA, result["/srv/a.txt"]);
        Assert.AreEqual(HashB.ToLowerInvariant(), result["/srv/b.bin"]);
    }

    [TestMethod]
    public void Parse_UnescapesNamesWithBackslashesAndNewlines()
    {
        // GNU sha256sum:名字里有 \ 或换行时整行以 \ 开头,名字里的 \\ 与 \n 被转义。
        string output = $"\\{HashA}  /srv/a\\\\b\\nc.txt\n";

        IReadOnlyDictionary<string, string?> result = RemoteSha256.Parse(output, "", 0, ["/srv/a\\b\nc.txt"]);

        Assert.AreEqual(HashA, result["/srv/a\\b\nc.txt"]);
    }

    [TestMethod]
    public void Parse_AFileThatFailed_IsNullButTheRestStand()
    {
        IReadOnlyDictionary<string, string?> result = RemoteSha256.Parse(
            $"{HashA}  /srv/a.txt\n",
            "sha256sum: /srv/gone.txt: No such file or directory\n",
            1,
            ["/srv/a.txt", "/srv/gone.txt"]);

        Assert.AreEqual(HashA, result["/srv/a.txt"]);
        Assert.IsNull(result["/srv/gone.txt"]);
    }

    [TestMethod]
    public void Parse_EveryFileInTheBatchUnreadable_IsPerFileNotUnsupported()
    {
        IReadOnlyDictionary<string, string?> result = RemoteSha256.Parse(
            "",
            "sha256sum: /srv/secret: Permission denied\n",
            1,
            ["/srv/secret"]);

        Assert.IsNull(result["/srv/secret"]);
    }

    [TestMethod]
    public void Parse_NoToolOrNotAPosixHost_IsNotSupported()
    {
        Assert.ThrowsExactly<NotSupportedException>(() =>
            RemoteSha256.Parse(RemoteSha256.UnavailableMarker + "\n", "", 0, ["/a"]));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            RemoteSha256.Parse("", "'sh' is not recognized as an internal or external command", 1, ["/a"]));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            RemoteSha256.Parse("This service allows sftp connections only.\n", "", 1, ["/a"]));
    }
}
