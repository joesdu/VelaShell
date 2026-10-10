using VelaShell.Core.Models;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

[TestClass]
public sealed class StartupAppearanceTests
{
    private string _directory = "";

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "velashell-startup-appearance-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    public void Default_MatchesAppSettingsDefaults()
    {
        AppSettings defaults = new();

        Assert.AreEqual(defaults.Theme, StartupAppearance.Default.Theme);
        Assert.AreEqual(defaults.AccentColor, StartupAppearance.Default.Accent);
        Assert.AreEqual(defaults.Language, StartupAppearance.Default.Language);
        Assert.AreEqual(SplashStyles.Classic, StartupAppearance.Default.SplashStyle);
    }

    [TestMethod]
    public void SerializeThenParse_RoundTrips()
    {
        StartupAppearance original = new("tokyo-night", "#7AA2F7", "ja-JP", SplashStyles.Constellation);

        Assert.AreEqual(original, StartupAppearance.Parse(original.Serialize()));
    }

    [TestMethod]
    public void From_TakesTheFourFieldsFromSettings()
    {
        AppSettings settings = new() { Theme = "nord", AccentColor = "#88C0D0", Language = "ko-KR" };
        settings.Appearance.SplashStyle = SplashStyles.Mascot;

        Assert.AreEqual(new StartupAppearance("nord", "#88C0D0", "ko-KR", SplashStyles.Mascot), StartupAppearance.From(settings));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not a mirror file at all")]
    [DataRow("=\n==\n\0\0")]
    public void Parse_GarbageFallsBackToDefault(string? text)
    {
        Assert.AreEqual(StartupAppearance.Default, StartupAppearance.Parse(text));
    }

    [TestMethod]
    public void Parse_FillsMissingKeysFromDefaultAndNormalizesStyle()
    {
        StartupAppearance parsed = StartupAppearance.Parse("splash=hologram\r\ntheme= light \r\nfuture=whatever\r\n");

        Assert.AreEqual("light", parsed.Theme);
        Assert.AreEqual(StartupAppearance.Default.Language, parsed.Language);
        Assert.AreEqual(SplashStyles.Classic, parsed.SplashStyle);
    }

    [TestMethod]
    public void Serialize_StripsLineBreaksSoOneValueCannotForgeAnother()
    {
        StartupAppearance sneaky = new("dark\nsplash=none", "", "", SplashStyles.Terminal);

        StartupAppearance parsed = StartupAppearance.Parse(sneaky.Serialize());

        Assert.AreEqual(SplashStyles.Terminal, parsed.SplashStyle);
        Assert.AreEqual("dark splash=none", parsed.Theme);
    }

    [TestMethod]
    public void Read_MissingFileGivesDefault()
    {
        Assert.AreEqual(StartupAppearance.Default, StartupAppearance.Read(Path.Combine(_directory, "nope.appearance")));
    }

    [TestMethod]
    public void Mirror_WritesFileAndSkipsUnchangedContent()
    {
        string path = Path.Combine(_directory, "startup.appearance");
        AppSettings settings = new() { Theme = "gruvbox" };
        settings.Appearance.SplashStyle = SplashStyles.Prompt;

        StartupAppearance.Mirror(settings, path);
        Assert.AreEqual(StartupAppearance.From(settings), StartupAppearance.Read(path));

        // 内容没变就不碰文件:启动时每次都镜像一遍,不该每次都写盘。
        DateTime stamped = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamped);
        StartupAppearance.Mirror(settings, path);
        Assert.AreEqual(stamped, File.GetLastWriteTimeUtc(path));

        settings.Appearance.SplashStyle = SplashStyles.None;
        StartupAppearance.Mirror(settings, path);
        Assert.AreEqual(SplashStyles.None, StartupAppearance.Read(path).SplashStyle);
    }

    [TestMethod]
    public void StoragePaths_PutsTheMirrorUnderTheDataRoot()
    {
        VelaShellStoragePaths paths = new(_directory);

        Assert.AreEqual(Path.Combine(_directory, "startup.appearance"), paths.StartupAppearanceFile);
    }
}
