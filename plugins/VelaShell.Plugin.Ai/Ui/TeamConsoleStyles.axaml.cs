using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 团队控制台的样式表(版式,以及它要用到的图标、LiveMarkdown 样式与 Markdown 皮肤)。
/// </summary>
/// <remarks>
/// 带一个类名,页面就能直接 <c>Styles.Add(new TeamConsoleStyles())</c>,不必在运行期按 <c>avares://</c> 地址去找程序集 ——
/// 宿主里 LiveMarkdown.Avalonia 有两份(宿主一份、插件装载上下文里一份),按名字找可能找错。
/// </remarks>
public partial class TeamConsoleStyles : Styles
{
    /// <summary>装载编译好的 XAML。</summary>
    public TeamConsoleStyles() => AvaloniaXamlLoader.Load(this);
}
