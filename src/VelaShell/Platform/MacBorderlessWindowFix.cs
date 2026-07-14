using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace VelaShell.Platform;

/// <summary>
/// macOS 无边框(<c>WindowDecorations="None"</c>)+ <c>SizeToContent</c> 对话框的“底部按钮点不动”修复。
///
/// <para>现象(用户反馈,macOS 26 ARM64;Windows 不受影响):新建连接 / 验证 / 消息等自适应高度的
/// 无边框弹窗,上半部输入框可正常聚焦输入,但底部页脚按钮(测试 / 保存 / 连接 等)绘制出来却收不到
/// 鼠标点击。</para>
///
/// <para>根因:这些弹窗以 <see cref="SizeToContent.Height" /> 在 <c>Opened</c> 阶段自动增高,但此刻原生
/// 窗口/视图尚未完全就绪,原生命中区域(hit region / tracking area)被固定在增高前的初始高度 ——
/// 于是新增出来的底部区域虽已渲染却不在可命中范围内。</para>
///
/// <para>修复:窗口完全显示之后(以及每次内容变化再次自动增高后),显式重新下发一次窗口尺寸,
/// 迫使原生窗口按当前实际高度重建命中区域。用重入标记 + 延迟复位吞掉本次微调自身触发的
/// <c>SizeChanged</c>,避免递归。仅在 macOS 生效,其它平台为空操作。</para>
/// </summary>
public static class MacBorderlessWindowFix
{
    private static readonly ConditionalWeakTable<Window, Guard> Guards = [];

    private sealed class Guard
    {
        public bool Busy;
    }

    /// <summary>为无边框、自适应高度的对话框挂上 macOS 命中区域修复;非 macOS 平台不做任何事。</summary>
    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        window.Opened += (_, _) => Reassert(window);
        window.SizeChanged += (_, _) => Reassert(window);
    }

    private static void Reassert(Window window)
    {
        Guard guard = Guards.GetOrCreateValue(window);
        if (guard.Busy)
        {
            return;
        }
        guard.Busy = true;
        // 在当前布局/显示流程之后再刷新,确保原生视图此时已经就绪。
        Dispatcher.UIThread.Post(() =>
        {
            if (!window.IsVisible)
            {
                guard.Busy = false;
                return;
            }
            double h = window.Bounds.Height;
            if (!double.IsFinite(h) || h <= 1)
            {
                guard.Busy = false;
                return;
            }
            // 暂时关闭自动尺寸,先把高度 +1 下发一次;下一帧再复位到 h。分两帧确保产生两次
            // 真实的原生 resize(同帧内连续赋值可能被合并成无变化的空操作),从而强制原生窗口
            // 按最终高度重建命中区域。整个过程 guard.Busy 保持置位,吞掉自身引发的 SizeChanged。
            SizeToContent keep = window.SizeToContent;
            window.SizeToContent = SizeToContent.Manual;
            window.Height = h + 1;
            Dispatcher.UIThread.Post(() =>
            {
                window.Height = h;
                window.SizeToContent = keep;
                // 再更低优先级复位,吞掉本次微调自身引发的 SizeChanged,断开递归。
                Dispatcher.UIThread.Post(() => guard.Busy = false, DispatcherPriority.Background);
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }
}
