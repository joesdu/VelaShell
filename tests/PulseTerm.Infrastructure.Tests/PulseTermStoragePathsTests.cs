using PulseTerm.Infrastructure.Persistence;

namespace PulseTerm.Infrastructure.Tests;

public sealed class PulseTermStoragePathsTests
{
    [Fact]
    public void Paths_AreGenerated_Under_PulseTerm_Root()
    {
        var paths = new PulseTermStoragePaths();

        Assert.Contains("PulseTerm", paths.RootDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("settings.json", paths.SettingsFile, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("pulseterm.db", paths.LiteDbFile, StringComparison.OrdinalIgnoreCase);
    }
}
