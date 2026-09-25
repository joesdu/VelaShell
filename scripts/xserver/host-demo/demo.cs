#:project ../../../src/VelaShell/VelaShell.csproj
#:property TreatWarningsAsErrors=false
#:property SignAssembly=false

// 内置 X 服务端 + VelaShell 的 Avalonia 宿主(AvaloniaXServerHost),不经主程序、不碰用户设置:
// 真的原生窗口、真的输入,拿来手动看 M3 的宿主行为。
//
//   dotnet run scripts/xserver/host-demo/demo.cs [显示号,默认 20]
//
// 服务端监听 127.0.0.1:6000+N;另开一个 0.0.0.0:7000+N 的转发,把 Docker 容器(靶场镜像 velashell-xclients)
// 经 host.docker.internal 来的连接转成本机连接 —— 服务端没配 cookie 时只放行本机连接:
//
//   docker run --rm velashell-xclients sh -c "DISPLAY=host.docker.internal:$((1000+N)) xeyes"
//
// 关掉那个小主窗口即退出。

using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using VelaShell.Services.XServer;
using VelaShell.XServer;

int display = args.Length > 0 && int.TryParse(args[0], out int n) ? n : 20;
DemoApp.Display = display;
AppBuilder.Configure<DemoApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);

sealed class DemoApp : Application
{
    public static int Display { get; set; }

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Window main = new() { Title = $"VelaShell X host demo :{Display}", Width = 420, Height = 80 };
            desktop.MainWindow = main;
            main.Opened += async (_, _) => await StartAsync(main);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartAsync(Window main)
    {
        AvaloniaXServerHost host = new();
        X11Server server = new(new X11ServerOptions { DisplayNumber = Display, Log = line => Console.WriteLine($"[x] {line}") }, host);
        await host.AttachAsync(server, CancellationToken.None);
        await server.StartAsync();
        _ = RelayAsync(7000 + Display, 6000 + Display);
        main.Content = new TextBlock
        {
            Margin = new Thickness(12),
            Text = $"listening on 127.0.0.1:{6000 + Display}, relay 0.0.0.0:{7000 + Display} → DISPLAY=host.docker.internal:{1000 + Display}",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        main.Closed += async (_, _) =>
        {
            host.Detach();
            await server.DisposeAsync();
        };
        Console.WriteLine($"ready: DISPLAY=host.docker.internal:{1000 + Display}");
    }

    /// <summary>0.0.0.0:from → 127.0.0.1:to 的字节转发(让容器来的连接在服务端看来是本机连接)。</summary>
    private static async Task RelayAsync(int from, int to)
    {
        TcpListener listener = new(IPAddress.Any, from);
        listener.Start();
        while (true)
        {
            TcpClient outer = await listener.AcceptTcpClientAsync();
            _ = Task.Run(async () =>
            {
                using TcpClient inner = new();
                await inner.ConnectAsync(IPAddress.Loopback, to);
                using (outer)
                {
                    NetworkStream a = outer.GetStream(), b = inner.GetStream();
                    await Task.WhenAny(a.CopyToAsync(b), b.CopyToAsync(a));
                }
            });
        }
    }
}
