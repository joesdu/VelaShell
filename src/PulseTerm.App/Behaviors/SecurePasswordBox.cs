using System;
using System.Runtime.CompilerServices;
using System.Security;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using PulseTerm.App.Security;

namespace PulseTerm.App.Behaviors;

/// <summary>
/// 让一个 <see cref="TextBox"/> 以 <see cref="SecureString"/> 承载密码,而不是把明文
/// 常驻在托管 <see cref="string"/> 中。SecureString 为唯一真实来源,TextBox.Text 仅保存
/// 等长的掩码字符('•');开启 <see cref="RevealProperty"/> 时才临时还原明文用于显示。
///
/// 用法:
///   <TextBox behaviors:SecurePasswordBox.Enabled="True"
///            behaviors:SecurePasswordBox.Password="{Binding Password}"
///            behaviors:SecurePasswordBox.Reveal="{Binding ShowPassword}" />
///
/// 掩码与安全缓冲保持 1:1 的位置映射,因此插入符/选区、方向键、Home/End 等原生行为可直接复用。
/// 仅接受可打印 ASCII;复制/剪切被禁用以避免密码外泄到剪贴板。
/// </summary>
public static class SecurePasswordBox
{
    private const char Mask = '•';

    private sealed class BoxState
    {
        public bool Suppress;
    }

    private static readonly ConditionalWeakTable<TextBox, BoxState> States = new();

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Enabled", typeof(SecurePasswordBox));

    /// <summary>双向绑定到 ViewModel 的密码;每次编辑都会赋一个新的 SecureString 引用以触发通知。</summary>
    public static readonly AttachedProperty<SecureString?> PasswordProperty =
        AvaloniaProperty.RegisterAttached<TextBox, SecureString?>(
            "Password", typeof(SecurePasswordBox), defaultBindingMode: BindingMode.TwoWay);

    public static readonly AttachedProperty<bool> RevealProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Reveal", typeof(SecurePasswordBox));

    public static bool GetEnabled(TextBox e) => e.GetValue(EnabledProperty);
    public static void SetEnabled(TextBox e, bool v) => e.SetValue(EnabledProperty, v);
    public static SecureString? GetPassword(TextBox e) => e.GetValue(PasswordProperty);
    public static void SetPassword(TextBox e, SecureString? v) => e.SetValue(PasswordProperty, v);
    public static bool GetReveal(TextBox e) => e.GetValue(RevealProperty);
    public static void SetReveal(TextBox e, bool v) => e.SetValue(RevealProperty, v);

    static SecurePasswordBox()
    {
        EnabledProperty.Changed.AddClassHandler<TextBox>(OnEnabledChanged);
        PasswordProperty.Changed.AddClassHandler<TextBox>(OnPasswordChanged);
        RevealProperty.Changed.AddClassHandler<TextBox>(OnRevealChanged);
    }

    private static BoxState State(TextBox tb) => States.GetValue(tb, _ => new BoxState());

    private static void OnEnabledChanged(TextBox tb, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            tb.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
            tb.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            Render(tb, GetPassword(tb), GetPassword(tb)?.Length ?? 0);
        }
        else
        {
            tb.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
            tb.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        }
    }

    private static void OnPasswordChanged(TextBox tb, AvaloniaPropertyChangedEventArgs e)
    {
        // 由本类内部编辑触发的赋值已自行渲染;仅对外部(ViewModel 加载/清空)赋值做重绘。
        if (State(tb).Suppress)
        {
            return;
        }

        var secure = e.NewValue as SecureString;
        Render(tb, secure, secure?.Length ?? 0);
    }

    private static void OnRevealChanged(TextBox tb, AvaloniaPropertyChangedEventArgs e)
    {
        if (!GetEnabled(tb))
        {
            return;
        }

        Render(tb, GetPassword(tb), tb.CaretIndex);
    }

    private static void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is not TextBox tb)
        {
            return;
        }

        // 我们完全接管输入:阻止 TextBox 自行把明文写入 Text。
        e.Handled = true;

        var text = FilterAscii(e.Text);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var (start, len) = Selection(tb);
        ApplyEdit(tb, start, len, text, start + text.Length);
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb)
        {
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.V:
                    e.Handled = true;
                    _ = PasteAsync(tb);
                    return;
                case Key.C:
                case Key.X:
                    // 禁止复制/剪切,避免密码(或掩码)进入剪贴板。
                    e.Handled = true;
                    return;
                default:
                    return; // Ctrl+A 等交由原生处理。
            }
        }

        switch (e.Key)
        {
            case Key.Back:
            {
                e.Handled = true;
                var (start, len) = Selection(tb);
                if (len > 0)
                {
                    ApplyEdit(tb, start, len, null, start);
                }
                else if (start > 0)
                {
                    ApplyEdit(tb, start - 1, 1, null, start - 1);
                }

                return;
            }
            case Key.Delete:
            {
                e.Handled = true;
                var (start, len) = Selection(tb);
                var total = GetPassword(tb)?.Length ?? 0;
                if (len > 0)
                {
                    ApplyEdit(tb, start, len, null, start);
                }
                else if (start < total)
                {
                    ApplyEdit(tb, start, 1, null, start);
                }

                return;
            }
        }
    }

    private static async System.Threading.Tasks.Task PasteAsync(TextBox tb)
    {
        var clipboard = TopLevel.GetTopLevel(tb)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        var text = FilterAscii(await clipboard.TryGetTextAsync());
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var (start, len) = Selection(tb);
        ApplyEdit(tb, start, len, text, start + text.Length);
    }

    /// <summary>在安全缓冲上复制并施加一次“删除选区 + 插入”编辑,然后赋新引用并重绘。</summary>
    private static void ApplyEdit(TextBox tb, int start, int removeLen, string? insert, int newCaret)
    {
        var current = GetPassword(tb);
        var next = current is null ? new SecureString() : current.Copy();

        for (var i = start + removeLen - 1; i >= start; i--)
        {
            if (i >= 0 && i < next.Length)
            {
                next.RemoveAt(i);
            }
        }

        if (!string.IsNullOrEmpty(insert))
        {
            var idx = Math.Clamp(start, 0, next.Length);
            foreach (var c in insert)
            {
                next.InsertAt(idx++, c);
            }
        }

        var state = State(tb);
        state.Suppress = true;
        SetPassword(tb, next);
        state.Suppress = false;

        current?.Dispose();
        Render(tb, next, newCaret);
    }

    private static void Render(TextBox tb, SecureString? secure, int caret)
    {
        var len = secure?.Length ?? 0;
        var state = State(tb);
        state.Suppress = true;
        tb.Text = GetReveal(tb) && secure is not null
            ? SecureStringConvert.ToPlaintext(secure)
            : new string(Mask, len);
        caret = Math.Clamp(caret, 0, len);
        tb.CaretIndex = caret;
        tb.SelectionStart = caret;
        tb.SelectionEnd = caret;
        state.Suppress = false;
    }

    /// <summary>返回选区的起点与长度;无选区时起点为插入符位置、长度为 0。</summary>
    private static (int Start, int Length) Selection(TextBox tb)
    {
        var a = tb.SelectionStart;
        var b = tb.SelectionEnd;
        var min = Math.Min(a, b);
        var max = Math.Max(a, b);
        var len = max - min;
        return len > 0 ? (min, len) : (tb.CaretIndex, 0);
    }

    internal static string FilterAscii(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        Span<char> buffer = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
        var n = 0;
        foreach (var c in text)
        {
            if (c is >= ' ' and <= '~')
            {
                buffer[n++] = c;
            }
        }

        return n == text.Length ? text : new string(buffer[..n]);
    }
}
