using VelaShell.Tests.TestSupport;

namespace VelaShell.Tests.Integration;

/// <summary>
/// 靶机镜像跟不上 Dockerfile 时的判定(<see cref="FixtureImage" />):比的是什么、旧了怎么说。
/// 不连容器 —— 判定是纯函数,真连容器的那一半在各 DockerIntegration 用例的环境检查里。
/// </summary>
[TestClass]
public sealed class FixtureImageTests
{
    private const string Dockerfile = """
        # 注释
        FROM alpine:3.21
        RUN apk add --no-cache openssh

        COPY Dockerfile /etc/velashell-fixture/Dockerfile
        CMD ["/usr/sbin/sshd", "-D", "-e"]
        """;

    [TestMethod]
    public void SameDockerfile_IsCurrent() =>
        Assert.IsNull(FixtureImage.Compare("ssh-shells", "ssh-shells", Dockerfile, Dockerfile));

    /// <summary>Windows 检出是 CRLF、改的只是注释与空行:Docker 解析时同样丢掉这些,不算旧。</summary>
    [TestMethod]
    public void LineEndingsCommentsAndBlankLines_DoNotCount()
    {
        string edited = Dockerfile.Replace("\n", "\r\n", StringComparison.Ordinal)
                                  .Replace("# 注释", "# 换了一句注释\r\n\r\n    # 又一句", StringComparison.Ordinal) + "   \r\n";

        Assert.IsNull(FixtureImage.Compare("ssh-shells", "ssh-shells", edited, Dockerfile));
    }

    /// <summary>镜像建于这项检查之前(里面根本没有那份 Dockerfile):旧了,给出重建命令。</summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("\n")]
    public void ImageWithoutTheFile_IsStale(string? image)
    {
        string? verdict = FixtureImage.Compare("ssh-shells", "ssh-shells", Dockerfile, image);

        Assert.IsNotNull(verdict);
        Assert.StartsWith("[SKIP]", verdict);
        Assert.Contains("docker compose -f docker-compose.test.yml build ssh-shells", verdict);
        Assert.Contains(FixtureImage.ImagePath, verdict);
    }

    /// <summary>改了一条指令而本机没重建:旧了,并指出从哪一行起对不上。</summary>
    [TestMethod]
    public void ChangedInstruction_IsStaleAndPointsAtTheLine()
    {
        string repository = Dockerfile.Replace("openssh", "openssh sudo", StringComparison.Ordinal);

        string? verdict = FixtureImage.Compare("ssh-shells", "ssh-shells", repository, Dockerfile);

        Assert.IsNotNull(verdict);
        Assert.Contains("RUN apk add --no-cache openssh sudo", verdict);
        Assert.Contains("docker compose -f docker-compose.test.yml build ssh-shells", verdict);
    }

    [TestMethod]
    public void ImageWithExtraLines_IsStale() =>
        Assert.IsNotNull(FixtureImage.Compare("ssh-shells", "ssh-shells", Dockerfile, Dockerfile + "\nRUN true\n"));

    /// <summary>
    /// 门槛本身得有着落:ssh-shells 的 Dockerfile 要把自己拷进镜像,否则重建多少次也永远是「旧了」。
    /// </summary>
    [TestMethod]
    public void TheShellsFixtureCopiesItsDockerfileIntoTheImage()
    {
        string? dockerfile = FixtureImage.ReadRepositoryDockerfile("ssh-shells");

        Assert.IsNotNull(dockerfile, "找不到 tests/fixtures/ssh-shells/Dockerfile");
        Assert.Contains($"COPY Dockerfile {FixtureImage.ImagePath}", dockerfile);
    }
}
