using System.Globalization;
using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.XServer;

namespace VelaShell.ViewModels;

/// <summary>下拉里的一项:存盘的值 + 显示给人看的文字。</summary>
/// <param name="Value">写进设置的值。</param>
/// <param name="Label">下拉里显示的文字。</param>
public sealed record XServerChoice(string Value, string Label);

/// <summary>设置 → X Server 页(本机 VcXsrv 的启动参数)。</summary>
public partial class SettingsViewModel
{
    /// <summary>
    /// 键盘布局下拉:首项「自动」(不传 <c>-xkblayout</c>,VcXsrv 跟随 Windows 当前布局),其后是常用 XKB 布局。
    /// </summary>
    /// <remarks>
    /// 布局名沿用 XKB 自己的英文描述(与 <c>xkeyboard-config</c> 的 <c>evdev.lst</c> 一致),不本地化 ——
    /// 它们是要和远端 <c>setxkbmap</c> 对照着看的技术名,与主题名同理。只有「自动」那一项跟随界面语言。
    /// 表外的布局用「附加参数」里的 <c>-xkblayout</c> 指定(后写的覆盖先写的)。
    /// </remarks>
    public XServerChoice[] XServerKeyboardLayouts { get; private set; } = BuildKeyboardLayouts();

    private static XServerChoice[] BuildKeyboardLayouts() =>
    [
        new("", Strings.Get("SetXServer_LayoutAuto")),
        new("us", "us - English (US)"),
        new("gb", "gb - English (UK)"),
        new("de", "de - German"),
        new("fr", "fr - French"),
        new("es", "es - Spanish"),
        new("latam", "latam - Spanish (Latin American)"),
        new("it", "it - Italian"),
        new("pt", "pt - Portuguese"),
        new("br", "br - Portuguese (Brazil)"),
        new("ch", "ch - German (Switzerland)"),
        new("be", "be - Belgian"),
        new("nl", "nl - Dutch"),
        new("se", "se - Swedish"),
        new("no", "no - Norwegian"),
        new("dk", "dk - Danish"),
        new("fi", "fi - Finnish"),
        new("pl", "pl - Polish"),
        new("cz", "cz - Czech"),
        new("hu", "hu - Hungarian"),
        new("ru", "ru - Russian"),
        new("ua", "ua - Ukrainian"),
        new("tr", "tr - Turkish"),
        new("ca", "ca - French (Canada)"),
        new("jp", "jp - Japanese"),
        new("kr", "kr - Korean"),
        new("cn", "cn - Chinese"),
        new("tw", "tw - Taiwanese"),
    ];

    /// <summary>键盘型号下拉(XKB 型号名,英文描述不本地化,理由同 <see cref="XServerKeyboardLayouts" />)。</summary>
    public XServerChoice[] XServerKeyboardModels { get; } =
    [
        new("pc105", "pc105 - Generic 105-key PC"),
        new("pc104", "pc104 - Generic 104-key PC"),
        new("pc102", "pc102 - Generic 102-key PC"),
        new("pc101", "pc101 - Generic 101-key PC"),
        new("jp106", "jp106 - Japanese 106-key"),
        new("kr106", "kr106 - Korean 106-key"),
        new("abnt2", "abnt2 - Brazilian ABNT2"),
        new("macintosh", "macintosh - Macintosh"),
    ];

    /// <summary>显示号下拉:首项「自动」,其后 <c>:0</c> 到 <c>:15</c>。</summary>
    public string[] XServerDisplayNumbers { get; private set; } = BuildDisplayNumbers();

    private static string[] BuildDisplayNumbers() =>
    [
        Strings.Get("SetXServer_DisplayAuto"),
        .. Enumerable.Range(0, XServerOptions.MaxDisplayNumber + 1)
            .Select(n => ":" + n.ToString(CultureInfo.InvariantCulture)),
    ];

    /// <summary>显示号下拉的选中项:0 = 自动,i = 显示 <c>:i-1</c>。</summary>
    public int XServerDisplayNumberIndex
    {
        get => XServer.DisplayNumber < 0 ? 0 : Math.Min(XServer.DisplayNumber, XServerOptions.MaxDisplayNumber) + 1;
        set
        {
            XServer.DisplayNumber = value <= 0 ? XServerOptions.AutoDisplayNumber : value - 1;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>窗口模式下拉的选中项,与 <see cref="XServerWindowModes.All" /> 同序。</summary>
    public int XServerWindowModeIndex
    {
        get => Math.Max(0, IndexOf(XServerWindowModes.All, XServer.WindowMode));
        set
        {
            XServer.WindowMode = value >= 0 && value < XServerWindowModes.All.Count
                ? XServerWindowModes.All[value]
                : XServerWindowModes.MultiWindow;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>
    /// 键盘布局下拉的选中项。存的值不在表里(导入的配置、手改的 JSON)时为 -1,下拉显示为空 ——
    /// 值本身不动,照样传给 VcXsrv,直到用户在下拉里另选一项。
    /// </summary>
    public int XServerKeyboardLayoutIndex
    {
        get => Array.FindIndex(XServerKeyboardLayouts, c => c.Value == XServer.KeyboardLayout);
        set
        {
            if (value >= 0 && value < XServerKeyboardLayouts.Length)
            {
                XServer.KeyboardLayout = XServerKeyboardLayouts[value].Value;
            }
            this.RaisePropertyChanged();
        }
    }

    /// <summary>键盘型号下拉的选中项;不在表里时为 -1(同 <see cref="XServerKeyboardLayoutIndex" />)。</summary>
    public int XServerKeyboardModelIndex
    {
        get => Array.FindIndex(XServerKeyboardModels, c => c.Value == XServer.KeyboardModel);
        set
        {
            if (value >= 0 && value < XServerKeyboardModels.Length)
            {
                XServer.KeyboardModel = XServerKeyboardModels[value].Value;
            }
            this.RaisePropertyChanged();
        }
    }

    /// <summary>当前平台能不能用本机 X Server(目前只有 Windows);不能用时整页只剩一段说明。</summary>
    public bool XServerSupported => _localXServer?.IsSupported ?? false;

    /// <summary>找到的 VcXsrv 路径;没找到为 <see langword="null" />。</summary>
    public string? XServerDetectedExecutable
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(XServerExecutableFound));
            this.RaisePropertyChanged(nameof(XServerDetectionText));
        }
    }

    /// <summary>是否找到了 VcXsrv(控制「未找到 + 安装方法」那段提示的显隐)。</summary>
    public bool XServerExecutableFound => XServerDetectedExecutable is not null;

    /// <summary>可执行文件一栏下方的状态行:找到了显示实际会用的路径,没找到说清是哪种没找到。</summary>
    public string XServerDetectionText =>
        XServerDetectedExecutable is { } path
            ? Strings.Format("SetXServer_Detected", path)
            : string.IsNullOrWhiteSpace(XServer.ExecutablePath)
                ? Strings.Get("SetXServer_NotDetected")
                : Strings.Format("XServer_ErrConfiguredNotFound", XServer.ExecutablePath.Trim());

    /// <summary>重新查一遍 VcXsrv(载入设置、改了路径、点了「浏览」之后)。</summary>
    public void RefreshXServerDetection()
    {
        XServerDetectedExecutable = _localXServer?.FindExecutable(XServer.ExecutablePath) is { Length: > 0 } found
            ? found
            : null;
        // 路径没变、找没找到也没变时 setter 不会吆喝,但「没找到」的措辞取决于路径填没填。
        this.RaisePropertyChanged(nameof(XServerDetectionText));
    }

    /// <summary>
    /// 「附加参数」下方的命令行预览:本页各项翻出来的参数,就是下一次启动时实际传给 VcXsrv 的
    /// (少一个 <c>-logfile</c>,那个由程序决定写到日志目录)。自动显示号在启动时才解析,这里写成 <c>:自动</c>。
    /// </summary>
    public string XServerCommandPreview
    {
        get
        {
            List<string> args = [.. XServerCommandLine.Build(XServer, Math.Max(0, XServer.DisplayNumber))];
            if (XServer.DisplayNumber < 0)
            {
                args[0] = ":" + Strings.Get("SetXServer_DisplayAuto");
            }
            return "vcxsrv.exe " + string.Join(' ', args.Select(Quote));

            static string Quote(string arg) =>
                arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? "\"" + arg + "\"" : arg;
        }
    }

    private XServerOptions? _hookedXServer;

    /// <summary>
    /// 跟踪当前 <see cref="XServer" /> 对象的单项修改:任何一项变了命令行预览都要重算;
    /// 路径那一栏变了还要重新查找 VcXsrv(逐键查几个固定位置的文件是否存在,开销可以忽略)。
    /// </summary>
    private void HookXServer(XServerOptions? options)
    {
        _hookedXServer?.PropertyChanged -= OnXServerItemChanged;
        _hookedXServer = options;
        options?.PropertyChanged += OnXServerItemChanged;
        this.RaisePropertyChanged(nameof(XServerCommandPreview));
    }

    private void OnXServerItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(XServerOptions.ExecutablePath))
        {
            RefreshXServerDetection();
        }
        this.RaisePropertyChanged(nameof(XServerCommandPreview));
    }

    /// <summary>
    /// 换语言时重建带本地化首项(「自动」)的两张下拉表。本 VM 是单例,不重建就停在启动语言上 ——
    /// 与左侧导航(<c>BuildSections</c>)同一个原因。条目换了之后选中项要重新吆喝一次,
    /// 否则 ComboBox 在 ItemsSource 替换的那一下会把选中项清成空。
    /// </summary>
    private void RebuildXServerChoices()
    {
        XServerKeyboardLayouts = BuildKeyboardLayouts();
        XServerDisplayNumbers = BuildDisplayNumbers();
        this.RaisePropertyChanged(nameof(XServerKeyboardLayouts));
        this.RaisePropertyChanged(nameof(XServerDisplayNumbers));
        this.RaisePropertyChanged(nameof(XServerKeyboardLayoutIndex));
        this.RaisePropertyChanged(nameof(XServerDisplayNumberIndex));
        this.RaisePropertyChanged(nameof(XServerDetectionText));
        this.RaisePropertyChanged(nameof(XServerCommandPreview));
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }
}
