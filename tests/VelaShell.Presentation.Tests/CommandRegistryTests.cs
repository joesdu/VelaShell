using VelaShell.Presentation.Commands;

namespace VelaShell.Presentation.Tests;

[TestClass]
[TestCategory("Commands")]
public class CommandRegistryTests
{
    [TestMethod]
    public void Register_ThenFindAndExecute()
    {
        var registry = new CommandRegistry();
        var ran = false;
        registry.Register(new CommandDescriptor("t.run", "Run", "工具", () => ran = true, Shortcut: "Ctrl+R"));

        Assert.IsNotNull(registry.Find("t.run"));
        Assert.IsTrue(registry.Execute("t.run"));
        Assert.IsTrue(ran);
    }

    [TestMethod]
    public void Execute_UnknownId_ReturnsFalse()
    {
        var registry = new CommandRegistry();
        Assert.IsFalse(registry.Execute("nope"));
    }

    [TestMethod]
    public void Execute_DisabledCommand_DoesNotRun()
    {
        var registry = new CommandRegistry();
        var ran = false;
        registry.Register(new CommandDescriptor("t.off", "Off", "工具", () => ran = true, CanExecute: () => false));

        Assert.IsFalse(registry.Execute("t.off"));
        Assert.IsFalse(ran);
    }

    [TestMethod]
    public void Register_SameId_ReplacesButKeepsOrder()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor("a", "A1", "c", () => { }));
        registry.Register(new CommandDescriptor("b", "B", "c", () => { }));
        registry.Register(new CommandDescriptor("a", "A2", "c", () => { }));

        Assert.AreEqual(2, registry.All.Count);
        Assert.AreEqual("A2", registry.All[0].Title);
        Assert.AreEqual("b", registry.All[1].Id);
    }
}
