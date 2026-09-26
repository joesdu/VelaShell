using System.Reflection;
using System.Runtime.Loader;
using VelaShell.Plugin.Ai.Bridge;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Interop;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Hosting;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.Ai;

/// <summary>
/// AI 助手插件入口。<b>启动即激活</b>(见 <c>plugin.json</c> 的 <c>onStartup</c>)——
/// IM 桥接要常驻,不能等用户点开面板才建连;聊天面板本身仍是首次触发命令才构造。
/// 命令:打开聊天(标签页/窗口)、解释当前终端输出。
/// </summary>
[VelaPlugin]
public sealed class AiPlugin : IVelaPlugin
{
    private IPluginContext? _context;
    private AiSettingsStore? _store;
    private IPluginPanel? _panel;
    private IPluginPanel? _collaborationPanel;
    private ChatPanelView? _view;
    private readonly List<IDisposable> _commands = [];

    /// <summary>IM 桥接(设置页保存后调它的 <c>ReloadAsync</c>)。</summary>
    public BridgeService? Bridge { get; private set; }

    /// <summary>对外的 MCP 服务端(设置页保存后调它的 <c>ReloadAsync</c>)。</summary>
    public McpEndpoint? McpServer { get; private set; }

    /// <inheritdoc />
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _store = new AiSettingsStore(context);
        RegisterCommands();
        // 命令标题本地化:语言切换时按同 id 重注册替换
        context.Events.LocaleChanged += _ => RegisterCommands();
        StartServices(context, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 拉起两条常驻服务:IM 桥接(往外接飞书/钉钉/Telegram)与 MCP 服务端
    /// (往内让 Claude Code / Codex 这类外部 agent 调 VelaShell)。
    /// </summary>
    /// <remarks>
    /// <b>不 await。</b>建连与监听都要碰系统资源,而插件激活是在启动路径上 ——
    /// 一台连不上的飞书、一个被占用的端口,都不该把 VelaShell 的启动拖住。
    /// 两者关着时各自读一次配置就返回。
    /// </remarks>
    private void StartServices(IPluginContext context, CancellationToken cancellationToken)
    {
        Bridge = new BridgeService(context, _store!);
        McpServer = new McpEndpoint(context, new McpServerSettingsStore(context));
        _ = Task.Run(async () =>
        {
            try
            {
                await Bridge.ReloadAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                context.Log.Error($"Starting the IM bridge failed: {ex}");
            }
            try
            {
                await McpServer.ReloadAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                context.Log.Error($"Starting the MCP server failed: {ex}");
            }
            // 两条服务先起来,再去预热面板的依赖:预热是纯磁盘活,让它排在后面,
            // 免得和建连抢这一会儿的 IO。
            PrewarmPanelAssemblies(context, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// 把聊天面板要用的那几十兆依赖<b>提前</b>装进来(后台线程,尽力而为)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 面板是在 <b>UI 线程</b>上构造的(宿主把内容工厂排进 <c>Dispatcher.UIThread.InvokeAsync</c>,
    /// 见 <c>PluginUiApi.ShowPanelAsync</c>),而第一次构造会连带把 LiveMarkdown / Mermaid /
    /// CSharpMath / Svg / Anthropic / OpenAI 这一串程序集从盘上读进来 —— 本插件目录下是
    /// 三十来个 dll、三十多兆。文件缓存冷的时候这就是几秒钟的磁盘 IO,而且<b>整段压在 UI 线程上</b>:
    /// 表现就是"点开 AI 面板,主窗口直接变成未响应"。
    /// (实测同一台机器上,光是激活期要碰的那 5 个程序集,冷缓存 2925 ms、热缓存 28 ms ——
    /// 差的全是 IO,不是代码。)
    /// </para>
    /// <para>
    /// 本插件本来就是 <c>onStartup</c> 常驻的,这份代价早晚要付;挪到启动之后的后台线程上付,
    /// 用户点开时就只剩控件构造那点事。
    /// </para>
    /// <para>
    /// <b>只装载程序集,不碰里面的类型。</b>装载本身线程安全,而类型的静态构造不是 ——
    /// Avalonia 控件的样式属性注册就跑在静态构造里,这会儿 UI 线程正忙着建主窗口,
    /// 在后台线程上把它们引爆等于两头同时往同一张注册表里写。静态构造留给 UI 线程
    /// 第一次真正用到时再跑,那时该付的 IO 已经付过了。
    /// </para>
    /// </remarks>
    /// <param name="context">日志用。</param>
    /// <param name="cancellationToken">
    /// 宿主在关时立刻收手 —— 冷缓存下这一趟能跑十几秒,退出不该陪它读完盘。
    /// </param>
    private static void PrewarmPanelAssemblies(IPluginContext context, CancellationToken cancellationToken)
    {
        Assembly self = typeof(AiPlugin).Assembly;
        // 两个条件缺一不可。
        // 其一,装载上下文必须是 PluginAssemblyLoadContext —— 这既是"我确实是作为插件被装载的"
        // 的判据,也意味着目录里躺着的正好是我自己那份私有依赖,可以整目录横扫。
        // 换个环境(headless 测试、宿主直接引用)这里是默认 ALC,目录里是别人的东西,不能碰。
        // 其二,要走这个上下文去装而不是 Assembly.Load:Avalonia* 与 SDK 得回落到宿主那一份,
        // 规矩写在 PluginAssemblyLoadContext 里,绕过它就会装出第二套类型。
        if (AssemblyLoadContext.GetLoadContext(self) is not PluginAssemblyLoadContext loadContext
            || Path.GetDirectoryName(self.Location) is not { Length: > 0 } directory)
        {
            return; // 不是插件装载路径(或单文件装载,没有目录):跳过,面板照常能开。
        }
        int warmed = 0;
        foreach (string path in Directory.EnumerateFiles(directory, "*.dll"))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            try
            {
                loadContext.LoadFromAssemblyName(AssemblyName.GetAssemblyName(path));
                warmed++;
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException
                                          or FileNotFoundException or IOException or UnauthorizedAccessException)
            {
                // 原生 dll、或者这一个就是装不动:预热失败只是没热到,不该影响任何事。
                context.Log.Warn($"Preloading '{Path.GetFileName(path)}' failed: {ex.Message}");
            }
        }
        context.Log.Info($"Preloaded {warmed} plugin assemblies; the first chat panel no longer pays for them on the UI thread.");
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        // 命令、事件订阅与面板由宿主自动清理;这里只拆自己的引用。
        _view?.Detach();
        _view = null;
        _panel = null;
        _commands.Clear();
        if (Bridge is { } bridge)
        {
            await bridge.DisposeAsync();
            Bridge = null;
        }
        if (McpServer is { } mcp)
        {
            await mcp.DisposeAsync();
            McpServer = null;
        }
        _context = null;
    }

    private void RegisterCommands()
    {
        IPluginContext context = _context!;
        var loc = new Loc(context.Host.Locale);
        foreach (IDisposable command in _commands)
        {
            command.Dispose();
        }
        _commands.Clear();
        _commands.Add(context.Commands.Register(new PluginCommandDescriptor(
            $"{context.PluginId}.chat", loc["CmdChat"], "AI",
            _ => OpenPanelAsync(PanelDisplayMode.Document))));
        _commands.Add(context.Commands.Register(new PluginCommandDescriptor(
            $"{context.PluginId}.chat-window", loc["CmdChatWindow"], "AI",
            _ => OpenPanelAsync(PanelDisplayMode.Window))));
        _commands.Add(context.Commands.Register(new PluginCommandDescriptor(
            $"{context.PluginId}.explain-terminal", loc["CmdExplain"], "AI",
            ExplainTerminalAsync)));
        _commands.Add(context.Commands.Register(new PluginCommandDescriptor(
            $"{context.PluginId}.collaboration", loc["CmdCollaboration"], "AI",
            _ => OpenCollaborationAsync())));
    }

    /// <summary>
    /// 打开「协作接入」设置窗口(IM 桥接 + 对外 MCP 服务端)。
    /// </summary>
    /// <remarks>
    /// 这一页<b>不挂在聊天面板下面</b>:它配的是两条常驻服务,与"当前这段对话"无关,
    /// 而且用户很可能根本没开过聊天面板就想去配它。所以入口在命令面板上,自己一个窗口。
    /// </remarks>
    private async Task OpenCollaborationAsync()
    {
        IPluginContext context = _context!;
        if (_collaborationPanel is { IsOpen: true } opened)
        {
            await opened.ActivateAsync();
            return;
        }
        var loc = new Loc(context.Host.Locale);
        _collaborationPanel = await context.Ui.ShowPanelAsync(
            new PanelOptions
            {
                Title = loc["Collaboration"],
                Icon = AiIcon.Panel,
                DisplayMode = PanelDisplayMode.Window,
                WindowWidth = 860,
                WindowHeight = 780
            },
            () => new CollaborationView(context, loc, RestartServicesAsync, Bridge));
        _collaborationPanel.Closed += () => _collaborationPanel = null;
    }

    /// <summary>设置页保存后按新配置重起两条服务。</summary>
    private async Task RestartServicesAsync()
    {
        if (Bridge is { } bridge)
        {
            await bridge.ReloadAsync();
        }
        if (McpServer is { } mcp)
        {
            await mcp.ReloadAsync();
        }
    }

    private async Task<ChatPanelView?> OpenPanelAsync(PanelDisplayMode mode)
    {
        IPluginContext context = _context!;
        if (_panel is { IsOpen: true } && _view is not null)
        {
            return _view; // 已开着(面板是活控件)
        }
        ChatPanelView? view = null;
        _panel = await context.Ui.ShowPanelAsync(
            new PanelOptions
            {
                Title = new Loc(context.Host.Locale)["Title"],
                // 标签页/标题栏上认得出是 AI 的那个标。不给的话宿主画一个通用插头 ——
                // 它对每个插件都一样,等于没有。
                Icon = AiIcon.Panel,
                DisplayMode = mode,
                // 聊天面板的位置对齐 VSCode 的 Copilot:标签区右侧独立一栏,
                // 终端与它并排看得见,而不是把当前终端顶掉
                Placement = PanelPlacement.Right,
                PlacementRatio = await PanelWidthRatioAsync(),
                WindowWidth = 720,
                WindowHeight = 560
            },
            () => view = new ChatPanelView(context, _store!));
        _view = view;
        _panel.PlacementRatioChanged += RememberPanelWidth;
        _panel.Closed += () =>
        {
            _view?.Detach();
            _view = null;
            _panel = null;
        };
        return _view;
    }

    /// <summary>
    /// 读上次记住的侧栏宽度。读不到就用 <see cref="PanelOptions" /> 的默认值 ——
    /// 面板本身能不能打开,不该被一条装饰性配置卡住。
    /// </summary>
    private async Task<double> PanelWidthRatioAsync()
    {
        try
        {
            AiSettings settings = await _store!.LoadAsync();
            return settings.PanelWidthPercent / 100.0;
        }
        catch (Exception ex)
        {
            _context!.Log.Warn($"Reading the panel width setting failed, using the default: {ex.Message}");
            return new PanelOptions { Title = "" }.PlacementRatio;
        }
    }

    /// <summary>
    /// 把用户拖出来的宽度记下来,下次打开就是这个宽度。
    /// </summary>
    /// <remarks>
    /// 这一项<b>不在设置页里</b>:让人去填一个百分比,不如直接把他拖出来的结果记住。
    /// 宿主在拖动<b>结束</b>时才通知一次(见 <c>IPluginPanel.PlacementRatioChanged</c>),
    /// 所以直接落盘,不必防抖。四舍五入到整百分比,免得配置里躺着 30.000000000000004。
    ///
    /// <para><b>交给面板去写,不要自己 Load-改-Save。</b>面板持有整份设置,它每次保存都是
    /// 整体覆盖 —— 背着它写库的话,下一次换模式/勾工具就会拿它内存里那份旧宽度盖回来,
    /// 表现就是"拖了不算数,重开还是老样子"。</para>
    /// </remarks>
    private void RememberPanelWidth(double ratio)
    {
        if (!double.IsFinite(ratio))
        {
            return;
        }
        _view?.RememberPanelWidth(Math.Clamp((int)Math.Round(ratio * 100), 15, 85));
    }

    private async Task ExplainTerminalAsync(CancellationToken cancellationToken)
    {
        IPluginContext context = _context!;
        var loc = new Loc(context.Host.Locale);
        IReadOnlyList<SessionInfo> sessions = await context.Sessions.ListAsync(cancellationToken);
        if (sessions.FirstOrDefault(s => s.State == SessionState.Connected) is not { } session)
        {
            context.Log.Warn(loc["NoConnectedSession"]);
            return;
        }
        string output = await context.Terminal.GetOutputAsync(session.SessionId, 200, cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
        {
            context.Log.Warn("Terminal buffer is empty; nothing to explain.");
            return;
        }
        ChatPanelView? view = await OpenPanelAsync(PanelDisplayMode.Document);
        view?.SendExternal($"{loc["ExplainPrompt"]}```\n{output}\n```");
    }
}
