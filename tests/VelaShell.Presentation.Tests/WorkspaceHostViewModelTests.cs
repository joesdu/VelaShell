namespace VelaShell.Presentation.Tests;

[TestClass]
public sealed class WorkspaceHostViewModelTests
{
    [TestMethod]
    public void Title_Defaults_ToWorkspaceTitle()
    {
        var viewModel = new WorkspaceHostViewModel();
        Assert.AreEqual("VelaShell Workspace", viewModel.Title);
    }
}
