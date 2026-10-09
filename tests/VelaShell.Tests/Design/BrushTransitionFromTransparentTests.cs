using System.Xml;
using System.Xml.Linq;

namespace VelaShell.Tests.Design;

/// <summary>
/// 守门:画刷过渡不许与 <c>Transparent</c> 配对(#577,DESIGN.md §6.1「Brush transitions」)。
/// </summary>
/// <remarks>
/// <para>
/// <c>Transparent</c> 是 <c>#00FFFFFF</c> —— 全透明的<b>白</b>。Avalonia 的 <c>ColorAnimator</c> 按 A、R、G、B
/// 逐通道插值,不做预乘,从它过渡到不透明的深色令牌,途中是半透明的白。新建连接对话框的协议栏与分区页签
/// (常态 <c>Transparent</c>,悬停 <c>VelaBgHover</c>)因此每次进出都闪一下亮灰:实测峰值约 <c>#7E7E86</c>,
/// 两头却是 <c>#343746</c> 与 <c>#363948</c>,停稳后几乎看不出 —— 用户看到的就是「闪烁」。
/// 这件事先前被当成高 DPI 下命中区域重采样,其实 100% 缩放下一样复现(plan.md §175)。
/// </para>
/// <para>
/// 扫的是同一作用域里「某属性挂了 <c>BrushTransition</c>」且「同一属性被设成 <c>Transparent</c>」的组合:
/// Style / ControlTheme 里 <c>Transitions</c> 的 Setter 与同级的 <c>Setter Property="X" Value="Transparent"</c>;
/// 元素自己的 <c>&lt;X.Transitions&gt;</c> 与它的 <c>X="Transparent"</c> 属性。
/// 过渡挂在一处、<c>Transparent</c> 设在另一个样式里的组合扫不到 —— 这是守门,不是证明。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Design")]
public sealed class BrushTransitionFromTransparentTests
{
    [TestMethod]
    public void NoBrushTransitionPairsWithATransparentValue()
    {
        string srcRoot = Path.Combine(RepoRoot(), "src");
        List<string> offenders = [];
        int scanned = 0;
        foreach (string file in Directory.EnumerateFiles(srcRoot, "*.axaml", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(srcRoot, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }
            scanned++;
            offenders.AddRange(Scan(File.ReadAllText(file)).Select(finding => $"{relative}:{finding}"));
        }

        Assert.IsGreaterThan(50, scanned, $"只扫到 {scanned} 个 axaml —— 多半是仓库根目录没找对,守门就失效了。");
        Assert.IsEmpty(offenders,
            "以下画刷过渡与 Transparent(#00FFFFFF,全透明的白)配对,插值途中会闪一下亮色(#577)。"
            + "要么直接落值,要么把常态换成目标色的全透明版(同一 RGB、alpha 为 0):" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>扫描器本身的正反样例:它要是什么都扫不出来,上面那条就是另一条「怎么改都绿」。</summary>
    [TestMethod]
    // #577 修复前的协议栏项:常态 Transparent,本体上挂 Background / BorderBrush 的过渡。
    [DataRow("<ControlTheme><Setter Property='Background' Value='Transparent'/><Setter Property='Transitions'><Transitions>"
        + "<BrushTransition Property='Background' Duration='0:0:0.12'/></Transitions></Setter></ControlTheme>", true)]
    [DataRow("<ControlTheme><Setter Property='BorderBrush' Value='Transparent'/><Setter Property='Transitions'><Transitions>"
        + "<BrushTransition Property='BorderBrush'/></Transitions></Setter></ControlTheme>", true)]
    // 写成 #00FFFFFF 也是同一个白。
    [DataRow("<Style><Setter Property='Background' Value='#00FFFFFF'/><Setter Property='Transitions'><Transitions>"
        + "<BrushTransition Property='Background'/></Transitions></Setter></Style>", true)]
    // 停靠标签的写法:两头都是不透明令牌,插值不会过冲。
    [DataRow("<Style><Setter Property='Background' Value='{DynamicResource VelaTabInactiveBg}'/><Setter Property='Transitions'><Transitions>"
        + "<BrushTransition Property='Background'/></Transitions></Setter></Style>", false)]
    // 属性名不同不算:Background 是 Transparent,过渡的却是 Foreground。
    [DataRow("<Style><Setter Property='Background' Value='Transparent'/><Setter Property='Transitions'><Transitions>"
        + "<BrushTransition Property='Foreground'/></Transitions></Setter></Style>", false)]
    // 不是画刷过渡不算。
    [DataRow("<Style><Setter Property='Background' Value='Transparent'/><Setter Property='Transitions'><Transitions>"
        + "<DoubleTransition Property='Opacity'/></Transitions></Setter></Style>", false)]
    // 元素自己的 Transitions 与它自己的 Transparent。
    [DataRow("<Border Background='Transparent'><Border.Transitions><Transitions><BrushTransition Property='Background'/>"
        + "</Transitions></Border.Transitions></Border>", true)]
    [DataRow("<Border Background='{DynamicResource VelaAccent}'><Border.Transitions><Transitions><BrushTransition Property='Background'/>"
        + "</Transitions></Border.Transitions></Border>", false)]
    // 带类型前缀的属性名按最后一段比。
    [DataRow("<Style><Setter Property='Border.Background' Value='Transparent'/><Setter Property='Transitions'><Transitions>"
        + "<BrushTransition Property='Background'/></Transitions></Setter></Style>", true)]
    public void Scanner_FlagsExactlyTheBadShapes(string xaml, bool flagged) =>
        Assert.AreEqual(flagged, Scan(xaml).Count > 0, xaml);

    /// <summary>扫一份 axaml,返回「行号  说明」形式的问题清单。</summary>
    private static List<string> Scan(string xaml)
    {
        XDocument document = XDocument.Parse(xaml, LoadOptions.SetLineInfo);
        List<string> findings = [];
        foreach (XElement transition in document.Descendants().Where(element => element.Name.LocalName == "BrushTransition"))
        {
            if (LastSegment(transition.Attribute("Property")?.Value) is not { } property
                || transition.Ancestors().FirstOrDefault(IsTransitionsHolder) is not { Parent: { } owner } holder)
            {
                continue;
            }
            bool paired = holder.Name.LocalName == "Setter"
                // Style / ControlTheme:同级 Setter 把同一属性设成 Transparent。
                ? owner.Elements().Any(setter => setter.Name.LocalName == "Setter"
                    && LastSegment(setter.Attribute("Property")?.Value) == property
                    && IsTransparent(setter.Attribute("Value")?.Value))
                // 元素自己的 <X.Transitions>:同一元素的属性是 Transparent。
                : owner.Attributes().Any(attribute => attribute.Name.LocalName == property && IsTransparent(attribute.Value));
            if (paired)
            {
                findings.Add($"{((IXmlLineInfo)transition).LineNumber}  BrushTransition({property}) 与 Transparent 配对");
            }
        }
        return findings;
    }

    private static bool IsTransitionsHolder(XElement element) =>
        element.Name.LocalName.EndsWith(".Transitions", StringComparison.Ordinal)
        || (element.Name.LocalName == "Setter" && element.Attribute("Property")?.Value == "Transitions");

    private static string? LastSegment(string? property) =>
        property is null ? null : property[(property.LastIndexOf('.') + 1)..];

    private static bool IsTransparent(string? value) =>
        string.Equals(value?.Trim(), "Transparent", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value?.Trim(), "#00FFFFFF", StringComparison.OrdinalIgnoreCase);

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位到仓库根目录(找不到 VelaShell.slnx)。");
    }
}
