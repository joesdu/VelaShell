using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using VelaShell.Infrastructure.Plugins;

namespace VelaShell.Services.Plugins;

/// <summary>
/// 进程内插件卸载前,让 Avalonia 忘掉插件的类型:<see cref="AvaloniaPropertyRegistry.UnregisterByModule" />
/// 清掉属性注册表里按类型登记的条目,以及各属性以插件类型为键的元数据缓存(插件控件被渲染过就会有,
/// 例如 <c>Visual.ClipToBounds</c>)。这些键经 <c>LoaderAllocator</c> 拽着整个可收集 ALC,不清就回收不掉。
/// </summary>
/// <remarks>
/// 排到 UI 线程上做:这些字典是普通 <see cref="Dictionary{TKey, TValue}" />,UI 线程在读属性时会往里写缓存,
/// 从别的线程改会把它弄坏。异步无妨 —— ALC 本来就要在引用全部消失后的某一轮 GC 才会被收走。
/// </remarks>
internal sealed class AvaloniaPluginStateReleaser : IPluginSharedStateReleaser
{
    /// <inheritdoc />
    public void Release(string pluginId, IReadOnlyList<Type> types)
    {
        if (types.Count == 0)
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _ = AvaloniaPropertyRegistry.Instance.UnregisterByModule(types);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Plugins] Could not release Avalonia state of '{pluginId}': {ex.Message}");
            }
        });
    }
}
