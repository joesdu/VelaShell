using PulseTerm.Presentation;

namespace PulseTerm.Presentation.Tests;

public sealed class WorkspaceHostViewModelTests
{
    [Fact]
    public void Title_Defaults_ToWorkspaceTitle()
    {
        var viewModel = new WorkspaceHostViewModel();

        Assert.Equal("PulseTerm Workspace", viewModel.Title);
    }
}
