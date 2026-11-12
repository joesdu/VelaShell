using VelaShell.Infrastructure.Plugins;

namespace VelaShell.Infrastructure.Tests.Plugins;

[TestClass]
[TestCategory("Plugins")]
public sealed class PluginArtifactTests
{
    private readonly string _root = Path.Combine(Environment.CurrentDirectory, "plugin-artifact-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public void Downloads_KeepActivePackagesAndRemoveAbandonedPartialFiles()
    {
        string active = PluginPackageDownloads.CreatePath(_root);
        try
        {
            File.WriteAllText(active, "active");
            string orphan = Path.Combine(_root, $"market-{Guid.NewGuid():N}.vpx");
            File.WriteAllText(orphan + ".partial", "interrupted");
            File.WriteAllText(orphan + ".lease", "");
            string legacy = Path.Combine(_root, "acme.test-1.0.0.vpx");
            File.WriteAllText(legacy, "legacy");
            PluginPackageDownloads.Sweep(_root);
            Assert.IsTrue(File.Exists(active));
            Assert.IsFalse(File.Exists(orphan + ".partial"));
            Assert.IsFalse(File.Exists(orphan + ".lease"));
            Assert.IsTrue(File.Exists(legacy));
        }
        finally { PluginPackageDownloads.Discard(active); }
        Assert.IsFalse(File.Exists(active));
        Assert.IsFalse(File.Exists(active + ".lease"));
    }

    [TestMethod]
    public void StagingAndShadows_SweepOnlyUnlockedOwnedArtifacts()
    {
        string user = Path.Combine(_root, "plugins");
        string shadows = Path.Combine(_root, "shadows");
        string staging = PluginManager.PackageStagingRoot(user);
        using var active = PluginArtifactLease.Create(staging, "", "", true);
        string abandoned = Path.Combine(shadows, "acme.test", $"gen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(abandoned + ".lease", "");
        string legacy = Path.Combine(shadows, "acme.test", "gen-1");
        Directory.CreateDirectory(legacy);
        PluginArtifactMaintenance.Sweep(user, shadows);
        Assert.IsTrue(Directory.Exists(active.Path));
        Assert.IsFalse(Directory.Exists(abandoned));
        Assert.IsTrue(Directory.Exists(legacy));
    }

    [TestMethod]
    public void Recovery_RestoresUniqueValidBackupAndPreservesAmbiguousBackups()
    {
        string valid = Backup("acme.unique");
        string invalid = Backup("acme.unique");
        File.Delete(Path.Combine(invalid, "entry.dll"));
        string first = Backup("acme.ambiguous");
        string second = Backup("acme.ambiguous");
        PluginArtifactMaintenance.Sweep(_root, null);
        Assert.IsTrue(File.Exists(Path.Combine(_root, "acme.unique", "entry.dll")));
        Assert.IsFalse(Directory.Exists(valid));
        Assert.IsTrue(Directory.Exists(invalid));
        Assert.IsTrue(Directory.Exists(first));
        Assert.IsTrue(Directory.Exists(second));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "acme.ambiguous")));
        Assert.IsTrue(PluginArtifactMaintenance.HasBackup(_root, "acme.ambiguous"));
    }

    [TestMethod]
    public void Recovery_DoesNotTouchAnActiveUpgradeOrReplaceAnExistingInstallation()
    {
        string backup = Backup("acme.test");
        using (PluginArtifactMaintenance.AcquireInstallationLease(_root))
        {
            PluginArtifactMaintenance.Sweep(_root, null);
            Assert.IsTrue(Directory.Exists(backup));
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "acme.test")));
        }
        Directory.CreateDirectory(Path.Combine(_root, "acme.test"));
        PluginArtifactMaintenance.Sweep(_root, null);
        Assert.IsTrue(Directory.Exists(backup));
    }

    private string Backup(string id)
    {
        string directory = Path.Combine(_root, ".upgrade", $"{id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            { "id": "{{id}}", "version": "1.0.0", "displayName": "Test", "author": "Test", "entry": "entry.dll" }
            """);
        File.WriteAllText(Path.Combine(directory, "entry.dll"), "fixture");
        return directory;
    }
}
