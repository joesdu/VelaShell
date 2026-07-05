using Avalonia;
using System;
using System.Text;
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

        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI(builder => { });
}
