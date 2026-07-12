using System.Globalization;
using System.Resources;
using VelaShell.Core.Resources;

namespace VelaShell.Core.Localization;

public class LocalizationService : ILocalizationService
{
    private readonly ResourceManager _resourceManager = new("VelaShell.Core.Resources.Strings", typeof(Strings).Assembly);

    // 服务自持目标文化,取词不依赖线程环境:CurrentUICulture 是线程级状态且随
    // ExecutionContext 流动 —— 在异步命令(设置保存)里改它会随上下文回卷丢失;
    // 而 UI 线程启动时被显式设置过文化后,DefaultThreadCurrentUICulture 也不再
    // 影响它。两条路都靠不住,曾表现为“保存后界面不换语言”。
    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    public string GetString(string key)
    {
        try
        {
            string? value = _resourceManager.GetString(key, _culture);
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

    public string CurrentLanguage => _culture.Name;

    public event Action<string>? LanguageChanged;

    public void SetLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var culture = new CultureInfo(language);
        if (culture.Name == _culture.Name)
        {
            return;
        }
        _culture = culture;

        // 环境文化仍尽力同步:Default* 覆盖未显式设置文化的线程(含此后新建的),
        // Current* 覆盖当前流;UI 线程的线程级文化由宿主在 Dispatcher 顶层回调里
        // 补设(见 App.axaml.cs),供 C# 侧 Strings.Get 与日期/数字格式化使用。
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        LanguageChanged?.Invoke(culture.Name);
    }
}
