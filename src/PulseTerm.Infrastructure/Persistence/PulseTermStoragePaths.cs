namespace PulseTerm.Infrastructure.Persistence;

public sealed class PulseTermStoragePaths
{
    public PulseTermStoragePaths()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PulseTerm");

        RootDirectory = root;
        SettingsFile = Path.Combine(root, "settings.json");
        StateFile = Path.Combine(root, "state.json");
        SessionsFile = Path.Combine(root, "sessions.json");
        LiteDbFile = Path.Combine(root, "pulseterm.db");
    }

    public string RootDirectory { get; }

    public string SettingsFile { get; }

    public string StateFile { get; }

    public string SessionsFile { get; }

    public string LiteDbFile { get; }
}
