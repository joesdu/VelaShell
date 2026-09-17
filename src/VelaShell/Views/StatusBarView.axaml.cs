using Avalonia.Controls;
using Avalonia.Interactivity;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>状态栏视图,展示当前连接状态与相关运行时信息。</summary>
public partial class StatusBarView : UserControl
{
    private const string IssuesUrl = "https://github.com/joesdu/VelaShell/issues";

    /// <summary>初始化 <see cref="StatusBarView"/> 并加载 XAML 组件。</summary>
    public StatusBarView() => InitializeComponent();

    /// <summary>点击反馈入口:在系统默认浏览器中打开 GitHub Issues 页。</summary>
    private void Issues_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (TopLevel.GetTopLevel(this) is { } top && Uri.TryCreate(IssuesUrl, UriKind.Absolute, out Uri? uri))
        {
            await top.Launcher.LaunchUriAsync(uri);
        }
    });
}
