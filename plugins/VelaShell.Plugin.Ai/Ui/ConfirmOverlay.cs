using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 盖在视图上的独立确认框:整块遮罩 + 居中的卡片(标题、说明、「取消」与危险操作按钮)。
/// </summary>
/// <remarks>
/// <para>
/// 会删东西的确认(换源清 Key、换源退出登录)原先靠同一个按钮点两下:第一下只提示,第二下就清 ——
/// 连点两下就算确认了,提示还没读完 Key 已经没了。插件窗口走宿主的 <c>PanelDisplayMode.Window</c>,
/// SDK 没有给插件弹模态框的契约,插件也不自己 new Window(见 <c>ChatPanelView.Dialogs</c>),
/// 所以在视图里盖一层:遮罩吞掉底下的点击,确认只能点框里那枚按钮。
/// </para>
/// <para>
/// 版式照宿主 <c>MessageDialog</c>:48 高的头部(警示图标 + 标题)、说明、52 高的按钮栏,卡片圆角 8、
/// <c>VelaBgSurface</c> + <c>VelaBorderSecondary</c> 描边 + <c>VelaShadowWindow</c>。危险按钮是描边 + <c>VelaError</c>
/// (DESIGN.md §5.1,不用实心红块)。默认焦点在「取消」,Esc 取消,Tab 只在框内循环;
/// 框打开期间视图被拆下(关窗、换页)按取消收场。
/// </para>
/// </remarks>
public sealed class ConfirmOverlay : Panel
{
    private readonly Border _scrim;
    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private readonly Button _confirm;
    private readonly Button _cancel;
    private TaskCompletionSource<bool>? _pending;

    /// <summary>建一个收起的确认框;放在视图根的最后一个子元素上,铺满整个视图。</summary>
    public ConfirmOverlay()
    {
        IsVisible = false;
        ZIndex = 100;

        Border scrim = _scrim = new Border { Name = "ConfirmScrim" };
        scrim[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaScrim");
        // 点在遮罩上不关框:要么点「取消」,要么按 Esc —— 误点一下外面不该被当成任何一种选择
        scrim.PointerPressed += (_, e) => e.Handled = true;

        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 24,
            Height = 24,
            Stretch = Stretch.None,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        icon[!Avalonia.Controls.Shapes.Path.DataProperty] = new DynamicResourceExtension("Icon.triangle-alert");
        icon[!Avalonia.Controls.Shapes.Shape.StrokeProperty] = new DynamicResourceExtension("VelaWarning");
        _title = new TextBlock
        {
            Name = "ConfirmTitle",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _title[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextPrimary");
        _title[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize13");
        var header = new Grid { ColumnDefinitions = [with("Auto,*")] };
        header.Children.Add(new Viewbox
        {
            Width = 15,
            Height = 15,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = icon
        });
        Grid.SetColumn(_title, 1);
        header.Children.Add(_title);
        var headerBar = new Border
        {
            Height = 48,
            Padding = new Thickness(20, 0),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = header
        };
        headerBar[!Border.BorderBrushProperty] = new DynamicResourceExtension("VelaBorderPrimary");

        _message = new TextBlock { Name = "ConfirmMessage", TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        _message[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextSecondary");
        _message[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize12");

        _cancel = new Button { Name = "ConfirmCancelButton", Classes = { "host" }, MinWidth = 72 };
        _cancel.Click += (_, _) => Close(false);
        _confirm = new Button { Name = "ConfirmAcceptButton", Classes = { "host" }, MinWidth = 72 };
        _confirm[!Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("VelaError");
        _confirm[!Avalonia.Controls.Primitives.TemplatedControl.BorderBrushProperty] = new DynamicResourceExtension("VelaError");
        _confirm.Click += (_, _) => Close(true);
        var buttonBar = new Border
        {
            Height = 52,
            Padding = new Thickness(20, 0),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _cancel, _confirm }
            }
        };
        buttonBar[!Border.BorderBrushProperty] = new DynamicResourceExtension("VelaBorderPrimary");

        var card = new Border
        {
            Name = "ConfirmCard",
            Width = 440,
            MaxWidth = 440,
            Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new StackPanel
            {
                Children = { headerBar, new Border
                    {
                        Padding = new Thickness(20, 16),
                        // 说明可能很长(扩展包要列出每台 MCP 服务器的完整启动命令):超出就在框内滚动,按钮栏始终看得见
                        Child = new ScrollViewer { MaxHeight = 360, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _message }
                    }, buttonBar }
            }
        };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgSurface");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("VelaBorderSecondary");
        card[!Border.BoxShadowProperty] = new DynamicResourceExtension("VelaShadowWindow");
        KeyboardNavigation.SetTabNavigation(card, KeyboardNavigationMode.Cycle);

        Children.Add(scrim);
        Children.Add(card);
    }

    /// <summary>
    /// 遮罩的圆角:与宿主窗口卡片的底边圆角对齐(插件窗口内容区上方是标题栏,下方两角是 7 = 外框 8 − 1px 描边),
    /// 否则方角的遮罩会盖到窗口那段弧线外面去。
    /// </summary>
    public CornerRadius ScrimCornerRadius
    {
        get => _scrim.CornerRadius;
        set => _scrim.CornerRadius = value;
    }

    /// <summary>框正开着(等用户选)。</summary>
    public bool IsOpen => _pending is not null;

    /// <summary>
    /// 打开确认框,等用户点「确认」(true)或取消(false:点「取消」、按 Esc、视图被拆下)。
    /// 已经开着一个就先按取消收掉那一个 —— 同一时刻只问一件事。
    /// </summary>
    public Task<bool> AskAsync(string title, string message, string confirmText, string cancelText)
    {
        Close(false);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = pending;
        _title.Text = title;
        _message.Text = message;
        _confirm.Content = confirmText;
        _cancel.Content = cancelText;
        Avalonia.Automation.AutomationProperties.SetName(_confirm, confirmText);
        Avalonia.Automation.AutomationProperties.SetName(_cancel, cancelText);
        IsVisible = true;
        // 会删东西的确认,默认落在「取消」上:顺手一个回车不该把 Key 清掉
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_pending, pending)) _cancel.Focus();
        });
        return pending.Task;
    }

    /// <summary>按取消收起(换了选择、重载了页面时由视图调用);没开着就什么都不做。</summary>
    public void Dismiss() => Close(false);

    private void Close(bool result)
    {
        TaskCompletionSource<bool>? pending = _pending;
        _pending = null;
        IsVisible = false;
        pending?.TrySetResult(result);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsOpen && e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Close(false);
        base.OnDetachedFromVisualTree(e);
    }
}
