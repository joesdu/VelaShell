using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

[TestClass]
public sealed class VelaShellStoragePathsTests
{
    [TestMethod]
    public void Paths_AreGenerated_Under_VelaShell_Root()
    {
        var paths = new VelaShellStoragePaths();

        Assert.Contains("VelaShell", paths.RootDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("settings.json", paths.SettingsFile, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("sonnetdb", paths.SonnetDbDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("secret.key", paths.SecretKeyFile, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".velashell", paths.LegacyDotDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
