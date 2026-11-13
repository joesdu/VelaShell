namespace VelaShell.Infrastructure.Plugins;

/// <summary>
/// 进程内插件的 ALC 卸载前,释放与插件共享的框架(Avalonia)里按类型登记 / 缓存的东西。
/// </summary>
/// <remarks>
/// <para>
/// 插件 ALC 与宿主共用一套 Avalonia(见 <c>PluginAssemblyLoadContext</c>)。Avalonia 的属性系统是进程级的:
/// 插件的控件一旦被布局、渲染过,宿主属性(如 <c>Visual.ClipToBounds</c>)的元数据缓存里就多了一条以插件类型为键的记录,
/// 而插件类型经 <c>LoaderAllocator</c> 拽住整个可收集 ALC —— 停用之后插件的程序集永远留在内存里,
/// Windows 上还连带缓存着的 PDB 句柄,把插件安装目录锁到宿主退出。
/// </para>
/// <para>
/// Infrastructure 不引用 Avalonia,所以由 UI 层实现并注册(见 <see cref="PluginManagerOptions.SharedStateReleaser" />)。
/// 只覆盖框架按类型缓存的那部分;插件自己往 Avalonia 全局注册的属性(<c>AvaloniaProperty.Register</c>、
/// <c>AddOwner</c>)这里放不掉,插件不该那样做(见插件开发指南)。
/// </para>
/// </remarks>
public interface IPluginSharedStateReleaser
{
    /// <summary>
    /// 释放共享框架里与这些类型相关的状态。在卸载 ALC 之前调用;实现可以把工作排到 UI 线程上异步完成 ——
    /// ALC 只有在这些引用消失之后才会被回收,卸载观察本来就是多轮 GC。
    /// </summary>
    /// <param name="pluginId">插件 id(日志用)。</param>
    /// <param name="types">插件 ALC 里全部程序集的类型。</param>
    void Release(string pluginId, IReadOnlyList<Type> types);
}
