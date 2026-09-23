using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.Core.Resources;
using VelaShell.Core.XServer;

namespace VelaShell.Views;

/// <summary>帮助表里的一行(已本地化)。</summary>
/// <param name="Syntax">参数写法(原样)。</param>
/// <param name="Description">说明(当前界面语言)。</param>
/// <param name="Example">示例(原样);没有为空串。</param>
/// <param name="Separator">行底分隔线:组内最后一行不画,免得和表格外框叠成两道线。</param>
public sealed record XServerHelpRow(string Syntax, string Description, string Example, Thickness Separator)
{
    /// <summary>有没有示例。</summary>
    public bool HasExample => Example.Length > 0;
}

/// <summary>帮助表里的一组(已本地化、已过滤)。</summary>
/// <param name="Title">分组标题。</param>
/// <param name="Rows">组内的行。</param>
/// <param name="Extensions">可运行时开关的扩展名单;只有「扩展」一组非空。</param>
public sealed record XServerHelpSection(string Title, IReadOnlyList<XServerHelpRow> Rows, IReadOnlyList<string> Extensions)
{
    /// <summary>这一组要不要显示扩展名单。</summary>
    public bool HasExtensions => Extensions.Count > 0;
}

/// <summary>VcXsrv 命令行参数帮助对话框(设置 → X Server →「附加参数」旁的「帮助」)。无 ViewModel。</summary>
/// <remarks>
/// 内容来自 <see cref="XServerHelpCatalog" />,打开时按当前界面语言取一次说明文字;
/// 过滤框按参数写法与说明文字同时匹配(不区分大小写),整组都滤空的组不显示。
/// </remarks>
public partial class XServerHelpDialog : Window
{
    private static readonly Thickness RowSeparator = new(0, 0, 0, 1);

    /// <summary>全部分组的本地化快照(过滤在它上面做,不必每敲一个字就重新查资源)。</summary>
    private readonly IReadOnlyList<(string Title, IReadOnlyList<(XServerHelpEntry Entry, string Description)> Entries, bool IsExtensions)> _all;

    /// <summary>初始化对话框并填入全部参数。</summary>
    public XServerHelpDialog()
    {
        InitializeComponent();
        _all =
        [
            .. XServerHelpCatalog.Groups.Select(group => (
                Strings.Get(group.TitleKey),
                (IReadOnlyList<(XServerHelpEntry, string)>)[.. group.Entries.Select(e => (e, Strings.Get(e.DescriptionKey)))],
                group.TitleKey == "XHelpGroup_Extensions"))
        ];
        ApplyFilter(string.Empty);
    }

    /// <summary>当前显示的分组(过滤后)。单测据此断言过滤结果。</summary>
    internal IReadOnlyList<XServerHelpSection> VisibleSections { get; private set; } = [];

    /// <summary>按关键字过滤;空串显示全部。</summary>
    internal void ApplyFilter(string? query)
    {
        string q = query?.Trim() ?? string.Empty;
        List<XServerHelpSection> sections = [];
        foreach ((string title, IReadOnlyList<(XServerHelpEntry Entry, string Description)> entries, bool isExtensions) in _all)
        {
            List<(XServerHelpEntry Entry, string Description)> matched =
            [
                .. entries.Where(e => q.Length == 0
                                      || e.Entry.Syntax.Contains(q, StringComparison.OrdinalIgnoreCase)
                                      || e.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            ];
            // 扩展名单也参与匹配:搜 "RANDR" 应该能落到「扩展」这一组。
            bool extensionHit = isExtensions && q.Length > 0
                                && XServerHelpCatalog.ToggleableExtensions.Any(x => x.Contains(q, StringComparison.OrdinalIgnoreCase));
            if (matched.Count == 0 && !extensionHit)
            {
                continue;
            }
            if (extensionHit && matched.Count == 0)
            {
                matched = [.. entries];
            }
            XServerHelpRow[] rows =
            [
                .. matched.Select((e, i) => new XServerHelpRow(
                    e.Entry.Syntax,
                    e.Description,
                    e.Entry.Example ?? string.Empty,
                    i == matched.Count - 1 ? default : RowSeparator))
            ];
            sections.Add(new(title, rows, isExtensions ? XServerHelpCatalog.ToggleableExtensions : []));
        }

        VisibleSections = sections;
        GroupsList.ItemsSource = sections;
        NoMatchText.IsVisible = sections.Count == 0;
    }

    private void FilterBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter(FilterBox.Text);

    // 推迟关闭:同步 Close 会让本轮点击/按键的后续路由打到已销毁的窗口刷
    // "PlatformImpl is null" 警告(见 WindowCloseExtensions)。
    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    /// <summary>Esc:过滤框里有字时先清空,再按一次才关闭。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(FilterBox.Text))
            {
                FilterBox.Text = string.Empty;
            }
            else
            {
                this.PostClose();
            }
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }
}
