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

    /// <summary>
    /// 载入后的规整(由设置服务在反序列化后调用):把旧字段迁移到唯一权威字段,
    /// 保证每个行为只有一个数据来源(设置审计 C-01/M-01)。
    /// </summary>
    public void Normalize()
    {
        // 旧版独立“视觉闪烁”开关会覆盖 BellMode;迁移为三态 BellMode 后清除旧值。
        if (TerminalBehavior.VisualBell)
        {
            TerminalBehavior.BellMode = "visual";
            TerminalBehavior.VisualBell = false;
        }
    }
}

/// <summary>
/// 分组选项基类:实现 INPC,设置页直接 TwoWay 绑定选项对象时,
/// 单项修改可被其它绑定(条件显隐、即时预览)实时观察到。
/// </summary>
public abstract class ObservableOptions : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}

/// <summary>设置 - 常规(设计 2BIRD)。</summary>
public class GeneralOptions : ObservableOptions
{
    // 启动
    public bool LaunchAtStartup
    {
        get;
        set => Set(ref field, value);
    }

    public bool RestoreSessionsOnStartup
    {
        get;
        set => Set(ref field, value);
    } = true;

    public bool CheckUpdatesOnStartup
    {
        get;
        set => Set(ref field, value);
    } = true;

    public bool MinimizeToTray
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>“恢复会话”的持久化槽位(不出现在设置界面):退出时已连接会话的配置 id。</summary>
    public List<Guid> LastOpenProfileIds { get; set; } = [];

    // 连接默认值
    public int ConnectTimeoutSeconds
    {
        get;
        set => Set(ref field, value);
    } = 30;

    public int KeepAliveSeconds
    {
        get;
        set => Set(ref field, value);
    } = 60;

    /// <summary>最大自动重连次数(自动重连的唯一次数来源,设置审计 C-02/N-06)。</summary>
    public int MaxRetries
    {
        get;
        set => Set(ref field, value);
    } = 3;

    // 数据与存储
    public bool SessionLogging
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>终端会话日志保留天数(区别于文件传输日志的保留天数,设置审计 N-08/N-09)。</summary>
    public int LogRetentionDays
    {
        get;
        set => Set(ref field, value);
    } = 30;

    // 更新
    public string UpdateChannel
    {
        get;
        set => Set(ref field, value);
    } = "stable";

    public bool AutoDownloadUpdates
    {
        get;
        set => Set(ref field, value);
    } = true;

    // 行为
    /// <summary>退出应用前确认;开启“最小化到托盘”后点关闭按钮只隐藏窗口,不触发本确认(设置审计 C-03)。</summary>
    public bool ConfirmBeforeClose
    {
        get;
        set => Set(ref field, value);
    } = true;

    public bool NotifyOnDisconnect
    {
        get;
        set => Set(ref field, value);
    } = true;

    public bool AutoReconnect
    {
        get;
        set => Set(ref field, value);
    } = true;

    public int ReconnectIntervalSeconds
    {
        get;
        set => Set(ref field, value);
    } = 5;

    // 通知
    /// <summary>连接断开时播放提示音(作用域是 SSH 断开事件,设置审计 N-01)。</summary>
    public bool SoundAlerts
    {
        get;
        set => Set(ref field, value);
    }

    // 隐私与安全
    public bool MasterPasswordProtection
    {
        get;
        set => Set(ref field, value);
    }

    public bool RememberPasswords
    {
        get;
        set => Set(ref field, value);
    } = true;
}

/// <summary>
/// 设置 - 外观(设计 ZAbb9)。设置页直接 TwoWay 绑定本对象,
/// 单项修改需要能被设置 VM 观察到,用于外观「即时预览」与颜色色块的实时刷新。
/// </summary>
public class AppearanceOptions : ObservableOptions
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
}

/// <summary>设置 - 终端(设计 08FpM;字体/字号/回滚沿用 AppSettings 顶层字段)。</summary>
public class TerminalBehaviorOptions : ObservableOptions
{
    /// <summary>行间距倍数;1.0 = 字体自然行高(与历史版本渲染一致)。</summary>
    public double LineHeight
    {
        get;
        set => Set(ref field, value);
    } = 1.0;

    public string CursorStyle
    {
        get;
        set => Set(ref field, value);
    } = "bar";

    public bool CursorBlink
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>终端响铃行为(唯一权威):system(系统提示音)/ none(不提示)/ visual(视觉闪烁)。</summary>
    public string BellMode
    {
        get;
        set => Set(ref field, value);
    } = "system";

    public bool TabFlashAlert
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>
    /// 旧版“视觉闪烁”独立开关,仅作为迁移槽位保留(读旧配置用);
    /// 运行时不再消费,载入时经 <see cref="AppSettings.Normalize" /> 并入 <see cref="BellMode" />。
    /// </summary>
    public bool VisualBell { get; set; }

    /// <summary>开 = 新输出把翻看历史的视图拉回底部;关 = 保持锚定(#15 的既有行为)。</summary>
    public bool ScrollOnOutput
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>开 = 每行左侧显示 [HH:mm:ss] 收行时间(默认关,占用左侧宽度)。与 <see cref="ShowLineNumber" /> 独立。</summary>
    public bool ShowLineTimestamp
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>开 = 每行左侧显示缓冲区行号(默认关,占用左侧宽度)。与 <see cref="ShowLineTimestamp" /> 独立。</summary>
    public bool ShowLineNumber
    {
        get;
        set => Set(ref field, value);
    }

    public bool ScrollOnKeystroke
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>
    /// 选中即复制(默认开,设计 §8):开 = 松开鼠标/双击选词后选中内容自动进剪贴板;
    /// 关 = 选中只高亮不复制,复制需 Ctrl+Shift+C。
    /// </summary>
    public bool CopyOnSelect
    {
        get;
        set => Set(ref field, value);
    } = true;

    public bool RightClickPaste
    {
        get;
        set => Set(ref field, value);
    } = true;

    public bool TrimTrailingWhitespaceOnCopy
    {
        get;
        set => Set(ref field, value);
    } = true;

    public bool DoubleClickSelectsWord
    {
        get;
        set => Set(ref field, value);
    } = true;

    public bool ConfirmMultilinePaste
    {
        get;
        set => Set(ref field, value);
    } = true;

    public bool ImeSupport
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>
    /// 选中时 Ctrl+C 复制(默认关):开 = 有选区时 Ctrl+C 复制选中内容而不发送中断,
    /// 无选区仍发送中断;关 = Ctrl+C 始终发送中断信号 ^C。
    /// </summary>
    public bool CtrlCCopiesWhenSelected
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>
    /// 连接/重连成功后追加执行的用户初始化命令(空 = 无)。它会被拼接在内置的
    /// bash 提示符补行脚本之后,一并静默注入远端 shell(回显被抑制,不在终端显示)。
    /// </summary>
    public string StartupCommand
    {
        get;
        set => Set(ref field, value);
    } = "";
}

/// <summary>设置 - 文件传输(设计 HGwa7)。</summary>
public class TransferOptions : ObservableOptions
{
    public string LocalDownloadDirectory
    {
        get;
        set => Set(ref field, value);
    } = "~/Downloads";

    public int MaxConcurrentTransfers
    {
        get;
        set => Set(ref field, value);
    } = 3;

    public bool PreserveTimestamps
    {
        get;
        set => Set(ref field, value);
    } = true;

    public bool NotifyOnComplete
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>ask / overwrite / skip / rename。</summary>
    public string ConflictPolicy
    {
        get;
        set => Set(ref field, value);
    } = "ask";

    /// <summary>显示隐藏文件:文件浏览器工具栏切换会写回本设置(设置审计 C-04)。</summary>
    public bool ShowHiddenFiles
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>规划中(断点续传):仅持久化,当前无运行时消费者,不出现在设置界面(设置审计 M-02/R-07)。</summary>
    public bool AutoResume { get; set; } = true;

    public bool BandwidthLimitEnabled
    {
        get;
        set => Set(ref field, value);
    }

    public int UploadLimitMBps
    {
        get;
        set => Set(ref field, value);
    }

    public int DownloadLimitMBps
    {
        get;
        set => Set(ref field, value);
    }

    public bool TransferLogging
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>文件传输日志保留天数(区别于终端会话日志的保留天数,设置审计 N-08/N-09)。</summary>
    public int TransferLogRetentionDays
    {
        get;
        set => Set(ref field, value);
    } = 30;

    public string LogDirectory
    {
        get;
        set => Set(ref field, value);
    } = "~/.velashell/logs";

    /// <summary>规划中(断点续传):仅持久化,当前无运行时消费者,不出现在设置界面(设置审计 M-02/R-08)。</summary>
    public bool ResumeEnabled { get; set; } = true;

    /// <summary>规划中(传输失败重试):仅持久化,当前无运行时消费者,不出现在设置界面(设置审计 R-09)。</summary>
    public int TransferMaxRetries { get; set; } = 3;

    /// <summary>规划中(临时文件清理):仅持久化,当前无运行时消费者,不出现在设置界面(设置审计 R-10)。</summary>
    public bool AutoCleanTempFiles { get; set; } = true;

    /// <summary>
    /// SFTP「使用默认编辑器打开」调用的程序(命令名或完整路径,如 notepad、
    /// notepad++、"C:\Program Files\Notepad++\notepad++.exe")。空 = 未配置。
    /// </summary>
    public string DefaultEditorPath
    {
        get;
        set => Set(ref field, value);
    } = "";
}

/// <summary>设置 - 安全审计(设计 glqQE;策略项持久化,审计数据在 SonnetDB audit_log)。</summary>
public class SecurityOptions : ObservableOptions
{
    /// <summary>规划中(会话录制):仅持久化,当前无运行时消费者,不出现在设置界面(设置审计 R-11)。</summary>
    public bool RecordProductionSessions { get; set; } = true;

    /// <summary>规划中(输入脱敏,依赖会话录制):仅持久化,不出现在设置界面(设置审计 R-12)。</summary>
    public bool MaskSensitiveInput { get; set; } = true;

    public bool ConfirmFirstFingerprint
    {
        get;
        set => Set(ref field, value);
    }

    public bool BlockOnFingerprintChange
    {
        get;
        set => Set(ref field, value);
    } = true;

    public bool AlertInApp
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>安全事件发生时播放系统提示音(作用域是主机指纹等安全事件,设置审计 N-02)。</summary>
    public bool AlertSound
    {
        get;
        set => Set(ref field, value);
    } = true;

    public bool AlertWebhook
    {
        get;
        set => Set(ref field, value);
    }

    public string WebhookUrl
    {
        get;
        set => Set(ref field, value);
    } = "";
}

/// <summary>设置 - 密钥管理(设计 UBP59)。</summary>
public class KeyOptions : ObservableOptions
{
    /// <summary>
    /// 新建连接时默认使用的密钥名称(~/.ssh 下的文件名)。null 归一为空串:
    /// “默认认证密钥”下拉在列表未就位/密钥被删除时会把 SelectedItem=null 写回来,
    /// 不允许它破坏模型的非空不变量。
    /// </summary>
    public string DefaultKeyName
    {
        get;
        set => Set(ref field, value ?? "");
    } = "";

    /// <summary>规划中(ssh-agent 集成):仅持久化,当前无运行时消费者,不出现在设置界面(设置审计 R-06)。</summary>
    public bool AutoLoadToAgent { get; set; } = true;
}
