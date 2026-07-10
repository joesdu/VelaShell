using System.Globalization;
using System.Resources;
using VelaShell.Core.Resources;

namespace VelaShell.Core.Localization;

public class LocalizationService : ILocalizationService
{
    private readonly ResourceManager _resourceManager = new("VelaShell.Core.Resources.Strings", typeof(Strings).Assembly);

    public string GetString(string key)
    {
        try
        {
            string? value = _resourceManager.GetString(key, CultureInfo.CurrentUICulture);
            return value ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
        catch (MissingSatelliteAssemblyException)
        {
            return key;
        }
    }

    public string CurrentLanguage => CultureInfo.CurrentUICulture.Name;

    public event Action<string>? LanguageChanged;

    public void SetLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var culture = new CultureInfo(language);
        if (culture.Name == CultureInfo.CurrentUICulture.Name)
        {
            return;
        }
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        LanguageChanged?.Invoke(culture.Name);
    }
}
