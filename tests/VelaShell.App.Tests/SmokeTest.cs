namespace VelaShell.App.Tests;

[TestClass]
public class SmokeTest
{
    [TestMethod]
    public void SmokeTest_AppInitializes()
    {
        var app = new App();
        Assert.IsNotNull(app);
    }
}
