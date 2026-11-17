using Avalonia.Media;
using AvaloniaEdit.Document;
using TextMateSharp.Grammars;
using VelaShell.Plugin.Ai.Ui;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// TextMate 语法高亮:按扩展名挑语言、状态跨行、上限与亮暗配色,以及 AvaloniaEdit 着色器的缓存作废。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class TextMateHighlighterTests
{
    private static Color ColorOf(HighlightSpan span) => ((ISolidColorBrush)span.Brush).Color;

    [TestMethod]
    public void CSharp_IsHighlighted_AndUnknownExtensionsStayPlain()
    {
        IReadOnlyList<HighlightSpan>[] lines = TextMateHighlighter.HighlightLines(["public class A { int x = 1; }"], "src/A.cs", ThemeName.DarkPlus)!;
        Assert.IsNotNull(lines);
        Assert.IsNotEmpty(lines[0], "关键字、类型名、数字都该有颜色");
        Assert.AreEqual(0, lines[0][0].Start, "public 在行首");

        Assert.IsNull(TextMateHighlighter.HighlightLines(["whatever"], "notes.unknownext", ThemeName.DarkPlus));
        Assert.IsNull(TextMateHighlighter.HighlightLines(["x"], "Makefile-without-extension", ThemeName.DarkPlus));
    }

    /// <summary>状态从上一行带下来:多行注释的第二行也是注释色 —— 只对折叠后的片段分词就会断在这里。</summary>
    [TestMethod]
    public void MultiLineComment_CarriesStateAcrossLines()
    {
        IReadOnlyList<HighlightSpan>[] lines = TextMateHighlighter.HighlightLines(
            ["/* first line", "still inside */ int x;"], "a.cs", ThemeName.DarkPlus)!;
        Color comment = ColorOf(lines[0][0]);
        HighlightSpan second = lines[1][0];
        Assert.AreEqual(0, second.Start);
        Assert.AreEqual(comment, ColorOf(second), "第二行开头仍在注释里");

        IReadOnlyList<HighlightSpan>[] alone = TextMateHighlighter.HighlightLines(["still inside */ int x;"], "a.cs", ThemeName.DarkPlus)!;
        Assert.IsFalse(alone[0].Count > 0 && alone[0][0].Start == 0 && ColorOf(alone[0][0]) == comment && alone[0][0].Length >= "still inside */".Length,
            "单独分这一行就没有注释状态");
    }

    [TestMethod]
    public void DarkAndLightThemes_UseDifferentColors()
    {
        Color dark = ColorOf(TextMateHighlighter.HighlightLines(["public class A {}"], "a.cs", ThemeName.DarkPlus)![0][0]);
        Color light = ColorOf(TextMateHighlighter.HighlightLines(["public class A {}"], "a.cs", ThemeName.LightPlus)![0][0]);
        Assert.AreNotEqual(dark, light);
        Assert.AreEqual(ThemeName.DarkPlus, TextMateHighlighter.ThemeFor(Avalonia.Styling.ThemeVariant.Dark));
        Assert.AreEqual(ThemeName.LightPlus, TextMateHighlighter.ThemeFor(Avalonia.Styling.ThemeVariant.Light));
    }

    [TestMethod]
    public void Limits_SkipHugeInputs()
    {
        string longLine = "var x = \"" + new string('a', TextMateHighlighter.MaxLineLength + 10) + "\";";
        IReadOnlyList<HighlightSpan>[] lines = TextMateHighlighter.HighlightLines([longLine, "int y = 2;"], "a.cs", ThemeName.DarkPlus)!;
        Assert.IsEmpty(lines[0], "单行太长不着色");
        Assert.IsNotEmpty(lines[1], "后面的行照常");

        string[] many = [.. Enumerable.Repeat("int x;", TextMateHighlighter.MaxLines + 1)];
        Assert.IsNull(TextMateHighlighter.HighlightLines(many, "a.cs", ThemeName.DarkPlus), "行数超限整份不高亮");
    }

    /// <summary>AvaloniaEdit 着色器:按行缓存,文档一改就从改动那一行起作废,之后的状态重新从上一行带下来。</summary>
    [TestMethod]
    public void Colorizer_InvalidatesFromTheChangedLine()
    {
        var document = new TextDocument("/* open\nstill comment */ int x;\nint y;");
        TextMateLineTokenizer tokenizer = TextMateHighlighter.ForPath("a.cs", ThemeName.DarkPlus)!;
        var colorizer = new TextMateColorizer(tokenizer, document);
        Color comment = ColorOf(colorizer.SpansOf(1)[0]);
        Assert.AreEqual(comment, ColorOf(colorizer.SpansOf(2)[0]));
        Assert.AreEqual(2, colorizer.CachedLines);

        // 把开头的 /* 改成 //:第二行不再在注释里
        document.Replace(0, 2, "//");
        Assert.AreEqual(0, colorizer.CachedLines, "改了第一行,缓存从第一行起作废");
        IReadOnlyList<HighlightSpan> second = colorizer.SpansOf(2);
        Assert.IsFalse(second.Count > 0 && second[0].Start == 0 && ColorOf(second[0]) == comment && second[0].Length >= "still comment */".Length);

        colorizer.SpansOf(3);
        document.Insert(document.GetLineByNumber(3).Offset, "  ");
        Assert.AreEqual(2, colorizer.CachedLines, "只作废改动那一行及之后");
        colorizer.Detach();
    }
}
