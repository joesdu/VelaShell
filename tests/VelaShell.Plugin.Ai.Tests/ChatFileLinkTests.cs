using VelaShell.Plugin.Ai.Ui;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public class ChatFileLinkTests
{
    [TestMethod]
    public void FileUri_RevealsContainingFolder_ForExecutable()
    {
        DirectoryInfo folder = Directory.CreateTempSubdirectory();
        try
        {
            string file = Path.Combine(folder.FullName, "安装 100%.exe");
            File.WriteAllText(file, "");
            Uri uri = new(new Uri(file).AbsoluteUri);
            string? path = ChatPanelView.GetLocalLinkPath(uri);
            Assert.AreEqual(file, path);
            Assert.AreEqual(folder.FullName, ChatPanelView.GetLocalLinkFolder(path!)?.FullName);
            Assert.AreEqual(folder.FullName, ChatPanelView.GetLocalLinkFolder(folder.FullName)?.FullName);
            File.Delete(file);
            Assert.IsNull(ChatPanelView.GetLocalLinkFolder(file));
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [TestMethod]
    [DataRow("/srv/build/tool.exe")]
    [DataRow("build/tool.exe")]
    [DataRow("https://example.org/tool.exe")]
    public void OtherLinks_AreNotLocalFiles(string href) =>
        Assert.IsNull(ChatPanelView.GetLocalLinkPath(new Uri(href, UriKind.RelativeOrAbsolute)));

    [TestMethod]
    [DataRow(@"C:\My Tools\app.exe", @"C:\My Tools\app.exe")]
    [DataRow(@"\\server\share\app.exe", @"\\server\share\app.exe")]
    [DataRow("/C:/My%20Tools/app.exe", "C:/My Tools/app.exe")]
    [OSCondition(OperatingSystems.Windows)]
    public void WindowsMarkdownPath_ResolvesToLocalPath(string href, string expected) => Assert.AreEqual(expected, ChatPanelView.GetLocalLinkPath(new Uri(href, UriKind.RelativeOrAbsolute)));
}
