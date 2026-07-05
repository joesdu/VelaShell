using Avalonia;
using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using ReactiveUI.Avalonia;
using Velopack;

namespace PulseTerm.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Enable legacy code pages (GBK, Big5, Shift_JIS, …) for the terminal encoding option.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        InstallGlobalExceptionGuards();

        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Last-resort guards so a background/reactive failure (e.g. an SSH auth exception raised
    /// off a command) is logged rather than terminating the whole client.
    /// </summary>
    private static void InstallGlobalExceptionGuards()
    {
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Trace.WriteLine($"[PulseTerm] Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Trace.WriteLine($"[PulseTerm] Unhandled domain exception: {e.ExceptionObject}");
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI(builder => { });

        // Silence Dock.Avalonia's benign "DockCapability" binding warnings while keeping all
        // other diagnostics. LogToTrace has already installed the trace sink at this point.
        if (Avalonia.Logging.Logger.Sink is { } sink and not Logging.FilteringLogSink)
            Avalonia.Logging.Logger.Sink = new Logging.FilteringLogSink(sink);

        return builder;
    }
}
