using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using ReactiveUI.Avalonia;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Persistence;
using Velopack;

// ReSharper disable InconsistentNaming

namespace VelaShell;

internal static partial class Program
{
    // Held for the whole process lifetime so a second launch can detect us. Released on exit.
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // Enable legacy code pages (GBK, Big5, Shift_JIS, …) for the terminal encoding option.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        InstallGlobalExceptionGuards();
        VelopackApp.Build().Run();

        // Only one instance per user may run: SonnetDB holds an exclusive lock on its WAL, so a
        // second process would otherwise crash at startup with a file-in-use IOException. Detect
        // the running instance up front and exit cleanly with a friendly notice instead.
        if (!TryAcquireSingleInstanceLock())
        {
            ShowMessage(Strings.Get("Boot_AlreadyRunning"), "VelaShell");
            return;
        }
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Last-resort: surface a readable dialog instead of a raw .NET crash box for testers.
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
    /// Acquires a session-scoped named mutex keyed on the local data directory. Returns false when
    /// another instance already holds it — the common case being a double-click while the app is
    /// open. Keyed on the storage path so distinct Windows users (distinct %LocalAppData%) run
    /// independently. Uses the Local namespace (no SeCreateGlobalPrivilege needed, unlike Global);
    /// the rare same-user-across-sessions collision is caught later by SonnetDB's file lock and the
    /// startup error dialog, rather than silently proceeding into a crash.
    /// </summary>
    private static bool TryAcquireSingleInstanceLock()
    {
        try
        {
            string root = new VelaShellStoragePaths().RootDirectory;
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..16];
            _singleInstanceMutex = new(false, $"Local\\VelaShell-{key}");
            try
            {
                if (!_singleInstanceMutex.WaitOne(TimeSpan.Zero))
                {
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // Previous owner died without releasing (e.g. crash). We now own it — proceed.
            }
            return true;
        }
        catch
        {
            // Never let the guard itself block startup; fall back to allowing the launch.
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
            // Best-effort: process teardown releases the handle regardless.
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>Shows a native message box on Windows; falls back to Trace elsewhere.</summary>
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
    /// Last-resort guards so a background/reactive failure (e.g. an SSH auth exception raised
    /// off a command) is logged rather than terminating the whole client.
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

    // Avalonia configuration, don't remove; also used by visual designer.
    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .WithInterFont()
                  .LogToTrace()
                  .UseReactiveUI(_ => { });
}
