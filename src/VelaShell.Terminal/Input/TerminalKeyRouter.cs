using Avalonia.Input;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Input;

/// <summary>一次按键在终端里应执行的动作类别。</summary>
public enum TerminalKeyActionKind
{
    /// <summary>无动作:按键不产生字节也不命中任何快捷键,交还基类。</summary>
    None,

    /// <summary>IME 组字中间态(ImeProcessed):不得编码,已提交文本会经 TextInput 单独送达。</summary>
    ImePassthrough,

    /// <summary>复制选区到剪贴板(Ctrl+Shift+C,或"选中时 Ctrl+C 复制"命中)。</summary>
    CopySelection,

    /// <summary>粘贴剪贴板(Ctrl+Shift+V / Shift+Insert)。</summary>
    PasteClipboard,

    /// <summary>翻动回滚历史(PageUp/PageDown),方向见 <see cref="TerminalKeyAction.ScrollPageDirection" />。</summary>
    ScrollHistory,

    /// <summary>把 <see cref="TerminalKeyAction.Bytes" /> 作为键入发送往 PTY。</summary>
    SendBytes
}

/// <summary>按键分类结果:动作类别 + 随类别携带的载荷。</summary>
/// <param name="Kind">动作类别。</param>
/// <param name="ScrollPageDirection">仅 <see cref="TerminalKeyActionKind.ScrollHistory" />:+1 向上翻页,-1 向下。</param>
/// <param name="Bytes">仅 <see cref="TerminalKeyActionKind.SendBytes" />:应发送的编码字节。</param>
public readonly record struct TerminalKeyAction(
    TerminalKeyActionKind Kind,
    int ScrollPageDirection = 0,
    byte[]? Bytes = null)
{
    /// <summary>便捷工厂。</summary>
    public static readonly TerminalKeyAction None = new(TerminalKeyActionKind.None);
}

/// <summary>
/// 键盘输入路由:把一次按键(键 + 修饰键 + 终端状态)分类成应执行的动作。
/// 纯决策、零副作用 —— 快捷键优先级、修饰键改写(Shift+Home/End 剥 Shift)与
/// 按键编码策略全部集中在这里,可脱离 Avalonia 控件单测;
/// <c>VelaTerminalControl.OnKeyDown</c> 只按分类结果执行。
/// </summary>
/// <remarks>
/// 决策顺序即优先级,与历史行为逐条对应:IME 透传 → 剪贴板快捷键 → Shift+Insert 粘贴
/// → 翻页 → 选中时 Ctrl+C 复制 → 编码发送。改动顺序前先想清楚谁该赢
/// (例如 Shift+Insert 必须先于编码器,否则会被编成 CSI 2~ 发出去)。
/// </remarks>
public static class TerminalKeyRouter
{
    /// <summary>
    /// 对一次按键分类。
    /// </summary>
    /// <param name="key">按下的键。</param>
    /// <param name="modifiers">修饰键。</param>
    /// <param name="modes">终端模式(应用光标键、键盘应用模式等影响编码)。</param>
    /// <param name="type">终端类型(决定功能键序列方言)。</param>
    /// <param name="canScrollHistory">主屏且有回滚历史(备用屏上的全屏程序应自己收到 CSI 5~/6~)。</param>
    /// <param name="ctrlCCopiesSelection">"选中时 Ctrl+C 复制"此刻是否命中(设置开启且有选区)。</param>
    public static TerminalKeyAction Classify(
        Key key,
        KeyModifiers modifiers,
        TerminalModes modes,
        TerminalType type,
        bool canScrollHistory,
        bool ctrlCCopiesSelection)
    {
        // IME 组字消耗的按键(挑选中文候选等)绝不能编码:会把散逸的 ESC/方向键/Enter
        // 发往 PTY(历史事故:htop 的 F3 搜索里输入中文会杀死 htop,#14a)。
        if (key == Key.ImeProcessed)
        {
            return new(TerminalKeyActionKind.ImePassthrough);
        }

        // 剪贴板快捷键。
        if (modifiers.HasFlag(KeyModifiers.Control) && modifiers.HasFlag(KeyModifiers.Shift))
        {
            switch (key)
            {
                case Key.C:
                    return new(TerminalKeyActionKind.CopySelection);
                case Key.V:
                    return new(TerminalKeyActionKind.PasteClipboard);
            }
        }

        // Shift+Insert 粘贴(经典 X11 / 终端惯例)。必须在编码器之前拦截,
        // 否则编码器会把这次按键当作 CSI 2~ 序列发送出去。
        if (key == Key.Insert && modifiers == KeyModifiers.Shift)
        {
            return new(TerminalKeyActionKind.PasteClipboard);
        }

        // PageUp/PageDown 在主屏上翻动回滚历史;Shift+ 变体则在任何位置翻页。
        if (key is Key.PageUp or Key.PageDown
            && (modifiers == KeyModifiers.Shift || (modifiers == KeyModifiers.None && canScrollHistory)))
        {
            return new(TerminalKeyActionKind.ScrollHistory, key == Key.PageUp ? 1 : -1);
        }

        KeyModifiers effectiveModifiers = modifiers;
        switch (key)
        {
            // Shift+Home/End 将 shell 光标跳到行首/行尾:发送纯 Home/End 序列,
            // readline 会绑定它们 —— 而带 Shift 的 CSI 1;2H/F 变体会被它忽略。
            case Key.Home or Key.End when modifiers == KeyModifiers.Shift:
                effectiveModifiers = KeyModifiers.None;
                break;
            // 有选中文本时 Ctrl+C 复制而非发送中断(设置 → 终端 → 输入)。
            case Key.C when modifiers == KeyModifiers.Control && ctrlCCopiesSelection:
                return new(TerminalKeyActionKind.CopySelection);
        }

        byte[]? encoded = InputEncoder.Encode(key, effectiveModifiers, modes, type);
        return encoded is { Length: > 0 }
            ? new(TerminalKeyActionKind.SendBytes, Bytes: encoded)
            : TerminalKeyAction.None;
    }
}
