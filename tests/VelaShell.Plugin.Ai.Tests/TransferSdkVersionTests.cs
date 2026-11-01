using System.Text.Json;
using System.Xml.Linq;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class TransferSdkVersionTests
{
    [TestMethod]
    public void UploadContract_HostTestsAndPluginUseTheSamePublishedSdk()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "VelaShell.slnx")))
            root = root.Parent;
        Assert.IsNotNull(root, "版本检查必须在仓库中运行，不能因找不到源码而跳过。");
        string host = ReadVersion(Path.Combine(root.FullName, "src", "Directory.Packages.props"),
            "PackageVersion", "VelaShell.PluginSdk");
        string tests = Path.Combine(root.FullName, "tests", "Directory.Packages.props");
        string plugin = Path.Combine(root.FullName, "plugins", "VelaShell.Plugin.Ai");
        Assert.IsGreaterThanOrEqualTo(new Version(2, 1, 1), Version.Parse(host), "上传任务契约需要正式 SDK 2.1.1 或更新版本。");
        Assert.AreEqual(host, ReadVersion(tests, "PackageVersion", "VelaShell.PluginSdk"));
        Assert.AreEqual(host, ReadVersion(tests, "PackageVersion", "VelaShell.PluginSdk.Testing"));
        Assert.AreEqual(host, ReadVersion(Path.Combine(plugin, "VelaShell.Plugin.Ai.csproj"),
            "PackageReference", "VelaShell.PluginSdk"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(plugin, "plugin.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        Assert.AreEqual(host, manifest.RootElement.GetProperty("minSdkVersion").GetString(),
            "新插件必须拒载旧契约程序集，不能仅靠能力位避免缺少类型。");
        Assert.AreEqual(Version.Parse(host), Version.Parse(VelaPluginApi.SdkVersion.Split('-')[0]),
            "实际加载的 SDK 必须匹配正式包引用，不能用本地旧程序集掩盖版本差异。");
        Assert.IsNotNull(typeof(IRemoteFsApi).GetMethod(nameof(IRemoteFsApi.StartUploadAsync)));
        Assert.IsNotNull(typeof(IRemoteFsApi).GetMethod(nameof(IRemoteFsApi.CancelTransferAsync),
            [typeof(string), typeof(long), typeof(CancellationToken)]));
        // 停止时撤待确认传输用到条件撤回(SDK 2.1.4 起):契约程序集里没有这个成员的宿主,插件必须靠 minSdkVersion 拒载。
        Assert.IsGreaterThanOrEqualTo(new Version(2, 1, 4), Version.Parse(host), "条件撤回需要正式 SDK 2.1.4 或更新版本。");
        Assert.IsNotNull(typeof(IRemoteFsApi).GetMethod(nameof(IRemoteFsApi.WithdrawPendingTransferAsync),
            [typeof(string), typeof(long), typeof(CancellationToken)]));
        Assert.IsNotNull(typeof(IRemoteFsApi).GetProperty(nameof(IRemoteFsApi.SupportsTransferWithdraw)));
    }

    private static string ReadVersion(string path, string element, string packageId) =>
        XDocument.Load(path).Descendants(element)
            .Single(node => node.Attribute("Include")?.Value == packageId).Attribute("Version")!.Value;
}
