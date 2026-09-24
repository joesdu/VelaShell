using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.XServer;

namespace VelaShell.Infrastructure.XServer;

/// <summary>
/// 按设置里的引擎(<see cref="XServerOptions.Engine" />)在内置 X 服务端与 VcXsrv 之间转发的
/// <see cref="ILocalXServer" />:标题栏按钮、设置页、SSH 的 X11 转发都只认这一个。
/// </summary>
/// <remarks>
/// <para>
/// <b>正在运行的那个优先。</b>用户在 VcXsrv 运行时把引擎切成内置,按钮上看到的、SSH 用的仍是 VcXsrv,
/// 直到它被停掉 —— 与「改动在下一次启动 X Server 时生效」同一个口径,不会去停一个正在显示窗口的 X 服务端。
/// </para>
/// <para>
/// VcXsrv 只在 Windows 上;其它平台上设置里即使写着 vcxsrv(比如从 Windows 同步过来的配置),也按内置处理。
/// </para>
/// </remarks>
public sealed class LocalXServerSelector : ILocalXServer
{
    private readonly ISettingsService _settings;
    private readonly ILocalXServer _builtIn;
    private readonly ILocalXServer _vcXsrv;
    private volatile string _engine = XServerEngines.BuiltIn;

    /// <summary>构造。</summary>
    /// <param name="settings">设置服务(读引擎;保存时跟着变)。</param>
    /// <param name="builtIn">内置引擎。</param>
    /// <param name="vcXsrv">VcXsrv 引擎。</param>
    public LocalXServerSelector(ISettingsService settings, ILocalXServer builtIn, ILocalXServer vcXsrv)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _builtIn = builtIn ?? throw new ArgumentNullException(nameof(builtIn));
        _vcXsrv = vcXsrv ?? throw new ArgumentNullException(nameof(vcXsrv));
        _builtIn.StateChanged += (_, e) => StateChanged?.Invoke(this, e);
        _vcXsrv.StateChanged += (_, e) => StateChanged?.Invoke(this, e);
        _settings.SettingsSaved += OnSettingsSaved;
    }

    /// <summary>内置引擎各平台都能用,所以总是支持。</summary>
    public bool IsSupported => true;

    /// <inheritdoc />
    public XServerState State => Active.State;

    /// <inheritdoc />
    public int? DisplayNumber => Active.DisplayNumber;

    /// <inheritdoc />
    public string? Display => Active.Display;

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <summary>找 VcXsrv 的可执行文件(内置引擎没有可执行文件)。</summary>
    public string? FindExecutable(string? configuredPath) => _vcXsrv.FindExecutable(configuredPath);

    /// <inheritdoc />
    public async Task<XServerStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (Running is { } running)
        {
            return await running.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        return await (await ChosenAsync().ConfigureAwait(false)).StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        await _builtIn.StopAsync().ConfigureAwait(false);
        await _vcXsrv.StopAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<XServerDisplayResolution> ResolveForwardingDisplayAsync(CancellationToken cancellationToken = default)
    {
        ILocalXServer target = Running ?? await ChosenAsync().ConfigureAwait(false);
        return await target.ResolveForwardingDisplayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在运行或正在启动的那个;都没有时为 <see langword="null" />。</summary>
    private ILocalXServer? Running =>
        _builtIn.State != XServerState.Stopped ? _builtIn
        : _vcXsrv.State != XServerState.Stopped ? _vcXsrv
        : null;

    /// <summary>界面上该显示的那个:在运行的优先,否则按最近一次知道的设置。</summary>
    private ILocalXServer Active => Running ?? Pick(_engine);

    /// <summary>按最新保存的设置选(启动与自动启动走这里,不用缓存值)。</summary>
    private async Task<ILocalXServer> ChosenAsync()
    {
        _engine = (await _settings.GetSnapshotAsync().ConfigureAwait(false)).XServer.Engine;
        return Pick(_engine);
    }

    private ILocalXServer Pick(string engine) =>
        engine == XServerEngines.VcXsrv && _vcXsrv.IsSupported ? _vcXsrv : _builtIn;

    private void OnSettingsSaved(AppSettings settings)
    {
        string engine = settings.XServer.Engine;
        if (engine != _engine)
        {
            _engine = engine;
            StateChanged?.Invoke(this, EventArgs.Empty);   // 按钮的提示 / 可用性跟着引擎变
        }
    }
}
