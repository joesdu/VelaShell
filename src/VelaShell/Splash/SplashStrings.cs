using System.Reflection;
using VelaShell.Core.Resources;

namespace VelaShell.Splash;

/// <summary>
/// 启动画面上的全部文案,按调用线程当前的界面文化一次取齐。
/// </summary>
/// <remarks>
/// 启动画面线程在取词前把自己的 <c>CurrentUICulture</c> 设成设置里的界面语言
/// (镜像文件里带着,见 <c>StartupAppearance</c>);设置页预览在 UI 线程上取,那里的文化本来就是界面语言。
/// </remarks>
internal sealed record SplashStrings(
    string[] Stages,
    string Starting,
    string Ready,
    string Tagline,
    string ConstellationName,
    string[] MascotLines,
    string MascotHello,
    string MascotReady,
    string Version,
    string Copyright)
{
    /// <summary>按当前线程的界面文化取齐文案。</summary>
    /// <returns>启动画面文案。</returns>
    public static SplashStrings Load()
    {
        Assembly assembly = typeof(SplashStrings).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                         ?? assembly.GetName().Version?.ToString(3)
                         ?? "0.0.0";
        string company = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "";
        return new(
            [
                Strings.Get("Splash_StageRuntime"),
                Strings.Get("Splash_StageDatabase"),
                Strings.Get("Splash_StageInterface"),
                Strings.Get("Splash_StageSessions"),
                Strings.Get("Splash_StageWindow")
            ],
            Strings.Get("Splash_Starting"),
            Strings.Get("Splash_Ready"),
            Strings.Get("Splash_Tagline"),
            Strings.Get("Splash_ConstellationName"),
            [
                // 运行时那一段在画面出现之前就结束了,轮不到它说话;占位用招呼语。
                Strings.Get("Splash_MascotHello"),
                Strings.Get("Splash_MascotDatabase"),
                Strings.Get("Splash_MascotInterface"),
                Strings.Get("Splash_MascotSessions"),
                Strings.Get("Splash_MascotWindow")
            ],
            Strings.Get("Splash_MascotHello"),
            Strings.Get("Splash_MascotReady"),
            "v" + version,
            string.IsNullOrWhiteSpace(company) ? "" : "© " + company);
    }

    /// <summary>一行状态:进行中的阶段名加省略号,全部完成是「准备就绪」,还没开始是「正在启动…」。</summary>
    /// <param name="frame">当前帧。</param>
    /// <returns>状态文案。</returns>
    public string Status(SplashFrame frame) =>
        frame.IsDone ? Ready : frame.Current >= 0 ? Stages[frame.Current] + "…" : Starting;
}
