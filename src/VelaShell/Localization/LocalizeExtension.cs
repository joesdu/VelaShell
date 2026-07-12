using System.Collections.Concurrent;
using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using VelaShell.Core.Localization;

namespace VelaShell.Localization;

/// <summary>
/// Live-updating localized string binding: <c>Text="{loc:Localize QuickConnect}"</c>.
/// Unlike <c>{x:Static Strings.X}</c> (frozen at load), every use refreshes instantly when
/// the language changes (#4 — no restart). Backed by resx via ILocalizationService.
/// 绑定目标是按键缓存的 <see cref="LocalizedText" /> 条目的普通属性 —— 不依赖
/// INPC 索引器变更通知(Avalonia 12 的绑定引擎对 "Item[]" 通知不响应,
/// 曾表现为“保存后既有界面不换语言、新开窗口才是新语言”)。
/// </summary>
public class LocalizeExtension : MarkupExtension
{
    public LocalizeExtension() { }

    public LocalizeExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LocalizedText.Value))
        {
            Mode = BindingMode.OneWay,
            Source = LocalizedStrings.Instance.GetEntry(Key)
        };
}

/// <summary>单个本地化键的实时值:换语言时 <see cref="Value" /> 触发标准属性变更通知。</summary>
public sealed class LocalizedText(LocalizedStrings owner, string key) : INotifyPropertyChanged
{
    public string Value => owner[key];

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void NotifyChanged() => PropertyChanged?.Invoke(this, new(nameof(Value)));
}

/// <summary>
/// 本地化取词源:按键缓存 <see cref="LocalizedText" /> 条目,语言切换时逐条目
/// 发标准属性通知,既有绑定全部重取。保留可绑定索引器 + INPC 供兼容。
/// </summary>
public sealed class LocalizedStrings : INotifyPropertyChanged
{
    private readonly ConcurrentDictionary<string, LocalizedText> _entries = new();
    private ILocalizationService _service = new LocalizationService();

    public static LocalizedStrings Instance { get; } = new();

    public string this[string key] => _service.GetString(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>取(或建)键的绑定条目;同键共享同一条目,通知一次全量生效。</summary>
    public LocalizedText GetEntry(string key) => _entries.GetOrAdd(key, k => new LocalizedText(this, k));

    /// <summary>Swaps in the DI-provided service and hooks live refresh (call once at startup).</summary>
    public void Attach(ILocalizationService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        service.LanguageChanged += _ =>
        {
            foreach (LocalizedText entry in _entries.Values)
            {
                entry.NotifyChanged();
            }
            PropertyChanged?.Invoke(this, new("Item[]"));
        };
    }
}
