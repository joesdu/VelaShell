using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 「配置工具」窗口:左栏分节导航(扩展包 / MCP 服务器 / 技能 / 工具,带计数),右栏「标题 · 说明 · 搜索 · 列表」。
/// 版式参照 VS Code Copilot 的配置界面。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么改成这样</b>:以前是「左栏 MCP 服务器概览 + 右栏一长串分组」,内置工具、技能、扩展包、各台 MCP 的工具全叠在一页里,
/// 东西一多就分不清哪块是哪块。现在一节一页:页头说清这一页管什么,搜索只在这一页里筛。
/// </para>
/// <para>
/// <b>行的样子统一</b>:每个来源一行(折叠箭头 + 名字 / 副标题 + 计数 + 操作 + 开关),展开后是逐项的开关行。
/// 「工具」页按来源分两区:内置工具按类别(取自 <see cref="ToolIcons" /> 的类别,与工具卡片同一套),
/// MCP 每台服务器一行(行尾开关是服务器本身的启停,展开逐个工具)。搜索时只留匹配的项,含匹配的来源自动展开。
/// </para>
/// <para>
/// 勾选状态<b>存的是「没勾的那些」</b>(<see cref="McpServerConfig.DisabledTools" /> / <see cref="AiSettings.DisabledBuiltinTools" /> /
/// <see cref="AiSettings.DisabledSkills" />):以后新增的工具、技能默认可用,而不是因为不在白名单里被静默屏蔽。
/// 每次改动当场保存,没有「保存」按钮。服务器<b>怎么配</b>仍在 <see cref="McpServersView" /> 那张表单里,点服务器行或「新增服务器」打开。
/// </para>
/// </remarks>
public sealed partial class ToolPickerView : UserControl
{
    /// <summary>左栏的分节。</summary>
    internal enum Page
    {
        Extensions,
        McpServers,
        Skills,
        Tools
    }

    private readonly IPluginContext _context;
    private readonly AiSettings _settings;
    private readonly Loc _loc;
    private readonly Func<Task> _persist;
    private readonly Action<string?> _openServerEditor;

    private readonly ListBox _nav = new() { Name = "ToolsNav", Classes = { "nav" } };
    private readonly TextBlock _title = new() { Name = "PageTitle", Classes = { "pageTitle" } };
    private readonly TextBlock _description = new() { Name = "PageDescription", Classes = { "pageDesc" }, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
    private readonly StackPanel _pageActions = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _search = new() { Name = "ToolsSearch", Height = 28, MinHeight = 28, Padding = new Avalonia.Thickness(8, 0) };
    private readonly StackPanel _content = new() { Name = "PageContent", Spacing = 2 };
    private readonly TextBlock _status = new() { Name = "ToolsStatus", Classes = { "dim" }, TextWrapping = TextWrapping.Wrap };

    private Page _page = Page.Tools;
    private bool _syncingNav;

    /// <summary>
    /// 各来源行的折叠状态,键 = 来源 id。只活在这个视图里,不落盘:重建(改服务器 / 刷新工具库)时保住刚才折 / 展的样子就够了。
    /// 默认折起 —— 一台 MCP 服务器动辄二三十个工具,全展开这页长得没法看;折着也能从计数与开关看出个大概。
    /// </summary>
    private readonly Dictionary<string, bool> _expanded = [with(StringComparer.Ordinal)];

    /// <param name="context">插件上下文(记日志、取插件数据目录)。</param>
    /// <param name="settings">面板共享的设置实例;开关直接改它。</param>
    /// <param name="loc">多语言文案。</param>
    /// <param name="persist">把设置落盘(拨一下就存一次)。</param>
    /// <param name="openServerEditor">打开某台服务器的配置表单;参数为 null 表示「新增一台」。</param>
    /// <param name="mcpChanged">MCP 服务器启停、扩展包启停 / 卸载 / 重新确认之后调用,让面板对齐 MCP 连接;可为空。</param>
    public ToolPickerView(IPluginContext context, AiSettings settings, Loc loc, Func<Task> persist,
        Action<string?> openServerEditor, Func<Task>? mcpChanged = null)
    {
        _context = context;
        _settings = settings;
        _loc = loc;
        _persist = persist;
        _openServerEditor = openServerEditor;
        _mcpChanged = mcpChanged;
        // 这套版式规则原先由插件自己的对话框外壳下发;窗体换成宿主的自绘卡片之后自己带上
        Styles.Add(new StyleInclude(new Uri("avares://VelaShell.Plugin.Ai/"))
        {
            Source = new Uri("avares://VelaShell.Plugin.Ai/Ui/DialogStyles.axaml")
        });
        // 开关与勾选框用插件自己画的那套:Fluent 默认是高饱和蓝,跟本程序的强调色不是一回事
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://VelaShell.Plugin.Ai/"))
        {
            Source = new Uri("avares://VelaShell.Plugin.Ai/Ui/AiTheme.axaml")
        });

        // ── 左栏:分节导航 ──
        _nav.SelectionChanged += (_, _) =>
        {
            if (_syncingNav || _nav.SelectedItem is not ListBoxItem { Tag: Page page } || page == _page) return;
            _page = page;
            _search.Text = "";
            RenderPage();
        };
        var rail = new Border
        {
            Width = 210,
            Padding = new Avalonia.Thickness(0, 14, 0, 14),
            Child = _nav,
            BorderThickness = new Avalonia.Thickness(0, 0, 1, 0)
        };
        rail[!BackgroundProperty] = new DynamicResourceExtension("VelaBgSidebar");
        rail[!BorderBrushProperty] = new DynamicResourceExtension("VelaBorderPrimary");

        // ── 右栏:页头(标题 + 页内操作)、说明、搜索,下面是可滚动的列表,最底下一行状态 ──
        _search.PlaceholderText = _loc["ToolsSearch"];
        _search[!FontSizeProperty] = new DynamicResourceExtension("VelaFontSize12");
        _search.TextChanged += (_, _) => RenderContent();
        AutomationProperties.SetName(_search, _loc["ToolsSearch"]);

        var head = new Grid { ColumnDefinitions = [with("*,Auto")] };
        head.Children.Add(_title);
        Grid.SetColumn(_pageActions, 1);
        head.Children.Add(_pageActions);

        // 内容区限宽居中:窗口拉宽时一行不至于拉到一屏那么长,开关离名字太远就对不上了
        var header = new StackPanel { Spacing = 0, MaxWidth = 860 };
        header.Children.Add(head);
        header.Children.Add(_description);
        header.Children.Add(new Border { Height = 12 });
        header.Children.Add(_search);
        header.Children.Add(new Border { Height = 10 });
        DockPanel.SetDock(header, Dock.Top);

        _status.Margin = new Avalonia.Thickness(0, 10, 0, 0);
        _status.MaxWidth = 860;
        DockPanel.SetDock(_status, Dock.Bottom);

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // 覆盖式滚动条会压住右缘:留白放在内容上
            Content = new Border { Padding = new Avalonia.Thickness(0, 0, 12, 0), MaxWidth = 872, Child = _content }
        };
        var right = new DockPanel { Margin = new Avalonia.Thickness(28, 20, 16, 16) };
        right.Children.Add(header);
        right.Children.Add(_status);
        right.Children.Add(scroll);

        var root = new Grid { ColumnDefinitions = [with("Auto,*")] };
        root.Children.Add(rail);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        // 确认框(扩展包安装 / 卸载 / 重新确认 MCP)盖在整个视图上
        Content = new Panel { Children = { root, _confirm } };
        Rebuild();
    }

    /// <summary>当前在哪一节(测试用)。</summary>
    internal Page CurrentPage => _page;

    /// <summary>切到某一节(测试用;界面上由左栏切换)。</summary>
    internal void ShowPage(Page page)
    {
        _page = page;
        _search.Text = "";
        Rebuild();
    }

    /// <summary>整窗重建:左栏计数与当前这一节(改完服务器、刷新工具库、启停之后都走这里)。</summary>
    public void Rebuild()
    {
        RebuildNav();
        RenderPage();
    }

    private void RebuildNav()
    {
        _syncingNav = true;
        try
        {
            _nav.Items.Clear();
            AddNav(Page.Extensions, _loc["ExtensionsGroup"], Extensions.List().Count);
            AddNav(Page.McpServers, _loc["McpServers"], _settings.McpServers.Count);
            AddNav(Page.Skills, _loc["SkillsGroup"], ScanSkills().Count(s => s.IsValid));
            AddNav(Page.Tools, _loc["ToolsNavTools"], CountEnabled());
            _nav.SelectedItem = _nav.Items.OfType<ListBoxItem>().First(i => (Page)i.Tag! == _page);
        }
        finally
        {
            _syncingNav = false;
        }
    }

    private void AddNav(Page page, string label, int count)
    {
        var row = new Grid { ColumnDefinitions = [with("*,Auto")] };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var number = new TextBlock { Classes = { "count" }, Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(number, 1);
        row.Children.Add(number);
        var item = new ListBoxItem { Name = "Nav" + page, Tag = page, Content = row };
        AutomationProperties.SetName(item, $"{label} {count}");
        _nav.Items.Add(item);
    }

    /// <summary>换页:页头(标题、说明、页内操作)与列表一起换。</summary>
    private void RenderPage()
    {
        _pageActions.Children.Clear();
        (string title, string description) = _page switch
        {
            Page.Extensions => (_loc["ExtensionsGroup"], _loc["PageExtensionsDesc"]),
            Page.McpServers => (_loc["McpServers"], _loc["McpHint"]),
            Page.Skills => (_loc["SkillsGroup"], _loc["PageSkillsDesc"]),
            _ => (_loc["ToolsNavTools"], _loc["PageToolsDesc"])
        };
        _title.Text = title;
        _description.Text = description;
        switch (_page)
        {
            case Page.Extensions: AddExtensionActions(); break;
            case Page.McpServers: AddServerActions(); break;
            case Page.Skills: AddSkillActions(); break;
        }
        RenderContent();
    }

    /// <summary>只重画列表(搜索词变了时走这里,页头不动)。</summary>
    private void RenderContent()
    {
        _content.Children.Clear();
        switch (_page)
        {
            case Page.Extensions: BuildExtensionsPage(); break;
            case Page.McpServers: BuildServersPage(); break;
            case Page.Skills: BuildSkillsPage(); break;
            default: BuildToolsPage(); break;
        }
        if (_content.Children.Count == 0 && Query.Length > 0)
        {
            _content.Children.Add(new TextBlock { Name = "ToolsNoMatch", Classes = { "dim" }, Text = _loc["ToolsNoMatch"], Margin = new Avalonia.Thickness(6, 4) });
        }
        _status.Text = _loc.F("ToolsSelected", CountEnabled());
    }

    // ───────────────────────── 搜索 ─────────────────────────

    private string Query => _search.Text?.Trim() ?? "";

    /// <summary>这几段文字里有没有搜索词(没搜索时恒为 true)。</summary>
    private bool Matches(params string?[] texts)
        => Query.Length == 0 || texts.Any(t => t?.Contains(Query, StringComparison.OrdinalIgnoreCase) == true);

    // ───────────────────────── 通用行 ─────────────────────────

    /// <summary>分区标题:「内置 23」「MCP 2」。</summary>
    private static StackPanel SectionLabel(string text, int count)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Avalonia.Thickness(6, 12, 0, 4) };
        row.Children.Add(new TextBlock { Classes = { "sectionLabel" }, Text = text });
        row.Children.Add(new Border
        {
            Classes = { "countBadge" },
            Child = new TextBlock { Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        });
        return row;
    }

    /// <summary>
    /// 一个来源一行:折叠箭头(有子项时)+ 名字 / 副标题 + 计数 + 操作 + 开关;展开后在下面列出 <paramref name="items" />。
    /// </summary>
    /// <param name="key">折叠状态的键。</param>
    /// <param name="name">名字。</param>
    /// <param name="subtitle">副标题(说明、路径、来源)。</param>
    /// <param name="count">行尾计数(如 <c>8/8</c>);可为空。</param>
    /// <param name="actions">行尾的操作按钮;可为空。</param>
    /// <param name="on">开关状态;null = 部分启用;不给开关时传 <paramref name="setOn" /> 为空。</param>
    /// <param name="setOn">开关被拨动时;为空则这一行没有开关。</param>
    /// <param name="items">展开后的子项;为空则这一行不可折展。</param>
    /// <param name="onClick">没有子项时点整行做什么(例如打开服务器表单)。</param>
    private Border SourceRow(string key, string name, string? subtitle, string? count, Control? actions, bool? on,
        Action<bool>? setOn, IReadOnlyList<Control>? items = null, Action? onClick = null)
    {
        bool expandable = items is { Count: > 0 };
        bool expanded = expandable && (Query.Length > 0 || _expanded.GetValueOrDefault(key));

        var grid = new Grid { ColumnDefinitions = [with("18,*,Auto,Auto,Auto")] };
        if (expandable)
        {
            var chevron = new Viewbox
            {
                Width = 11,
                Height = 11,
                Child = Glyph("Icon.chevron-down"),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = Avalonia.RelativePoint.Center,
                RenderTransform = expanded ? null : new RotateTransform(-90)
            };
            grid.Children.Add(chevron);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(2, 0, 12, 0) };
        text.Children.Add(new TextBlock { Classes = { "sourceName" }, Text = name });
        if (!string.IsNullOrEmpty(subtitle)) text.Children.Add(new TextBlock { Classes = { "sourceSub" }, Text = subtitle });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (!string.IsNullOrEmpty(count))
        {
            var number = new TextBlock { Name = "SourceCount", Classes = { "sourceCount" }, Text = count, Margin = new Avalonia.Thickness(0, 0, 10, 0) };
            Grid.SetColumn(number, 2);
            grid.Children.Add(number);
        }
        if (actions is not null)
        {
            actions.Margin = new Avalonia.Thickness(0, 0, 10, 0);
            actions.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(actions, 3);
            grid.Children.Add(actions);
        }
        if (setOn is not null)
        {
            CheckBox toggle = Switch(on, setOn, name);
            Grid.SetColumn(toggle, 4);
            grid.Children.Add(toggle);
        }

        var header = new Border { Classes = { "sourceRow" }, Child = grid };
        if (expandable || onClick is not null)
        {
            header.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            header.PointerPressed += (_, e) =>
            {
                // 点在开关或按钮上不算点行
                if (e.Source is Avalonia.Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
                if (e.Source is Avalonia.Visual w && w.FindAncestorOfType<CheckBox>(includeSelf: true) is not null) return;
                e.Handled = true;
                if (expandable)
                {
                    _expanded[key] = !_expanded.GetValueOrDefault(key);
                    RenderContent();
                }
                else
                {
                    onClick!();
                }
            };
        }

        var block = new StackPanel();
        block.Children.Add(header);
        if (expandable)
        {
            var rows = new StackPanel { Classes = { "toolRows" }, IsVisible = expanded };
            foreach (Control item in items!) rows.Children.Add(item);
            block.Children.Add(rows);
        }
        var source = new Border { Classes = { "toolSource" }, Tag = key, Child = block };
        AutomationProperties.SetName(source, name);
        return source;
    }

    /// <summary>展开后的一行:工具名(等宽)+ 只读标记 + 说明,行尾开关。</summary>
    private Border ItemRow(string name, string description, bool readOnly, bool on, Action<bool> setOn)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new TextBlock { Classes = { "toolName" }, Text = name });
        if (readOnly) title.Children.Add(new TextBlock { Classes = { "dim" }, Text = _loc["ToolReadOnly"], VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 0, 12, 0) };
        text.Children.Add(title);
        if (description.Length > 0)
        {
            text.Children.Add(new TextBlock { Classes = { "sourceSub" }, Text = description, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
        }
        var grid = new Grid { ColumnDefinitions = [with("*,Auto")] };
        grid.Children.Add(text);
        CheckBox toggle = Switch(on, setOn, name);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return new Border { Classes = { "itemRow" }, Tag = name, Child = grid };
    }

    /// <summary>行尾的开关(套 <c>AiSwitchTheme</c> 的 CheckBox;<paramref name="on" /> 为 null 时显示「部分启用」)。</summary>
    private static CheckBox Switch(bool? on, Action<bool> setOn, string name)
    {
        var toggle = new CheckBox { Name = "Switch", IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        toggle[!ThemeProperty] = new DynamicResourceExtension("AiSwitchTheme");
        AutomationProperties.SetName(toggle, name);
        toggle.IsCheckedChanged += (_, _) => setOn(toggle.IsChecked == true);
        return toggle;
    }

    /// <summary>一组开关的合计状态:全开 true、全关 false、有开有关 null。</summary>
    private static bool? Aggregate(int on, int total) => on == total ? true : on == 0 ? false : null;

    /// <summary>页头 / 行尾的描边小按钮。</summary>
    private static Button HeaderButton(string name, string text)
    {
        var button = new Button
        {
            Name = name,
            Content = text,
            Height = 26,
            Padding = new Avalonia.Thickness(12, 0),
            [!FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11"),
            VerticalAlignment = VerticalAlignment.Center
        };
        button[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        return button;
    }

    /// <summary>警示色的一行字(解析失败、需要重新确认)。</summary>
    private static TextBlock WarningText(string name, string text)
    {
        var warning = new TextBlock
        {
            Name = name,
            Classes = { "sourceSub" },
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None
        };
        warning[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaWarning");
        return warning;
    }

    /// <summary>
    /// 一枚 lucide 图标。几何与描边都用 <see cref="DynamicResourceExtension" /> 绑,
    /// <b>不要 FindResource 取一次</b> —— Vela* 颜色令牌住在 ThemeDictionaries 里,一次性取值经常拿到 null。
    /// </summary>
    private static Avalonia.Controls.Shapes.Path Glyph(string icon)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Width = 24,
            Height = 24,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        path[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension(icon);
        path[!Shape.StrokeProperty] = new DynamicResourceExtension("VelaTextSecondary");
        return path;
    }

    // ───────────────────────── 保存与计数 ─────────────────────────

    private void AfterChange()
    {
        _ = _persist();
        Rebuild();
    }

    /// <summary>当前启用的工具总数(内置 + 各台启用中的 MCP 服务器)。</summary>
    private int CountEnabled()
    {
        HashSet<string> builtinOff = Lines(_settings.DisabledBuiltinTools);
        int total = AgentToolbox.Catalog.Count(t => !builtinOff.Contains(t.Name));
        foreach (McpServerConfig server in _settings.McpServers.Where(s => s.Enabled))
        {
            HashSet<string> off = Lines(server.DisabledTools);
            total += server.KnownTools.Count(t => !off.Contains(t.Name));
        }
        return total;
    }

    private static HashSet<string> Lines(string text)
        => new(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    private static void Toggle(HashSet<string> disabled, string name, bool enabled)
    {
        if (enabled) disabled.Remove(name);
        else disabled.Add(name);
    }
}
