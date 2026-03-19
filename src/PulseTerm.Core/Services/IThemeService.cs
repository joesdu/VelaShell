namespace PulseTerm.Core.Services;

public interface IThemeService
{
    string CurrentTheme { get; }

    void SetTheme(string themeName);

    event Action<string>? ThemeChanged;
}
