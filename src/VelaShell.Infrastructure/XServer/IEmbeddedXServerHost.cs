using VelaShell.XServer;

namespace VelaShell.Infrastructure.XServer;

/// <summary>
/// 内置 X 服务端的宿主:把服务端的顶层窗口显示成原生窗口,并把用户输入交回服务端。
/// 由界面层实现(Avalonia),经 DI 交给 <see cref="BuiltInLocalXServer" />。
/// </summary>
/// <remarks>
/// <see cref="IX11ServerHost" /> 的回调在服务端的执行线程上来,实现自己切 UI 线程(见该接口的说明)。
/// 这里多出来的两步是生命周期:服务端启动时 <see cref="AttachAsync" />,停下时 <see cref="Detach" />。
/// </remarks>
public interface IEmbeddedXServerHost : IX11ServerHost
{
    /// <summary>
    /// 服务端刚建好、还没开始监听:宿主记下它(注入输入用),并把当前的显示器布局、DPI 与键盘布局告诉它。
    /// 返回的任务完成之后才开始接受 X 客户端 —— 第一个客户端就能拿到正确的屏幕尺寸与 DPI。
    /// </summary>
    /// <param name="server">服务端。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task AttachAsync(X11Server server, CancellationToken cancellationToken);

    /// <summary>
    /// 设置里选的键盘布局(XKB 布局名,如 <c>de</c>);空串 = 跟随系统当前的布局。在 <see cref="AttachAsync" /> 之前调用。
    /// 默认实现什么也不做(不关心键盘布局的宿主不必实现)。
    /// </summary>
    /// <param name="layout">XKB 布局名;空串表示跟随系统。</param>
    void UseKeyboardLayout(string layout)
    {
    }

    /// <summary>
    /// 设置里选的窗口模式(<see cref="VelaShell.Core.XServer.XServerWindowModes" />)。在 <see cref="AttachAsync" /> 之前调用。
    /// 单窗口(rootful)模式下服务端有 <see cref="X11Server.Screen" />:宿主据此决定屏幕窗口带不带边框、是否全屏。默认实现什么也不做。
    /// </summary>
    /// <param name="mode">窗口模式。</param>
    void UseWindowMode(string mode)
    {
    }

    /// <summary>服务端要停了:关掉它所有顶层窗口对应的原生窗口,不再往它注入输入。可在任意线程上调用。</summary>
    void Detach();

    /// <summary>
    /// 服务端报来 <see cref="IX11ServerHost.ServerGrabStalled" />,宿主原样转出来:<see cref="BuiltInLocalXServer" /> 据此提示用户。
    /// 在服务端的执行线程上触发(已经放掉像素锁),处理要快、不许同步等服务端。默认实现从不触发。
    /// </summary>
    event EventHandler<XServerGrabStall>? GrabStallReported
    {
        add
        {
        }
        remove
        {
        }
    }
}
