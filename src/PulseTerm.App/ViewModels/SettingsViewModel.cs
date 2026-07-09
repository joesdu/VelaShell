using System;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using PulseTerm.Core.Data;
using PulseTerm.Core.Localization;
using PulseTerm.Core.Models;
using PulseTerm.Core.Services;
using PulseTerm.Core.Ssh;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

/// <summary>设置左侧导航项(图标为 PathIcon 几何)。</summary>
public sealed record SettingsSection(string Name, string Icon);

/// <summary>关于页的开源依赖条目(项目主页 + 许可证页面可点击跳转)。</summary>
public sealed record DependencyInfo(string Name, string License, string Url, string LicenseUrl);

/// <summary>快捷键页的分组与条目(只读展示,自定义键位后续版本提供)。</summary>
public sealed record ShortcutGroup(string Title, ShortcutItem[] Items);

public sealed record ShortcutItem(string Label, string[] Keys);

public class SettingsViewModel : ReactiveObject
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly ILocalizationService? _localizationService;

    private AppSettings _loaded = new();

    private string _language = "en";
    private string _theme = "dark";
    private string _accentColor = "";
    private int _selectedSectionIndex;
    private string _terminalFont = "JetBrains Mono";
    private int _terminalFontSize = 14;
    private int _scrollbackLines = 10000;
    private int _defaultPort = 22;
    private string _terminalType = "xterm-256color";
    private string _terminalEncoding = "UTF-8";
    private GeneralOptions _general = new();
    private AppearanceOptions _appearance = new();
    private TerminalBehaviorOptions _terminalBehavior = new();
    private TransferOptions _transfer = new();
    private SecurityOptions _security = new();
    private KeyOptions _keys = new();

    private readonly IRecentConnectionService? _recentConnections;
    private string _updateStatus = string.Empty;
    private string _clearHistoryStatus = string.Empty;

    public SettingsViewModel(
        ISettingsService settingsService,
        IThemeService themeService,
        ILocalizationService? localizationService = null,
        IAppDataStore? appDataStore = null,
        ISshKeyService? sshKeyService = null,
        IRecentConnectionService? recentConnections = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _localizationService = localizationService;
        _recentConnections = recentConnections;

        SshKeys = new SshKeyManagerViewModel(sshKeyService);
        Snippets = appDataStore is null ? null : new QuickCommandsViewModel(appDataStore);

        LoadCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        ResetCommand = ReactiveCommand.Create(ResetToDefaults);
        SetAccentCommand = ReactiveCommand.Create<string>(hex => AccentColor = hex);
        ClearHistoryCommand = ReactiveCommand.CreateFromTask(ClearHistoryAsync);
        CheckUpdatesCommand = ReactiveCommand.Create(() =>
        {
            UpdateStatus = "当前已是最新版本";
        });
    }

    /// <summary>保存/取消后由窗口关闭。</summary>
    public event EventHandler? CloseRequested;

    // ———— 顶层字段(既有行为:保存后立即生效) ————

    public string Language
    {
        get => _language;
        set => this.RaiseAndSetIfChanged(ref _language, value);
    }

    public string Theme
    {
        get => _theme;
        set => this.RaiseAndSetIfChanged(ref _theme, value);
    }

    public string TerminalFont
    {
        get => _terminalFont;
        set => this.RaiseAndSetIfChanged(ref _terminalFont, value);
    }

    public int TerminalFontSize
    {
        get => _terminalFontSize;
        set => this.RaiseAndSetIfChanged(ref _terminalFontSize, value);
    }

    public int ScrollbackLines
    {
        get => _scrollbackLines;
        set => this.RaiseAndSetIfChanged(ref _scrollbackLines, value);
    }

    public int DefaultPort
    {
        get => _defaultPort;
        set => this.RaiseAndSetIfChanged(ref _defaultPort, value);
    }

    public string TerminalType
    {
        get => _terminalType;
        set => this.RaiseAndSetIfChanged(ref _terminalType, value);
    }

    public string TerminalEncoding
    {
        get => _terminalEncoding;
        set => this.RaiseAndSetIfChanged(ref _terminalEncoding, value);
    }

    /// <summary>Accent-color override (hex, e.g. "#00D4AA"); empty uses the theme default.</summary>
    public string AccentColor
    {
        get => _accentColor;
        set => this.RaiseAndSetIfChanged(ref _accentColor, value);
    }

    // ———— 分组选项(设计 §14 各页;POCO 直接 TwoWay 绑定) ————

    public GeneralOptions General
    {
        get => _general;
        private set => this.RaiseAndSetIfChanged(ref _general, value);
    }

    public AppearanceOptions Appearance
    {
        get => _appearance;
        private set => this.RaiseAndSetIfChanged(ref _appearance, value);
    }

    public TerminalBehaviorOptions TerminalBehavior
    {
        get => _terminalBehavior;
        private set => this.RaiseAndSetIfChanged(ref _terminalBehavior, value);
    }

    public TransferOptions Transfer
    {
        get => _transfer;
        private set => this.RaiseAndSetIfChanged(ref _transfer, value);
    }

    public SecurityOptions Security
    {
        get => _security;
        private set => this.RaiseAndSetIfChanged(ref _security, value);
    }

    public KeyOptions Keys
    {
        get => _keys;
        private set => this.RaiseAndSetIfChanged(ref _keys, value);
    }

    /// <summary>密钥管理页。</summary>
    public SshKeyManagerViewModel SshKeys { get; }

    /// <summary>代码片段页(quick_commands 集合);无存储时为 null。</summary>
    public QuickCommandsViewModel? Snippets { get; }

    /// <summary>Left-nav sections per design §14.</summary>
    public SettingsSection[] Sections { get; } =
    [
        new("常规", "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"),
        new("外观", "M12 3c-4.97 0-9 4.03-9 9s4.03 9 9 9c.83 0 1.5-.67 1.5-1.5 0-.39-.15-.74-.39-1.01-.23-.26-.38-.61-.38-.99 0-.83.67-1.5 1.5-1.5H16c2.76 0 5-2.24 5-5 0-4.42-4.03-8-9-8zm-5.5 9c-.83 0-1.5-.67-1.5-1.5S5.67 9 6.5 9 8 9.67 8 10.5 7.33 12 6.5 12zm3-4C8.67 8 8 7.33 8 6.5S8.67 5 9.5 5s1.5.67 1.5 1.5S10.33 8 9.5 8zm5 0c-.83 0-1.5-.67-1.5-1.5S13.67 5 14.5 5s1.5.67 1.5 1.5S15.33 8 14.5 8zm3 4c-.83 0-1.5-.67-1.5-1.5S16.67 9 17.5 9s1.5.67 1.5 1.5-.67 1.5-1.5 1.5z"),
        new("终端", "M20 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 14H4V8h16v10zm-2-1h-6v-2h6v2zM7.5 17l-1.41-1.41L8.67 13l-2.59-2.59L7.5 9l4 4-4 4z"),
        new("密钥管理", "M12.65 10C11.83 7.67 9.61 6 7 6c-3.31 0-6 2.69-6 6s2.69 6 6 6c2.61 0 4.83-1.67 5.65-4H17v4h4v-4h2v-4H12.65zM7 14c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2z"),
        new("快捷键", "M20 5H4c-1.1 0-1.99.9-1.99 2L2 17c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm-9 3h2v2h-2V8zm0 3h2v2h-2v-2zM8 8h2v2H8V8zm0 3h2v2H8v-2zm-1 2H5v-2h2v2zm0-3H5V8h2v2zm9 7H8v-2h8v2zm0-4h-2v-2h2v2zm0-3h-2V8h2v2zm3 3h-2v-2h2v2zm0-3h-2V8h2v2z"),
        new("文件传输", "M16 17.01V10h-2v7.01h-3L15 21l4-3.99h-3zM9 3 5 6.99h3V14h2V6.99h3L9 3z"),
        new("安全审计", "M12 1 3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4z"),
        new("代码片段", "M9.4 16.6 4.8 12l4.6-4.6L8 6l-6 6 6 6 1.4-1.4zm5.2 0 4.6-4.6-4.6-4.6L16 6l6 6-6 6-1.4-1.4z"),
        new("关于", "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z"),
    ];

    public int SelectedSectionIndex
    {
        get => _selectedSectionIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedSectionIndex, value);
    }

    public string[] AvailableLanguages { get; } = ["en", "zh-CN"];

    public string[] AvailableThemes { get; } = ["dark", "light", "system"];

    // xterm-256color is the primary/recommended profile and is listed first.
    public string[] AvailableTerminalTypes { get; } =
    [
        "xterm-256color", "xterm", "vt520", "vt420", "vt340", "vt320", "vt220", "vt102", "vt100", "vt52"
    ];

    public string[] AvailableEncodings { get; } =
    [
        "UTF-8", "GBK", "GB18030", "Big5", "Shift_JIS", "EUC-KR", "ISO-8859-1"
    ];

    public string[] AvailableUpdateChannels { get; } = ["stable", "preview"];

    public string[] AvailableCursorStyles { get; } = ["bar", "block", "underline"];

    public string[] AvailableBellModes { get; } = ["system", "none", "visual"];

    public string[] AvailableConflictPolicies { get; } = ["ask", "overwrite", "skip", "rename"];

    public string[] AvailableTabBarPositions { get; } = ["top", "bottom"];

    public string[] AvailableSidebarPositions { get; } = ["left", "right"];

    public string[] AvailableWindowStates { get; } = ["remember", "maximized", "default"];

    // ———— 关于页(真实构建信息) ————

    public string AppVersion => "v0.0.5-beta";
    public string AboutFramework => "Avalonia UI 12.0.5";
    public string AboutRuntime => $".NET {Environment.Version.Major}.{Environment.Version.Minor}";
    public string AboutSshLibrary => "SSH.NET 2025.1.0";
    public string AboutOs => $"{Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})";
    public string AboutConfigPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseTerm");

    /// <summary>开源依赖(真实技术栈)。</summary>
    public DependencyInfo[] AboutDependencies { get; } =
    [
        new("Avalonia UI", "MIT", "https://github.com/AvaloniaUI/Avalonia", "https://github.com/AvaloniaUI/Avalonia/blob/main/licence.md"),
        new("SSH.NET", "MIT", "https://github.com/sshnet/SSH.NET", "https://github.com/sshnet/SSH.NET/blob/develop/LICENSE"),
        new("Dock.Avalonia", "MIT", "https://github.com/wieslawsoltes/Dock", "https://github.com/wieslawsoltes/Dock/blob/master/LICENSE.TXT"),
        new("ReactiveUI", "MIT", "https://github.com/reactiveui/ReactiveUI", "https://github.com/reactiveui/ReactiveUI/blob/main/LICENSE"),
        new("SonnetDB", "MIT", "https://github.com/IoTSharp/SonnetDB", "https://github.com/IoTSharp/SonnetDB/blob/main/LICENSE"),
        new("Velopack", "MIT", "https://github.com/velopack/velopack", "https://github.com/velopack/velopack/blob/develop/LICENSE"),
    ];

    public ReactiveCommand<Unit, Unit> LoadCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetCommand { get; }
    public ReactiveCommand<string, Unit> SetAccentCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckUpdatesCommand { get; }

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => this.RaiseAndSetIfChanged(ref _updateStatus, value);
    }

    public string ClearHistoryStatus
    {
        get => _clearHistoryStatus;
        private set => this.RaiseAndSetIfChanged(ref _clearHistoryStatus, value);
    }

    /// <summary>外观页强调色色板(设计 ZAbb9)。</summary>
    public string[] AccentSwatches { get; } =
        ["#00D4AA", "#3498DB", "#9B59B6", "#E74C3C", "#F39C12", "#1ABC9C", "#E91E63"];

    // ———— 终端配色方案预设(§12.5) ————

    public string[] AvailableColorSchemes { get; } =
        TerminalColorScheme.BuiltIn.Select(s => s.Name).ToArray();

    private int _colorSchemeIndex = -1;

    /// <summary>选择预设即把整套颜色写入 Appearance(保存后生效);-1 = 未选择。
    /// 载入设置时复位,不做反向匹配(用户可能自定义过单色)。</summary>
    public int ColorSchemeIndex
    {
        get => _colorSchemeIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _colorSchemeIndex, value);
            if (value >= 0 && value < TerminalColorScheme.BuiltIn.Length)
            {
                // 克隆后整体替换:引用变化才能保证 Appearance.X 路径绑定全部刷新。
                var updated = System.Text.Json.JsonSerializer.Deserialize<AppearanceOptions>(
                    System.Text.Json.JsonSerializer.Serialize(Appearance)) ?? new AppearanceOptions();
                TerminalColorScheme.BuiltIn[value].ApplyTo(updated);
                Appearance = updated;
            }
        }
    }

    /// <summary>快捷键页分组(设计 YQvri;只读展示)。</summary>
    public ShortcutGroup[] ShortcutGroups { get; } =
    [
        new("常规",
        [
            new("新建连接", ["Ctrl", "N"]),
            new("打开设置", ["Ctrl", ","]),
            new("命令面板", ["Ctrl", "K"]),
            new("关闭标签页", ["Ctrl", "W"]),
        ]),
        new("终端",
        [
            new("复制", ["Ctrl", "Shift", "C"]),
            new("粘贴", ["Ctrl", "Shift", "V"]),
            new("搜索终端内容", ["Ctrl", "F"]),
            new("清屏", ["Ctrl", "L"]),
        ]),
        new("会话管理",
        [
            new("新建连接", ["Ctrl", "N"]),
            new("断开当前连接", ["Ctrl", "D"]),
            new("重新连接", ["Ctrl", "Shift", "R"]),
            new("切换到上一个会话", ["Ctrl", "Tab"]),
        ]),
        new("文件操作",
        [
            new("上传文件", ["Ctrl", "U"]),
            new("下载文件", ["Ctrl", "Shift", "D"]),
            new("切换文件浏览器", ["Ctrl", "B"]),
        ]),
        new("窗口管理",
        [
            new("水平分屏", ["Ctrl", "Shift", "H"]),
            new("垂直分屏", ["Ctrl", "Shift", "V"]),
            new("关闭当前标签", ["Ctrl", "W"]),
            new("全屏切换", ["F11"]),
            new("切换侧边栏", ["Ctrl", "\\"]),
        ]),
    ];

    // ———— 下拉的索引映射(POCO 字符串 ↔ ComboBox SelectedIndex) ————

    public int ThemeIndex
    {
        get => Theme switch { "light" => 1, "system" => 2, _ => 0 };
        set
        {
            Theme = value switch { 1 => "light", 2 => "system", _ => "dark" };
            this.RaisePropertyChanged();
        }
    }

    public int LanguageIndex
    {
        get => Language == "en" ? 1 : 0;
        set
        {
            Language = value == 1 ? "en" : "zh-CN";
            this.RaisePropertyChanged();
        }
    }

    public int UpdateChannelIndex
    {
        get => General.UpdateChannel == "preview" ? 1 : 0;
        set { General.UpdateChannel = value == 1 ? "preview" : "stable"; this.RaisePropertyChanged(); }
    }

    public int CursorStyleIndex
    {
        get => TerminalBehavior.CursorStyle switch { "block" => 1, "underline" => 2, _ => 0 };
        set
        {
            TerminalBehavior.CursorStyle = value switch { 1 => "block", 2 => "underline", _ => "bar" };
            this.RaisePropertyChanged();
        }
    }

    public int BellModeIndex
    {
        get => TerminalBehavior.BellMode switch { "none" => 1, "visual" => 2, _ => 0 };
        set
        {
            TerminalBehavior.BellMode = value switch { 1 => "none", 2 => "visual", _ => "system" };
            this.RaisePropertyChanged();
        }
    }

    public int ConflictPolicyIndex
    {
        get => Transfer.ConflictPolicy switch { "overwrite" => 1, "skip" => 2, "rename" => 3, _ => 0 };
        set
        {
            Transfer.ConflictPolicy = value switch { 1 => "overwrite", 2 => "skip", 3 => "rename", _ => "ask" };
            this.RaisePropertyChanged();
        }
    }

    public int TabBarPositionIndex
    {
        get => Appearance.TabBarPosition == "bottom" ? 1 : 0;
        set { Appearance.TabBarPosition = value == 1 ? "bottom" : "top"; this.RaisePropertyChanged(); }
    }

    public int SidebarPositionIndex
    {
        get => Appearance.SidebarPosition == "right" ? 1 : 0;
        set { Appearance.SidebarPosition = value == 1 ? "right" : "left"; this.RaisePropertyChanged(); }
    }

    public int WindowStateIndex
    {
        get => Appearance.StartupWindowState switch { "maximized" => 1, "default" => 2, _ => 0 };
        set
        {
            Appearance.StartupWindowState = value switch { 1 => "maximized", 2 => "default", _ => "remember" };
            this.RaisePropertyChanged();
        }
    }

    /// <summary>文件夹选择器直接改写 Transfer POCO 后,通知绑定刷新。</summary>
    public void RaisePropertyChangedForTransfer() => this.RaisePropertyChanged(nameof(Transfer));

    /// <summary>常规页“清除历史记录”:清空 SonnetDB 连接历史。</summary>
    private async Task ClearHistoryAsync()
    {
        if (_recentConnections is null)
        {
            return;
        }

        try
        {
            await _recentConnections.ClearAsync();
            ClearHistoryStatus = "已清除最近连接记录。";
        }
        catch (Exception ex)
        {
            ClearHistoryStatus = $"清除失败:{ex.Message}";
        }
    }

    /// <summary>配置导出为 JSON 文本(常规页“导出”)。</summary>
    public string BuildExportJson()
        => System.Text.Json.JsonSerializer.Serialize(_loaded, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });

    /// <summary>从导出的 JSON 导入配置(常规页“导入”);格式非法时返回 false。</summary>
    public bool TryApplyImportedJson(string json)
    {
        try
        {
            var imported = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true,
                });
            if (imported is null)
            {
                return false;
            }

            _loaded = imported;
            ApplyToViewModel(imported);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private async Task LoadAsync()
    {
        _loaded = await _settingsService.GetSettingsAsync();
        ApplyToViewModel(_loaded);

        await SshKeys.RefreshAsync();
        if (Snippets is not null && Snippets.AllCommands.Count <= 10)
        {
            await Snippets.LoadCustomCommandsAsync();
        }
    }

    private void ApplyToViewModel(AppSettings settings)
    {
        Language = settings.Language;
        Theme = settings.Theme;
        AccentColor = settings.AccentColor;
        TerminalFont = settings.TerminalFont;
        TerminalFontSize = settings.TerminalFontSize;
        ScrollbackLines = settings.ScrollbackLines;
        DefaultPort = settings.DefaultPort;
        TerminalType = settings.TerminalType;
        TerminalEncoding = settings.TerminalEncoding;
        General = settings.General;
        Appearance = settings.Appearance;
        TerminalBehavior = settings.TerminalBehavior;
        Transfer = settings.Transfer;
        Security = settings.Security;
        Keys = settings.Keys;

        // 配色方案下拉复位为“未选择”(不反向匹配,用户可能自定义过单色)。
        _colorSchemeIndex = -1;
        this.RaisePropertyChanged(nameof(ColorSchemeIndex));

        // 分组对象整体替换后,派生的下拉索引一并刷新。
        this.RaisePropertyChanged(nameof(ThemeIndex));
        this.RaisePropertyChanged(nameof(LanguageIndex));
        this.RaisePropertyChanged(nameof(UpdateChannelIndex));
        this.RaisePropertyChanged(nameof(CursorStyleIndex));
        this.RaisePropertyChanged(nameof(BellModeIndex));
        this.RaisePropertyChanged(nameof(ConflictPolicyIndex));
        this.RaisePropertyChanged(nameof(TabBarPositionIndex));
        this.RaisePropertyChanged(nameof(SidebarPositionIndex));
        this.RaisePropertyChanged(nameof(WindowStateIndex));
    }

    /// <summary>恢复默认(常规页按钮):所有设置回到出厂值,保存后生效。</summary>
    private void ResetToDefaults()
    {
        _loaded = new AppSettings();
        ApplyToViewModel(_loaded);
    }

    private async Task SaveAsync()
    {
        // 分组选项对象被绑定直接修改;顶层字段从 VM 回写。
        _loaded.Language = Language;
        _loaded.Theme = Theme;
        _loaded.AccentColor = AccentColor;
        _loaded.TerminalFont = TerminalFont;
        _loaded.TerminalFontSize = TerminalFontSize;
        _loaded.ScrollbackLines = ScrollbackLines;
        _loaded.DefaultPort = DefaultPort;
        _loaded.TerminalType = TerminalType;
        _loaded.TerminalEncoding = TerminalEncoding;
        _loaded.General = General;
        _loaded.Appearance = Appearance;
        _loaded.TerminalBehavior = TerminalBehavior;
        _loaded.Transfer = Transfer;
        _loaded.Security = Security;
        _loaded.Keys = Keys;

        await _settingsService.SaveSettingsAsync(_loaded);

        // Apply live — theme, accent and language all take effect without restart (#2/#3/#4).
        _themeService.SetTheme(Theme);
        try { _themeService.SetAccent(AccentColor); } catch (ArgumentException) { /* invalid hex: keep previous */ }
        _localizationService?.SetLanguage(Language);

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
