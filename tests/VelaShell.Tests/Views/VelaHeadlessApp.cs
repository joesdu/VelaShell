using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using VelaShell.Tests.Views;

[assembly: AvaloniaTestApplication(typeof(VelaHeadlessApp))]

namespace VelaShell.Tests.Views;

/// <summary>
/// 本程序集所有 headless 视图测试共用的宿主,经 <see cref="AvaloniaTestApplicationAttribute" />
/// 注册,各测试类用 <c>HeadlessUnitTestSession.GetOrStartForAssembly</c> 取同一个会话。
/// </summary>
/// <remarks>
/// 必须共用:一个进程只允许一个 Avalonia Application。各测试类若各起各的 App,先跑的那个会赢,
/// 其余测试就悄悄地对着别人的样式跑 —— 单独跑绿、全量跑红,且报错完全指不到症结。
/// 因此这里按真实 App.axaml 的顺序加载完整样式栈,让测试看到的就是生产里的那套。
/// </remarks>
public class VelaHeadlessApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Resources.MergedDictionaries.Add(LoadDictionary("avares://VelaShell.Controls/Themes/VelaTokens.axaml"));
        Resources.MergedDictionaries.Add(LoadDictionary("avares://VelaShell.Controls/Themes/VelaShellTokens.axaml"));
        Resources.MergedDictionaries.Add(LoadDictionary("avares://VelaShell.Controls/Themes/Icons.axaml"));
        Styles.Add(LoadStyles("avares://VelaShell/Themes/DockStyles.axaml"));
        Styles.Add(LoadStyles("avares://VelaShell/Themes/InputStyles.axaml"));
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<VelaHeadlessApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

    private static ResourceInclude LoadDictionary(string uri) => new(new Uri(uri)) { Source = new(uri) };

    private static StyleInclude LoadStyles(string uri) => new(new Uri(uri)) { Source = new(uri) };
}
