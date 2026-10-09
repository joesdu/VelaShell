using System.Xml.Linq;

namespace VelaShell.Tests.Design;

/// <summary>
/// 守门:铺了实心强调色 <c>VelaAccent</c> 的容器里,不许再用强调色写字。
/// </summary>
/// <remarks>
/// <para>
/// <c>VelaAccentText</c> 名字像「压在强调色上的字」,其实是「用强调色写的字」——
/// <c>ThemeTokenApplier</c> 运行时把它直接赋成强调色本身。隧道面板、文件传输浮层与消息中心的
/// 数量徽标都拿它压在 <c>VelaAccent</c> 实底上,字和底同一个颜色,数字整个看不见。
/// </para>
/// <para>
/// 数量徽标改成与「创建」药丸按钮同一套:<c>VelaAccentDim</c> 浅底 + <c>VelaAccent</c> 字。
/// 真要实心强调底时,上面的字用 <c>VelaAccentForeground</c>(按主题算过对比度)。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Design")]
public sealed class AccentOnAccentTests
{
    private const string AccentFill = "{DynamicResource VelaAccent}";

    private static readonly string[] AccentInk =
    [
        "{DynamicResource VelaAccent}",
        "{DynamicResource VelaAccentText}"
    ];

    [TestMethod]
    public void NothingIsWrittenInAccentOnAnAccentFill()
    {
        string viewsRoot = Path.Combine(RepoRoot(), "src", "VelaShell", "Views");
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(viewsRoot, "*.axaml", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(viewsRoot, file).Replace('\\', '/');
            XDocument document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (XElement fill in document.Descendants().Where(e => (string?)e.Attribute("Background") == AccentFill))
            {
                foreach (XElement ink in fill.Descendants().Where(e => AccentInk.Contains((string?)e.Attribute("Foreground"))))
                {
                    offenders.Add($"{relative}:{((System.Xml.IXmlLineInfo)ink).LineNumber}  <{ink.Name.LocalName} Foreground=\"{(string?)ink.Attribute("Foreground")}\">");
                }
            }
        }

        Assert.IsEmpty(
            offenders,
            "VelaAccent 实底上的字/图标不能再用强调色(VelaAccentText 运行时就是 VelaAccent),"
            + "数量徽标请改用 VelaAccentDim 底 + VelaAccent 字,实心底上的字用 VelaAccentForeground:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位到仓库根目录(找不到 VelaShell.slnx)。");
    }
}
