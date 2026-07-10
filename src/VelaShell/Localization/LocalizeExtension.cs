using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using VelaShell.Core.Localization;

namespace VelaShell.Localization;

/// <summary>
/// Live-updating localized string binding: <c>Text="{loc:Localize QuickConnect}"</c>.
/// Unlike <c>{x:Static Strings.X}</c> (frozen at load), every use refreshes instantly when
/// the language changes (#4 — no restart). Backed by resx via ILocalizationService.
/// </summary>
public class LocalizeExtension : MarkupExtension
{
    public LocalizeExtension() { }

    public LocalizeExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Mode = BindingMode.OneWay,
            Source = LocalizedStrings.Instance
        };
}

/// <summary>
/// Bindable indexer over the localization service; language changes re-resolve
/// every bound key at once via the indexer-changed notification.
/// </summary>
public sealed class LocalizedStrings : INotifyPropertyChanged
{
    private ILocalizationService _service = new LocalizationService();

    public static LocalizedStrings Instance { get; } = new();

    public string this[string key] => _service.GetString(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Swaps in the DI-provided service and hooks live refresh (call once at startup).</summary>
    public void Attach(ILocalizationService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        service.LanguageChanged += _ =>
            PropertyChanged?.Invoke(this, new("Item[]"));
    }
}
