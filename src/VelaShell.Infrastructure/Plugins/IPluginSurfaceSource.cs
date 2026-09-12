using VelaShell.PluginSdk;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>
/// 宿主里"某插件现在开着几个界面"的一个来源:停靠面板 / 独立窗口、工作台文档、协议文件会话。
/// <para>
/// 插件管理页据此把"已激活、但名下一个标签都没开着"的插件显示为后台运行,而不是一律"运行中" ——
/// 进程内插件激活后常驻(<see cref="PluginIdlePolicy.KeepAlive" />),关掉它的标签并不会让它停下来,
/// 只写"运行中"会让人以为关标签没生效。
/// </para>
/// </summary>
public interface IPluginSurfaceSource
{
    /// <summary>该插件在本来源里开着的界面数。</summary>
    /// <param name="manifest">插件清单:会话按其声明的协议 / 工作台 id 归属到插件。</param>
    /// <returns>开着的界面数。</returns>
    int CountOpenSurfaces(PluginManifest manifest);

    /// <summary>开着的界面数可能变化时触发;可能在任意线程上触发。</summary>
    event Action? SurfacesChanged;
}
