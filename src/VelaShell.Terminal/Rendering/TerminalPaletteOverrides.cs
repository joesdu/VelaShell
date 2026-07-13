using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Rendering;

/// <summary>
/// 用户自定义终端配色的稀疏覆盖集(设置 → 外观):null 槽位表示"未自定义,跟随主题"。
/// 由宿主(App 层)将设置中与出厂默认值不同的颜色填进来,叠加在主题调色板之上,
/// 这样没改过的颜色仍能在暗/亮主题(Dracula/Solarized Light)之间正常切换。
/// </summary>
public sealed class TerminalPaletteOverrides
{
    public const int AnsiCount = 16;

    public Rgba? Foreground { get; set; }

    public Rgba? Background { get; set; }

    public Rgba? Cursor { get; set; }

    /// <summary>选区底色(不含透明度,渲染层负责按方案叠加 alpha)。</summary>
    public Rgba? Selection { get; set; }

    /// <summary>ANSI 0–15(前 8 普通 + 后 8 明亮)。</summary>
    public Rgba?[] Ansi { get; } = new Rgba?[AnsiCount];

    public bool IsEmpty
    {
        get
        {
            if (Foreground is not null || Background is not null || Cursor is not null || Selection is not null)
            {
                return false;
            }
            return Ansi.Length == 0;
        }
    }
}
