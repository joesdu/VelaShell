// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 附录 B「Protocol Encoding」
//   (请求操作码、事件码、错误码、事件掩码、修饰键与按钮状态位、预定义原子)

namespace VelaShell.XServer.Protocol;

/// <summary>核心请求的主操作码(协议附录 B「Requests」)。</summary>
internal static class XOpcode
{
    public const byte CreateWindow = 1;
    public const byte ChangeWindowAttributes = 2;
    public const byte GetWindowAttributes = 3;
    public const byte DestroyWindow = 4;
    public const byte DestroySubwindows = 5;
    public const byte ChangeSaveSet = 6;
    public const byte ReparentWindow = 7;
    public const byte MapWindow = 8;
    public const byte MapSubwindows = 9;
    public const byte UnmapWindow = 10;
    public const byte UnmapSubwindows = 11;
    public const byte ConfigureWindow = 12;
    public const byte CirculateWindow = 13;
    public const byte GetGeometry = 14;
    public const byte QueryTree = 15;
    public const byte InternAtom = 16;
    public const byte GetAtomName = 17;
    public const byte ChangeProperty = 18;
    public const byte DeleteProperty = 19;
    public const byte GetProperty = 20;
    public const byte ListProperties = 21;
    public const byte SetSelectionOwner = 22;
    public const byte GetSelectionOwner = 23;
    public const byte ConvertSelection = 24;
    public const byte SendEvent = 25;
    public const byte GrabPointer = 26;
    public const byte UngrabPointer = 27;
    public const byte GrabButton = 28;
    public const byte UngrabButton = 29;
    public const byte ChangeActivePointerGrab = 30;
    public const byte GrabKeyboard = 31;
    public const byte UngrabKeyboard = 32;
    public const byte GrabKey = 33;
    public const byte UngrabKey = 34;
    public const byte AllowEvents = 35;
    public const byte GrabServer = 36;
    public const byte UngrabServer = 37;
    public const byte QueryPointer = 38;
    public const byte GetMotionEvents = 39;
    public const byte TranslateCoordinates = 40;
    public const byte WarpPointer = 41;
    public const byte SetInputFocus = 42;
    public const byte GetInputFocus = 43;
    public const byte QueryKeymap = 44;
    public const byte OpenFont = 45;
    public const byte CloseFont = 46;
    public const byte QueryFont = 47;
    public const byte QueryTextExtents = 48;
    public const byte ListFonts = 49;
    public const byte ListFontsWithInfo = 50;
    public const byte SetFontPath = 51;
    public const byte GetFontPath = 52;
    public const byte CreatePixmap = 53;
    public const byte FreePixmap = 54;
    public const byte CreateGC = 55;
    public const byte ChangeGC = 56;
    public const byte CopyGC = 57;
    public const byte SetDashes = 58;
    public const byte SetClipRectangles = 59;
    public const byte FreeGC = 60;
    public const byte ClearArea = 61;
    public const byte CopyArea = 62;
    public const byte CopyPlane = 63;
    public const byte PolyPoint = 64;
    public const byte PolyLine = 65;
    public const byte PolySegment = 66;
    public const byte PolyRectangle = 67;
    public const byte PolyArc = 68;
    public const byte FillPoly = 69;
    public const byte PolyFillRectangle = 70;
    public const byte PolyFillArc = 71;
    public const byte PutImage = 72;
    public const byte GetImage = 73;
    public const byte PolyText8 = 74;
    public const byte PolyText16 = 75;
    public const byte ImageText8 = 76;
    public const byte ImageText16 = 77;
    public const byte CreateColormap = 78;
    public const byte FreeColormap = 79;
    public const byte CopyColormapAndFree = 80;
    public const byte InstallColormap = 81;
    public const byte UninstallColormap = 82;
    public const byte ListInstalledColormaps = 83;
    public const byte AllocColor = 84;
    public const byte AllocNamedColor = 85;
    public const byte AllocColorCells = 86;
    public const byte AllocColorPlanes = 87;
    public const byte FreeColors = 88;
    public const byte StoreColors = 89;
    public const byte StoreNamedColor = 90;
    public const byte QueryColors = 91;
    public const byte LookupColor = 92;
    public const byte CreateCursor = 93;
    public const byte CreateGlyphCursor = 94;
    public const byte FreeCursor = 95;
    public const byte RecolorCursor = 96;
    public const byte QueryBestSize = 97;
    public const byte QueryExtension = 98;
    public const byte ListExtensions = 99;
    public const byte ChangeKeyboardMapping = 100;
    public const byte GetKeyboardMapping = 101;
    public const byte ChangeKeyboardControl = 102;
    public const byte GetKeyboardControl = 103;
    public const byte Bell = 104;
    public const byte ChangePointerControl = 105;
    public const byte GetPointerControl = 106;
    public const byte SetScreenSaver = 107;
    public const byte GetScreenSaver = 108;
    public const byte ChangeHosts = 109;
    public const byte ListHosts = 110;
    public const byte SetAccessControl = 111;
    public const byte SetCloseDownMode = 112;
    public const byte KillClient = 113;
    public const byte RotateProperties = 114;
    public const byte ForceScreenSaver = 115;
    public const byte SetPointerMapping = 116;
    public const byte GetPointerMapping = 117;
    public const byte SetModifierMapping = 118;
    public const byte GetModifierMapping = 119;
    public const byte NoOperation = 127;

    /// <summary>扩展的主操作码从这里起分配(128–255)。</summary>
    public const byte FirstExtension = 128;
}

/// <summary>错误码(协议附录 B「Errors」)。</summary>
internal enum XErrorCode : byte
{
    /// <summary>操作码不认识。</summary>
    Request = 1,
    /// <summary>数值越界。</summary>
    Value = 2,
    /// <summary>窗口不存在。</summary>
    Window = 3,
    /// <summary>像素图不存在。</summary>
    Pixmap = 4,
    /// <summary>原子不存在。</summary>
    Atom = 5,
    /// <summary>光标不存在。</summary>
    Cursor = 6,
    /// <summary>字体不存在。</summary>
    Font = 7,
    /// <summary>参数之间不匹配(深度、类别等)。</summary>
    Match = 8,
    /// <summary>可绘对象不存在。</summary>
    Drawable = 9,
    /// <summary>无权操作。</summary>
    Access = 10,
    /// <summary>资源分配失败。</summary>
    Alloc = 11,
    /// <summary>颜色表不存在。</summary>
    Colormap = 12,
    /// <summary>GC 不存在。</summary>
    GContext = 13,
    /// <summary>资源 ID 不在客户端的范围内或已被占用。</summary>
    IDChoice = 14,
    /// <summary>名字(字体、颜色)不存在。</summary>
    Name = 15,
    /// <summary>请求长度不对。</summary>
    Length = 16,
    /// <summary>服务端没实现。</summary>
    Implementation = 17,
}

/// <summary>核心事件码(协议附录 B「Events」)。</summary>
internal static class XEventCode
{
    public const byte KeyPress = 2;
    public const byte KeyRelease = 3;
    public const byte ButtonPress = 4;
    public const byte ButtonRelease = 5;
    public const byte MotionNotify = 6;
    public const byte EnterNotify = 7;
    public const byte LeaveNotify = 8;
    public const byte FocusIn = 9;
    public const byte FocusOut = 10;
    public const byte KeymapNotify = 11;
    public const byte Expose = 12;
    public const byte GraphicsExposure = 13;
    public const byte NoExposure = 14;
    public const byte VisibilityNotify = 15;
    public const byte CreateNotify = 16;
    public const byte DestroyNotify = 17;
    public const byte UnmapNotify = 18;
    public const byte MapNotify = 19;
    public const byte MapRequest = 20;
    public const byte ReparentNotify = 21;
    public const byte ConfigureNotify = 22;
    public const byte ConfigureRequest = 23;
    public const byte GravityNotify = 24;
    public const byte ResizeRequest = 25;
    public const byte CirculateNotify = 26;
    public const byte CirculateRequest = 27;
    public const byte PropertyNotify = 28;
    public const byte SelectionClear = 29;
    public const byte SelectionRequest = 30;
    public const byte SelectionNotify = 31;
    public const byte ColormapNotify = 32;
    public const byte ClientMessage = 33;
    public const byte MappingNotify = 34;

    /// <summary>Generic Event Extension 的事件(XI2、Present 等用它发超过 32 字节的事件)。</summary>
    public const byte GenericEvent = 35;

    /// <summary>SendEvent 发出的事件在事件码上置这一位。</summary>
    public const byte SentFlag = 0x80;
}

/// <summary>事件掩码位(协议附录 B「SETofEVENT」)。</summary>
[Flags]
internal enum XEventMask : uint
{
    None = 0,
    KeyPress = 0x00000001,
    KeyRelease = 0x00000002,
    ButtonPress = 0x00000004,
    ButtonRelease = 0x00000008,
    EnterWindow = 0x00000010,
    LeaveWindow = 0x00000020,
    PointerMotion = 0x00000040,
    PointerMotionHint = 0x00000080,
    Button1Motion = 0x00000100,
    Button2Motion = 0x00000200,
    Button3Motion = 0x00000400,
    Button4Motion = 0x00000800,
    Button5Motion = 0x00001000,
    ButtonMotion = 0x00002000,
    KeymapState = 0x00004000,
    Exposure = 0x00008000,
    VisibilityChange = 0x00010000,
    StructureNotify = 0x00020000,
    ResizeRedirect = 0x00040000,
    SubstructureNotify = 0x00080000,
    SubstructureRedirect = 0x00100000,
    FocusChange = 0x00200000,
    PropertyChange = 0x00400000,
    ColormapChange = 0x00800000,
    OwnerGrabButton = 0x01000000,

    /// <summary>协议规定的合法位全集;超出的位是 BadValue。</summary>
    AllValid = 0x01FFFFFF,

    /// <summary>设备事件(可沿窗口树向上传播、受 do-not-propagate 约束的那些)。</summary>
    DeviceEvents = KeyPress | KeyRelease | ButtonPress | ButtonRelease | PointerMotion
                   | Button1Motion | Button2Motion | Button3Motion | Button4Motion | Button5Motion | ButtonMotion,
}

/// <summary>按键与按钮状态位(SETofKEYBUTMASK)。</summary>
[Flags]
internal enum XModMask : ushort
{
    None = 0,
    Shift = 0x0001,
    Lock = 0x0002,
    Control = 0x0004,
    Mod1 = 0x0008,
    Mod2 = 0x0010,
    Mod3 = 0x0020,
    Mod4 = 0x0040,
    Mod5 = 0x0080,
    Button1 = 0x0100,
    Button2 = 0x0200,
    Button3 = 0x0400,
    Button4 = 0x0800,
    Button5 = 0x1000,
    AnyModifier = 0x8000,
}

/// <summary>预定义原子(协议附录 B「Predefined Atoms」),编号 1–68。</summary>
internal static class XAtom
{
    public const uint None = 0;
    public const uint Primary = 1;
    public const uint Secondary = 2;
    public const uint Atom = 4;
    public const uint Cardinal = 6;
    public const uint Font = 18;
    public const uint Integer = 19;
    public const uint String = 31;
    public const uint Window = 33;
    public const uint WmHints = 35;
    public const uint WmIconName = 37;
    public const uint WmName = 39;
    public const uint WmNormalHints = 40;
    public const uint WmClass = 67;
    public const uint WmTransientFor = 68;

    /// <summary>预定义原子的名字,下标即原子值(0 = None 占位)。</summary>
    public static readonly string[] PredefinedNames =
    [
        "",
        "PRIMARY", "SECONDARY", "ARC", "ATOM", "BITMAP", "CARDINAL", "COLORMAP", "CURSOR",
        "CUT_BUFFER0", "CUT_BUFFER1", "CUT_BUFFER2", "CUT_BUFFER3", "CUT_BUFFER4", "CUT_BUFFER5",
        "CUT_BUFFER6", "CUT_BUFFER7", "DRAWABLE", "FONT", "INTEGER", "PIXMAP", "POINT", "RECTANGLE",
        "RESOURCE_MANAGER", "RGB_COLOR_MAP", "RGB_BEST_MAP", "RGB_BLUE_MAP", "RGB_DEFAULT_MAP",
        "RGB_GRAY_MAP", "RGB_GREEN_MAP", "RGB_RED_MAP", "STRING", "VISUALID", "WINDOW", "WM_COMMAND",
        "WM_HINTS", "WM_CLIENT_MACHINE", "WM_ICON_NAME", "WM_ICON_SIZE", "WM_NAME", "WM_NORMAL_HINTS",
        "WM_SIZE_HINTS", "WM_ZOOM_HINTS", "MIN_SPACE", "NORM_SPACE", "MAX_SPACE", "END_SPACE",
        "SUPERSCRIPT_X", "SUPERSCRIPT_Y", "SUBSCRIPT_X", "SUBSCRIPT_Y", "UNDERLINE_POSITION",
        "UNDERLINE_THICKNESS", "STRIKEOUT_ASCENT", "STRIKEOUT_DESCENT", "ITALIC_ANGLE", "X_HEIGHT",
        "QUAD_WIDTH", "WEIGHT", "POINT_SIZE", "RESOLUTION", "COPYRIGHT", "NOTICE", "FONT_NAME",
        "FAMILY_NAME", "FULL_NAME", "CAP_HEIGHT", "WM_CLASS", "WM_TRANSIENT_FOR",
    ];
}

/// <summary>窗口属性值掩码(CreateWindow / ChangeWindowAttributes 的 value-mask)。</summary>
[Flags]
internal enum XWindowAttrMask : uint
{
    BackgroundPixmap = 0x0001,
    BackgroundPixel = 0x0002,
    BorderPixmap = 0x0004,
    BorderPixel = 0x0008,
    BitGravity = 0x0010,
    WinGravity = 0x0020,
    BackingStore = 0x0040,
    BackingPlanes = 0x0080,
    BackingPixel = 0x0100,
    OverrideRedirect = 0x0200,
    SaveUnder = 0x0400,
    EventMask = 0x0800,
    DoNotPropagateMask = 0x1000,
    Colormap = 0x2000,
    Cursor = 0x4000,
}

/// <summary>ConfigureWindow 的 value-mask。</summary>
[Flags]
internal enum XConfigMask : ushort
{
    X = 0x01,
    Y = 0x02,
    Width = 0x04,
    Height = 0x08,
    BorderWidth = 0x10,
    Sibling = 0x20,
    StackMode = 0x40,
}

/// <summary>GC 的 value-mask。</summary>
[Flags]
internal enum XGcMask : uint
{
    Function = 0x000001,
    PlaneMask = 0x000002,
    Foreground = 0x000004,
    Background = 0x000008,
    LineWidth = 0x000010,
    LineStyle = 0x000020,
    CapStyle = 0x000040,
    JoinStyle = 0x000080,
    FillStyle = 0x000100,
    FillRule = 0x000200,
    Tile = 0x000400,
    Stipple = 0x000800,
    TileStipXOrigin = 0x001000,
    TileStipYOrigin = 0x002000,
    Font = 0x004000,
    SubwindowMode = 0x008000,
    GraphicsExposures = 0x010000,
    ClipXOrigin = 0x020000,
    ClipYOrigin = 0x040000,
    ClipMask = 0x080000,
    DashOffset = 0x100000,
    Dashes = 0x200000,
    ArcMode = 0x400000,
}
