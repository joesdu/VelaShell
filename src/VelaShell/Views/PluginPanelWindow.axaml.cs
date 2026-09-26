using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace VelaShell.Views;

/// <summary>
/// 插件面板的独立窗口。窗体规格与资源监视/任务管理器一致
/// (按平台的卡片外框 + 自绘缩放抓取区,见 <see cref="WindowChrome" />),内容为插件自己创建的 Avalonia 控件。
/// </summary>
public partial class PluginPanelWindow : Window
{
    /// <summary>初始化窗口,按平台装外框(最大化时卡片铺满、抓取区让位也由 <see cref="WindowChrome" /> 管)。</summary>
    public PluginPanelWindow()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Tool, ResizeGrips);
    }

    /// <summary>设置面板内容(插件自己创建的控件)。</summary>
    public void SetContent(Control content) => PanelContent.Content = content;

    /// <summary>设置标题栏文本:面板标题 + 所属插件 id。</summary>
    public void SetTitle(string title, string pluginId)
    {
        Title = title;
        TitleText.Text = title;
        SubtitleText.Text = pluginId;
    }

    /// <summary>
    /// 设置标题栏图标。插件没自报(或路径解析不了)就保持 AXAML 里那个通用插头 ——
    /// 标题栏上少一个图标会让整行文字左移,比画一个通用的更难看。
    /// </summary>
    /// <param name="icon">插件在 <c>PanelOptions.Icon</c> 里交出来的图标。</param>
    public void SetIcon(Services.TabIcon? icon)
    {
        if (icon is null)
        {
            return;
        }
        TitleIcon.Data = icon.Geometry;
        TitleIcon.ViewBoxSize = icon.ViewBoxSize;
        TitleIcon.Fill = icon.Fill;
    }

    /// <summary>
    /// 把插件声明的标题栏动作按钮插到最小化键左侧(按给出的顺序)。
    /// 与三连按钮同一套 caption 样式,只是图标换成插件给的路径、悬停带提示。
    /// </summary>
    public void SetTitleActions(IReadOnlyList<PluginSdk.Ui.PanelTitleAction> actions)
    {
        for (int i = 0; i < actions.Count; i++)
        {
            PluginSdk.Ui.PanelTitleAction action = actions[i];
            var icon = new Controls.Controls.LucideIcon { Width = 12, Height = 12, Data = StreamGeometry.Parse(action.IconPathData) };
            icon.Bind(Controls.Controls.LucideIcon.ForegroundProperty, this.GetResourceObservable("VelaTextSecondary"));
            var button = new Button { Classes = { "caption" }, Content = icon };
            ToolTip.SetTip(button, action.ToolTip);
            button.Click += (_, _) => action.OnClick();
            CaptionButtons.Children.Insert(i, button);
        }
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        this.BeginWindowMoveDrag(e);
    }

    /// <summary>缩放抓取区。只认左键:系统 sizing 模态循环只在左键弹起时退出(#116)。</summary>
    private void ResizeEdge_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (WindowState == WindowState.Normal
            && sender is Border { Tag: string tag }
            && Enum.TryParse(tag, out WindowEdge edge))
        {
            BeginResizeDrag(edge, e);
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
