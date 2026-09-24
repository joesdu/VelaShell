using Avalonia.Input;
using VelaShell.XServer.Host;

namespace VelaShell.Services.XServer;

/// <summary>Avalonia 的输入 → X 的键码、按钮号与光标字形。</summary>
/// <remarks>
/// 按<b>物理键</b>(<see cref="PhysicalKey" />,与键盘布局无关的位置)翻:X 客户端拿到键码后按服务端的键位表自己
/// 解释成字符,与 X.Org 在 Linux 上的做法一致。换算成哪个字符由服务端的键位表决定,不由宿主决定。
/// </remarks>
internal static class XInputMap
{
    /// <summary>物理键 → X 键码(evdev + 8,见 <see cref="XKeycodes" />);X 这边没有对应键时为 0。</summary>
    public static byte Keycode(PhysicalKey key) => key switch
    {
        PhysicalKey.Escape => XKeycodes.Escape,
        PhysicalKey.Digit1 => XKeycodes.D1,
        PhysicalKey.Digit2 => XKeycodes.D2,
        PhysicalKey.Digit3 => XKeycodes.D3,
        PhysicalKey.Digit4 => XKeycodes.D4,
        PhysicalKey.Digit5 => XKeycodes.D5,
        PhysicalKey.Digit6 => XKeycodes.D6,
        PhysicalKey.Digit7 => XKeycodes.D7,
        PhysicalKey.Digit8 => XKeycodes.D8,
        PhysicalKey.Digit9 => XKeycodes.D9,
        PhysicalKey.Digit0 => XKeycodes.D0,
        PhysicalKey.Minus => XKeycodes.Minus,
        PhysicalKey.Equal => XKeycodes.Equal,
        PhysicalKey.Backspace => XKeycodes.BackSpace,
        PhysicalKey.Tab => XKeycodes.Tab,
        PhysicalKey.Q => XKeycodes.Q,
        PhysicalKey.W => XKeycodes.W,
        PhysicalKey.E => XKeycodes.E,
        PhysicalKey.R => XKeycodes.R,
        PhysicalKey.T => XKeycodes.T,
        PhysicalKey.Y => XKeycodes.Y,
        PhysicalKey.U => XKeycodes.U,
        PhysicalKey.I => XKeycodes.I,
        PhysicalKey.O => XKeycodes.O,
        PhysicalKey.P => XKeycodes.P,
        PhysicalKey.BracketLeft => XKeycodes.BracketLeft,
        PhysicalKey.BracketRight => XKeycodes.BracketRight,
        PhysicalKey.Enter => XKeycodes.Return,
        PhysicalKey.ControlLeft => XKeycodes.ControlLeft,
        PhysicalKey.A => XKeycodes.A,
        PhysicalKey.S => XKeycodes.S,
        PhysicalKey.D => XKeycodes.D,
        PhysicalKey.F => XKeycodes.F,
        PhysicalKey.G => XKeycodes.G,
        PhysicalKey.H => XKeycodes.H,
        PhysicalKey.J => XKeycodes.J,
        PhysicalKey.K => XKeycodes.K,
        PhysicalKey.L => XKeycodes.L,
        PhysicalKey.Semicolon => XKeycodes.Semicolon,
        PhysicalKey.Quote => XKeycodes.Apostrophe,
        PhysicalKey.Backquote => XKeycodes.Grave,
        PhysicalKey.ShiftLeft => XKeycodes.ShiftLeft,
        PhysicalKey.Backslash => XKeycodes.Backslash,
        PhysicalKey.Z => XKeycodes.Z,
        PhysicalKey.X => XKeycodes.X,
        PhysicalKey.C => XKeycodes.C,
        PhysicalKey.V => XKeycodes.V,
        PhysicalKey.B => XKeycodes.B,
        PhysicalKey.N => XKeycodes.N,
        PhysicalKey.M => XKeycodes.M,
        PhysicalKey.Comma => XKeycodes.Comma,
        PhysicalKey.Period => XKeycodes.Period,
        PhysicalKey.Slash => XKeycodes.Slash,
        PhysicalKey.ShiftRight => XKeycodes.ShiftRight,
        PhysicalKey.NumPadMultiply => XKeycodes.KeypadMultiply,
        PhysicalKey.AltLeft => XKeycodes.AltLeft,
        PhysicalKey.Space => XKeycodes.Space,
        PhysicalKey.CapsLock => XKeycodes.CapsLock,
        PhysicalKey.F1 => XKeycodes.F1,
        PhysicalKey.F2 => XKeycodes.F2,
        PhysicalKey.F3 => XKeycodes.F3,
        PhysicalKey.F4 => XKeycodes.F4,
        PhysicalKey.F5 => XKeycodes.F5,
        PhysicalKey.F6 => XKeycodes.F6,
        PhysicalKey.F7 => XKeycodes.F7,
        PhysicalKey.F8 => XKeycodes.F8,
        PhysicalKey.F9 => XKeycodes.F9,
        PhysicalKey.F10 => XKeycodes.F10,
        PhysicalKey.NumLock => XKeycodes.NumLock,
        PhysicalKey.ScrollLock => XKeycodes.ScrollLock,
        PhysicalKey.NumPad7 => XKeycodes.Keypad7,
        PhysicalKey.NumPad8 => XKeycodes.Keypad8,
        PhysicalKey.NumPad9 => XKeycodes.Keypad9,
        PhysicalKey.NumPadSubtract => XKeycodes.KeypadSubtract,
        PhysicalKey.NumPad4 => XKeycodes.Keypad4,
        PhysicalKey.NumPad5 => XKeycodes.Keypad5,
        PhysicalKey.NumPad6 => XKeycodes.Keypad6,
        PhysicalKey.NumPadAdd => XKeycodes.KeypadAdd,
        PhysicalKey.NumPad1 => XKeycodes.Keypad1,
        PhysicalKey.NumPad2 => XKeycodes.Keypad2,
        PhysicalKey.NumPad3 => XKeycodes.Keypad3,
        PhysicalKey.NumPad0 => XKeycodes.Keypad0,
        PhysicalKey.NumPadDecimal => XKeycodes.KeypadDecimal,
        PhysicalKey.IntlBackslash => XKeycodes.IntlBackslash,
        PhysicalKey.F11 => XKeycodes.F11,
        PhysicalKey.F12 => XKeycodes.F12,
        PhysicalKey.NumPadEnter => XKeycodes.KeypadEnter,
        PhysicalKey.ControlRight => XKeycodes.ControlRight,
        PhysicalKey.NumPadDivide => XKeycodes.KeypadDivide,
        PhysicalKey.PrintScreen => XKeycodes.PrintScreen,
        PhysicalKey.AltRight => XKeycodes.AltRight,
        PhysicalKey.Home => XKeycodes.Home,
        PhysicalKey.ArrowUp => XKeycodes.Up,
        PhysicalKey.PageUp => XKeycodes.PageUp,
        PhysicalKey.ArrowLeft => XKeycodes.Left,
        PhysicalKey.ArrowRight => XKeycodes.Right,
        PhysicalKey.End => XKeycodes.End,
        PhysicalKey.ArrowDown => XKeycodes.Down,
        PhysicalKey.PageDown => XKeycodes.PageDown,
        PhysicalKey.Insert => XKeycodes.Insert,
        PhysicalKey.Delete => XKeycodes.Delete,
        PhysicalKey.Pause => XKeycodes.Pause,
        PhysicalKey.MetaLeft => XKeycodes.SuperLeft,
        PhysicalKey.MetaRight => XKeycodes.SuperRight,
        PhysicalKey.ContextMenu => XKeycodes.Menu,
        _ => 0,
    };

    /// <summary>鼠标按钮 → X 按钮号(1 左、2 中、3 右、8 后退、9 前进);其它为 0。</summary>
    public static int Button(MouseButton button) => button switch
    {
        MouseButton.Left => 1,
        MouseButton.Middle => 2,
        MouseButton.Right => 3,
        MouseButton.XButton1 => 8,
        MouseButton.XButton2 => 9,
        _ => 0,
    };

    /// <summary>
    /// cursor 字体字形号(协议附录 B「cursor font」里的编号)→ 系统光标。−1 默认箭头,−2 隐藏;认不出来的给箭头。
    /// </summary>
    public static StandardCursorType Cursor(int glyph) => glyph switch
    {
        -2 => StandardCursorType.None,
        0 or 24 or 88 => StandardCursorType.No,                  // X_cursor、circle、pirate
        12 => StandardCursorType.BottomLeftCorner,
        14 => StandardCursorType.BottomRightCorner,
        16 => StandardCursorType.BottomSide,
        30 or 34 or 90 or 130 => StandardCursorType.Cross,        // cross、crosshair、plus、tcross
        52 or 120 => StandardCursorType.SizeAll,                  // fleur、sizing
        58 or 60 => StandardCursorType.Hand,                      // hand1、hand2
        70 => StandardCursorType.LeftSide,
        92 => StandardCursorType.Help,                            // question_arrow
        96 => StandardCursorType.RightSide,
        108 => StandardCursorType.SizeWestEast,                   // sb_h_double_arrow
        116 => StandardCursorType.SizeNorthSouth,                 // sb_v_double_arrow
        134 => StandardCursorType.TopLeftCorner,
        136 => StandardCursorType.TopRightCorner,
        138 => StandardCursorType.TopSide,
        150 => StandardCursorType.Wait,                           // watch
        152 => StandardCursorType.Ibeam,                          // xterm
        _ => StandardCursorType.Arrow,
    };
}
