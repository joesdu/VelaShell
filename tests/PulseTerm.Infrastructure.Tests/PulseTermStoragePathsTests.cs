using PulseTerm.Infrastructure.Persistence;

namespace PulseTerm.Infrastructure.Tests;

[TestClass]
public sealed class PulseTermStoragePathsTests
{
    [TestMethod]
    public void Paths_AreGenerated_Under_PulseTerm_Root()
    {
        var paths = new PulseTermStoragePaths();

        StringAssert.Contains(paths.RootDirectory, "PulseTerm", StringComparison.OrdinalIgnoreCase);
        StringAssert.EndsWith(paths.SettingsFile, "settings.json", StringComparison.OrdinalIgnoreCase);
        StringAssert.EndsWith(paths.SonnetDbDirectory, "sonnetdb", StringComparison.OrdinalIgnoreCase);
        StringAssert.EndsWith(paths.SecretKeyFile, "secret.key", StringComparison.OrdinalIgnoreCase);
        StringAssert.EndsWith(paths.LegacyDotDirectory, ".pulseterm", StringComparison.OrdinalIgnoreCase);
    }
}
