using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VelaShell.Core.Models;

public class AppSettings
{
    public string Language { get; set; } = "zh-CN";

    public string Theme { get; set; } = "dark";

    /// <summary>Accent-color override as a hex string (e.g. "#00D4AA"); empty = use theme default.</summary>
    public string AccentColor { get; set; } = "";

    public string TerminalFont { get; set; } = "JetBrains Mono";

    public int TerminalFontSize { get; set; } = 14;

    public int ScrollbackLines { get; set; } = 50000;

    public int DefaultPort { get; set; } = 22;

    /// <summary>Terminal emulation profile advertised as TERM (default xterm-256color).</summary>
    public string TerminalType { get; set; } = "xterm-256color";

    /// <summary>Character encoding used to decode host output (default UTF-8).</summary>
    public string TerminalEncoding { get; set; } = "UTF-8";

    // —— 设计 §14 各页面的分组选项(SonnetDB app_config 文档,JSON 嵌套) ——
    // 部分选项当前仅持久化,由后续功能消费。

    public GeneralOptions General { get; set; } = new();

    public AppearanceOptions Appearance { get; set; } = new();

    public TerminalBehaviorOptions TerminalBehavior { get; set; } = new();

    public TransferOptions Transfer { get; set; } = new();

    public SecurityOptions Security { get; set; } = new();

    public KeyOptions Keys { get; set; } = new();
}

/// <summary>设置 - 常规(设计 2BIRD)。</summary>
public class GeneralOptions
{
    // 启动
    public bool LaunchAtStartup { get; set; }

    public bool RestoreSessionsOnStartup { get; set; } = true;

    public bool CheckUpdatesOnStartup { get; set; } = true;

    public bool MinimizeToTray { get; set; }

    /// <summary>“恢复会话”的持久化槽位(不出现在设置界面):退出时已连接会话的配置 id。</summary>
    public List<Guid> LastOpenProfileIds { get; set; } = [];

    // 连接默认值
    public int ConnectTimeoutSeconds { get; set; } = 30;

    public int KeepAliveSeconds { get; set; } = 60;

    public int MaxRetries { get; set; } = 3;

    // 数据与存储
    public bool SessionLogging { get; set; }

    public int LogRetentionDays { get; set; } = 30;

    // 更新
    public string UpdateChannel { get; set; } = "stable";

    public bool AutoDownloadUpdates { get; set; } = true;

    // 行为
    public bool ConfirmBeforeClose { get; set; } = true;

    public bool NotifyOnDisconnect { get; set; } = true;

    public bool AutoReconnect { get; set; } = true;

    public int ReconnectIntervalSeconds { get; set; } = 5;

    // 通知
    public bool SoundAlerts { get; set; }

    // 隐私与安全
    public bool MasterPasswordProtection { get; set; }

    public bool RememberPasswords { get; set; } = true;
}

/// <summary>
/// 设置 - 外观(设计 ZAbb9)。实现 INPC:设置页直接 TwoWay 绑定本对象,
/// 单项修改需要能被设置 VM 观察到,用于外观「即时预览」与颜色色块的实时刷新。
/// </summary>
public class AppearanceOptions : INotifyPropertyChanged
{
    public string UiFont
    {
        get;
        set => Set(ref field, value);
    } = "Inter";

    public int UiFontSize
    {
        get;
        set => Set(ref field, value);
    } = 13;

    public int WindowOpacityPercent
    {
        get;
        set => Set(ref field, value);
    } = 100;

    public string TabBarPosition
    {
        get;
        set => Set(ref field, value);
    } = "top";

    public bool ShowMenuBar
    {
        get;
        set => Set(ref field, value);
    } = true;

    public string SidebarPosition
    {
        get;
        set => Set(ref field, value);
    } = "left";

    public string StartupWindowState
    {
        get;
        set => Set(ref field, value);
    } = "remember";

    // “记住上次”窗口状态的持久化槽位(不出现在设置界面,由主窗口关闭时回写)。
    public double LastWindowWidth
    {
        get;
        set => Set(ref field, value);
    }

    public double LastWindowHeight
    {
        get;
        set => Set(ref field, value);
    }

    public bool LastWindowMaximized
    {
        get;
        set => Set(ref field, value);
    }

    // 终端颜色(默认 = Dracula 官方 Windows Terminal 方案,用户确认)
    public string TerminalForeground
    {
        get;
        set => Set(ref field, value);
    } = "#F8F8F2";

    public string TerminalBackground
    {
        get;
        set => Set(ref field, value);
    } = "#282A36";

    public string CursorColor
    {
        get;
        set => Set(ref field, value);
    } = "#F8F8F2";

    public string SelectionColor
    {
        get;
        set => Set(ref field, value);
    } = "#44475A";

    public List<string> AnsiNormal
    {
        get;
        set => Set(ref field, value);
    } = ["#21222C", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2"];

    public List<string> AnsiBright
    {
        get;
        set => Set(ref field, value);
    } = ["#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF"];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}

/// <summary>设置 - 终端(设计 08FpM;字体/字号/回滚沿用 AppSettings 顶层字段)。</summary>
public class TerminalBehaviorOptions
{
    /// <summary>行间距倍数;1.0 = 字体自然行高(与历史版本渲染一致)。</summary>
    public double LineHeight { get; set; } = 1.0;

    public string CursorStyle { get; set; } = "bar";

    public bool CursorBlink { get; set; } = true;

    public string BellMode { get; set; } = "system";

    public bool TabFlashAlert { get; set; } = true;

    public bool VisualBell { get; set; }

    /// <summary>开 = 新输出把翻看历史的视图拉回底部;关 = 保持锚定(#15 的既有行为)。</summary>
    public bool ScrollOnOutput { get; set; }

    public bool ScrollOnKeystroke { get; set; } = true;

    /// <summary>
    /// 选中即复制(默认开,设计 §8):开 = 松开鼠标/双击选词后选中内容自动进剪贴板;
    /// 关 = 选中只高亮不复制,复制需 Ctrl+Shift+C。
    /// </summary>
    public bool CopyOnSelect { get; set; } = true;

    public bool RightClickPaste { get; set; } = true;

    public bool TrimTrailingWhitespaceOnCopy { get; set; } = true;

    public bool DoubleClickSelectsWord { get; set; } = true;

    public bool ConfirmMultilinePaste { get; set; } = true;

    public bool ImeSupport { get; set; } = true;

    /// <summary>
    /// 选中时 Ctrl+C 复制(默认关):开 = 有选区时 Ctrl+C 复制选中内容而不发送中断,
    /// 无选区仍发送中断;关 = Ctrl+C 始终发送中断信号 ^C。
    /// </summary>
    public bool CtrlCCopiesWhenSelected { get; set; }

    /// <summary>
    /// 连接/重连成功后追加执行的用户初始化命令(空 = 无)。它会被拼接在内置的
    /// bash 提示符补行脚本之后,一并静默注入远端 shell(回显被抑制,不在终端显示)。
    /// </summary>
    public string StartupCommand { get; set; } = "";
}

/// <summary>设置 - 文件传输(设计 HGwa7)。</summary>
public class TransferOptions
{
    public string LocalDownloadDirectory { get; set; } = "~/Downloads";

    public int MaxConcurrentTransfers { get; set; } = 3;

    public bool PreserveTimestamps { get; set; } = true;

    public bool NotifyOnComplete { get; set; } = true;

    /// <summary>ask / overwrite / skip / rename。</summary>
    public string ConflictPolicy { get; set; } = "ask";

    public bool ShowHiddenFiles { get; set; }

    public bool AutoResume { get; set; } = true;

    public bool BandwidthLimitEnabled { get; set; }

    public int UploadLimitMBps { get; set; }

    public int DownloadLimitMBps { get; set; }

    public bool TransferLogging { get; set; } = true;

    public int TransferLogRetentionDays { get; set; } = 30;

    public string LogDirectory { get; set; } = "~/.velashell/logs";

    public bool ResumeEnabled { get; set; } = true;

    public int TransferMaxRetries { get; set; } = 3;

    public bool AutoCleanTempFiles { get; set; } = true;

    /// <summary>
    /// SFTP「使用默认编辑器打开」调用的程序(命令名或完整路径,如 notepad、
    /// notepad++、"C:\Program Files\Notepad++\notepad++.exe")。空 = 未配置。
    /// </summary>
    public string DefaultEditorPath { get; set; } = "";
}

/// <summary>设置 - 安全审计(设计 glqQE;策略项持久化,审计数据在 SonnetDB audit_log)。</summary>
public class SecurityOptions
{
    public bool RecordProductionSessions { get; set; } = true;

    public bool MaskSensitiveInput { get; set; } = true;

    public bool ConfirmFirstFingerprint { get; set; }

    public bool BlockOnFingerprintChange { get; set; } = true;

    public bool AlertInApp { get; set; } = true;

    /// <summary>安全事件时播放系统提示音。</summary>
    public bool AlertSound { get; set; } = true;

    public bool AlertWebhook { get; set; }

    public string WebhookUrl { get; set; } = "";
}

/// <summary>设置 - 密钥管理(设计 UBP59)。</summary>
public class KeyOptions
{
    /// <summary>新建连接时默认使用的密钥名称(~/.ssh 下的文件名)。</summary>
    public string DefaultKeyName { get; set; } = "";

    public bool AutoLoadToAgent { get; set; } = true;
}
