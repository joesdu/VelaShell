namespace PulseTerm.App.Tests;

public class SmokeTest
{
    [Fact]
    public void SmokeTest_AppInitializes()
    {
        var app = new App();
        Assert.NotNull(app);
    }
}
