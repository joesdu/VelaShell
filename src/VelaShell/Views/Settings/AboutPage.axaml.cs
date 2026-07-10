using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VelaShell.Views.Settings;

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
    }

    /// <summary>点击依赖项目名/许可证时,用系统默认浏览器打开对应链接(URL 存放在控件 Tag)。</summary>
    private async void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string url } || string.IsNullOrWhiteSpace(url))
        {
            return;
        }
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            await top.Launcher.LaunchUriAsync(uri);
        }
    }
}
