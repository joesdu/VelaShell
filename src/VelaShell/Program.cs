using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using ReactiveUI.Avalonia;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Services.Update;

// ReSharper disable InconsistentNaming

namespace VelaShell;

internal static partial class Program
{
    // 整个进程生命周期内持有,以便第二次启动能检测到我们。退出时释放。
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // 启用旧代码页(GBK、Big5、Shift_JIS 等)以支持终端编码选项。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        InstallGlobalExceptionGuards();

        // 每个用户只允许运行一个实例:SonnetDB 对其 WAL 持有独占锁,否则第二个进程会在启动时
        // 因文件被占用而抛出 IOException 崩溃。改为在启动前检测运行中的实例,并以友好提示干净退出。
        // 自更新后重启(--after-update)时,前一个进程仍在关闭中,因此等待其释放锁,而非立即退出。
        bool afterUpdate = args.Contains("--after-update", StringComparer.Ordinal);
        if (!TryAcquireSingleInstanceLock(afterUpdate ? TimeSpan.FromSeconds(15) : TimeSpan.Zero))
        {
            ShowMessage(Strings.Get("Boot_AlreadyRunning"), "VelaShell");
            return;
        }
        try
        {
            FinalizePendingUpdate();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // 最后手段:向测试人员弹出可读对话框,而非原始的 .NET 崩溃框。
            Trace.WriteLine($"[VelaShell] Fatal startup error: {ex}");
            ShowMessage(Strings.Format("Boot_StartupFailed", ex.Message), Strings.Get("Boot_StartupErrorTitle"));
            throw;
        }
        finally
        {
            ReleaseSingleInstanceLock();
        }
    }

    /// <summary>
    /// 完成上一轮留下的自更新:删除成功交换后遗留的 *.old 文件,或回滚中途崩溃的交换。
    /// 旧进程可能仍在退出并持有其(已重命名的)映像文件,因此删除失败会在后台短暂重试,
    /// 若仍失败则留待下次启动再处理。永不抛异常。
    /// </summary>
    private static void FinalizePendingUpdate()
    {
        string appDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        UpdateApplier applier = new(appDir);
        if (applier.TryFinalizeStartup())
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                if (applier.TryFinalizeStartup())
                {
                    return;
                }
            }
        });
    }

    /// <summary>
    /// 获取一个以本地数据目录为键、作用域限于会话的命名互斥体。当已有其他实例持有时返回 false
    /// —— 常见情形是应用已打开时的双击启动。以存储路径为键,使不同的 Windows 用户
    /// (不同的 %LocalAppData%)各自独立运行。使用 Local 命名空间(无需 Global 那样的
    /// SeCreateGlobalPrivilege);罕见的同用户跨会话冲突,会在之后由 SonnetDB 的文件锁与启动错误
    /// 对话框捕获,而非静默继续直至崩溃。
    /// </summary>
    private static bool TryAcquireSingleInstanceLock(TimeSpan waitTimeout)
    {
        try
        {
            string root = new VelaShellStoragePaths().RootDirectory;
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..16];
            _singleInstanceMutex = new(false, $"Local\\VelaShell-{key}");
            try
            {
                if (!_singleInstanceMutex.WaitOne(waitTimeout))
                {
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // 前持有者未释放即终止(例如崩溃)。现在归我们所有 —— 继续。
            }
            return true;
        }
        catch
        {
            // 绝不让该守卫自身阻塞启动;退路为允许启动。
            return true;
        }
    }

    private static void ReleaseSingleInstanceLock()
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // 尽力而为:进程卸载时无论如何都会释放句柄。
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>在 Windows 上显示原生消息框;其他平台退路为 Trace。</summary>
    private static void ShowMessage(string text, string caption)
    {
        if (OperatingSystem.IsWindows())
        {
            const uint MB_OK = 0x0, MB_ICONINFORMATION = 0x40;
            MessageBoxW(IntPtr.Zero, text, caption, MB_OK | MB_ICONINFORMATION);
        }
        else
        {
            Trace.WriteLine($"[VelaShell] {caption}: {text}");
        }
    }

    /// <summary>
    /// 最后手段的守卫:使后台/响应式失败(例如命令触发的 SSH 认证异常)被记录,
    /// 而非终止整个客户端。
    /// </summary>
    private static void InstallGlobalExceptionGuards()
    {
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Trace.WriteLine($"[VelaShell] Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Trace.WriteLine($"[VelaShell] Unhandled domain exception: {e.ExceptionObject}");
    }

    // Avalonia 配置,勿删除;可视化设计器也用到。
    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
#if LINUX
                  .UseWayland()
#endif
                  .WithInterFont()
                  .LogToTrace()
                  .UseReactiveUI(_ => { });
}
