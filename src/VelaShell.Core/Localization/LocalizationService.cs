using System.Globalization;
using System.Resources;
using VelaShell.Core.Resources;

namespace VelaShell.Core.Localization;

/// <summary>基于资源程序集的本地化服务:自持目标文化,提供取词与运行时切换语言。</summary>
public class LocalizationService : ILocalizationService
{
    private readonly ResourceManager _resourceManager = new("VelaShell.Core.Resources.Strings", typeof(Strings).Assembly);

    // 服务自持目标文化,取词不依赖线程环境:CurrentUICulture 是线程级状态且随
    // ExecutionContext 流动 —— 在异步命令(设置保存)里改它会随上下文回卷丢失;
    // 而 UI 线程启动时被显式设置过文化后,DefaultThreadCurrentUICulture 也不再
    // 影响它。两条路都靠不住,曾表现为“保存后界面不换语言”。
    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    /// <summary>按键取当前文化下的本地化字符串;缺失或资源不可用时回退返回键本身。</summary>
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

    /// <summary>当前语言的文化名称(如 zh-CN)。</summary>
    public string CurrentLanguage => _culture.Name;

    /// <summary>语言切换成功后触发,携带新的文化名称。</summary>
    public event Action<string>? LanguageChanged;

    /// <summary>切换当前语言:与现有文化相同则跳过,否则更新并尽力同步环境文化后触发 <see cref="LanguageChanged"/>。</summary>
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
