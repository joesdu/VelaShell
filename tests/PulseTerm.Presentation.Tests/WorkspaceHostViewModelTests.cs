using PulseTerm.Presentation;

namespace PulseTerm.Presentation.Tests;

[TestClass]
public sealed class WorkspaceHostViewModelTests
{
    [TestMethod]
    public void Title_Defaults_ToWorkspaceTitle()
    {
        var viewModel = new WorkspaceHostViewModel();

        Assert.AreEqual("PulseTerm Workspace", viewModel.Title);
    }
}
