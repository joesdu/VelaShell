using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

[TestClass]
public sealed class VelaShellStoragePathsTests
{
    [TestMethod]
    public void Paths_AreGenerated_Under_VelaShell_Root()
    {
        var paths = new VelaShellStoragePaths();

        StringAssert.Contains(paths.RootDirectory, "VelaShell", StringComparison.OrdinalIgnoreCase);
        StringAssert.EndsWith(paths.SettingsFile, "settings.json", StringComparison.OrdinalIgnoreCase);
        StringAssert.EndsWith(paths.SonnetDbDirectory, "sonnetdb", StringComparison.OrdinalIgnoreCase);
        StringAssert.EndsWith(paths.SecretKeyFile, "secret.key", StringComparison.OrdinalIgnoreCase);
        StringAssert.EndsWith(paths.LegacyDotDirectory, ".velashell", StringComparison.OrdinalIgnoreCase);
    }
}
