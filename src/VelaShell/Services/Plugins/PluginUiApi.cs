using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Ui;
using VelaShell.ViewModels;

namespace VelaShell.Services.Plugins;

/// <summary>
/// 每插件一个的界面能力(<see cref="IUiApi" />)实现(进程内插件):
/// 在宿主 UI 线程调用内容工厂拿到插件自己创建的 Avalonia 控件,按
/// <see cref="PanelOptions.DisplayMode" /> 呈现为停靠文档或独立窗口;
/// 实例释放(插件停用)时全部关闭 —— 插件离场,宿主 UI 不残留。
/// 往消息中心发通知转给共用的 <see cref="Infrastructure.Plugins.PluginNotificationService" />(隔离插件的
/// <c>ui/notify</c> 经插件上下文也落到这里),发送方就是构造时绑定的插件 id。
/// </summary>
internal sealed class PluginUiApi(
    string pluginId,
    IPluginLogger log,
    Func<MainWindowViewModel?> mainViewModel,
    IBackgroundActivityService? backgroundActivity = null,
    Infrastructure.Plugins.PluginNotificationService? notifications = null)
    : IUiApi, Infrastructure.Plugins.IPluginSurfaceSource, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<PluginPanel> _panels = [];
    private bool _disposed;

    /// <inheritdoc />
    public event Action? SurfacesChanged;

    /// <inheritdoc />
    /// <remarks>本实例只属于一个插件,清单用不上:数的就是它自己弹出去、还开着的面板。</remarks>
    public int CountOpenSurfaces(PluginSdk.PluginManifest manifest)
    {
        lock (_gate)
        {
            return _panels.Count;
        }
    }

    public async Task<IPluginPanel> ShowPanelAsync(PanelOptions options, Func<object> contentFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(contentFactory);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        // 插件多半在后台线程上调这个(SDK 允许),而下面整段要排到 UI 线程上执行:
        // 排队 + 构造控件这段时间右下角圆环转着,免得"点了一下面板,好一会儿没动静"。
        // 注意这里覆盖的只是**宿主这一段** —— 插件激活由 PluginManager 自己登记,
        // 面板打开后插件再去拉自己的数据,那段宿主看不见也管不着。
        using IBackgroundActivityScope? activity =
            backgroundActivity?.Begin(Strings.Get("Msg_PluginOpeningPanel"), options.Title);
        PluginPanel panel = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // 工厂在 UI 线程调用:插件可在其中放心构造任何 Avalonia 控件(含编译期 AXAML 视图)。
            Control content = contentFactory() as Control
                ?? throw new ArgumentException("contentFactory must return an Avalonia Control.", nameof(contentFactory));
            if (options.DisplayMode == PanelDisplayMode.Document)
            {
                MainWindowViewModel viewModel = mainViewModel()
                    ?? throw new InvalidOperationException("Host main window is not ready; cannot open a docked panel.");
                // 第一方 AI 的聊天面板(它总以 Placement=Right 请求)由宿主放进专属区;
                // 它另开的文档(改动对比等,Placement=Tabs)与通用插件一样走停靠契约进主标签区。
                // 专属区已有内容时同样走停靠 —— 再 Attach 会抛异常,插件只能退回成独立小窗。
                return pluginId == "velashell.ai"
                       && options.Placement == PanelPlacement.Right
                       && viewModel.AiAssistant.Content is null
                    ? new PluginPanel(pluginId, log, options, content, viewModel.AiAssistant)
                    : new PluginPanel(pluginId, log, options, content, viewModel.Layout);
            }
            return new PluginPanel(pluginId, log, options, content, MainWindow());
        });
        lock (_gate)
        {
            if (_disposed)
            {
                _ = panel.CloseAsync();
                throw new ObjectDisposedException(nameof(PluginUiApi));
            }
            _panels.Add(panel);
        }
        panel.Closed += () =>
        {
            bool removed;
            lock (_gate)
            {
                removed = _panels.Remove(panel);
            }
            if (removed)
            {
                RaiseSurfacesChanged();
            }
        };
        RaiseSurfacesChanged();
        return panel;
    }

    /// <inheritdoc />
    public bool SupportsNotifications => notifications is not null;

    /// <inheritdoc />
    public Task<PluginNotificationOutcome> PostNotificationAsync(PluginNotification notification,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return notifications is null
            ? throw new NotSupportedException("This host does not accept plugin notifications.")
            : notifications.PostAsync(pluginId, notification, cancellationToken);
    }

    private void RaiseSurfacesChanged()
    {
        try
        {
            SurfacesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            log.Error("Panel count change handler threw.", ex);
        }
    }

    private static Window? MainWindow() =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>插件停用:关闭其全部面板(封送到 UI 线程,尽力而为)。</summary>
    public void Dispose()
    {
        List<PluginPanel> panels;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            panels = [.. _panels];
            _panels.Clear();
        }
        // 插件在停用:它的状态本身就要变,不必再为面板数单独通知一次。
        SurfacesChanged = null;
        foreach (PluginPanel panel in panels)
        {
            _ = panel.CloseAsync();
        }
    }
}
