using System.Text;
using VelaShell.Core.Models;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 启动画面要用的那几项设置:主题、强调色覆盖、界面语言与启动画面样式。
/// </summary>
/// <remarks>
/// <para>
/// 启动画面是在 <c>Main</c> 里、Avalonia 与 SonnetDB 都还没起来的时候画的 —— 冷启动时那之后还要等
/// 好几秒才轮得到读设置。所以这几项和渲染模式(<see cref="VelaShellStoragePaths.RenderModeFile" />)
/// 一样,保存设置时额外镜像成一个小文件(<see cref="VelaShellStoragePaths.StartupAppearanceFile" />),
/// 启动路径上只做一次 <c>File.ReadAllText</c>。
/// </para>
/// <para>
/// 格式是几行 <c>key=value</c>,不用 JSON:启动画面线程上能少装载一个程序集就少一个,
/// 而这几个值本身都不含换行。认不出来的行一律跳过,缺的项回到出厂值 —— 镜像坏了最多是
/// 启动画面配色不对,绝不能挡住启动。
/// </para>
/// </remarks>
/// <param name="Theme">主题 Id(含 <c>system</c>)。</param>
/// <param name="Accent">强调色覆盖(<c>#RRGGBB</c>);空 = 跟随主题。</param>
/// <param name="Language">界面语言(BCP-47);空 = 跟随系统。</param>
/// <param name="SplashStyle">启动画面样式(<see cref="SplashStyles" />)。</param>
public sealed record StartupAppearance(string Theme, string Accent, string Language, string SplashStyle)
{
    /// <summary>没有镜像文件时(首次运行、从没保存过设置)用的出厂值,与 <see cref="AppSettings" /> 的默认值一致。</summary>
    public static StartupAppearance Default { get; } = From(new AppSettings());

    /// <summary>从一份完整设置里取出启动画面要的那几项。</summary>
    /// <param name="settings">应用设置。</param>
    /// <returns>对应的启动外观。</returns>
    public static StartupAppearance From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(
            settings.Theme ?? "",
            settings.AccentColor ?? "",
            settings.Language ?? "",
            SplashStyles.Normalize(settings.Appearance?.SplashStyle));
    }

    /// <summary>序列化成镜像文件的内容。</summary>
    /// <returns>几行 <c>key=value</c>。</returns>
    public string Serialize()
    {
        StringBuilder text = new();
        text.Append("theme=").Append(OneLine(Theme)).Append('\n');
        text.Append("accent=").Append(OneLine(Accent)).Append('\n');
        text.Append("language=").Append(OneLine(Language)).Append('\n');
        text.Append("splash=").Append(OneLine(SplashStyle)).Append('\n');
        return text.ToString();
    }

    /// <summary>解析镜像文件的内容;认不出来的行跳过,缺的项取 <see cref="Default" />。</summary>
    /// <param name="text">文件内容。</param>
    /// <returns>解析结果(永不为 <see langword="null" />)。</returns>
    public static StartupAppearance Parse(string? text)
    {
        StartupAppearance result = Default;
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }
        foreach (string rawLine in text.Split('\n'))
        {
            int separator = rawLine.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            string key = rawLine[..separator].Trim();
            string value = rawLine[(separator + 1)..].Trim();
            result = key switch
            {
                "theme" => result with { Theme = value },
                "accent" => result with { Accent = value },
                "language" => result with { Language = value },
                "splash" => result with { SplashStyle = SplashStyles.Normalize(value) },
                _ => result
            };
        }
        return result;
    }

    /// <summary>读镜像文件;不存在或读不了时返回 <see cref="Default" />。永不抛异常。</summary>
    /// <param name="path">镜像文件路径。</param>
    /// <returns>启动外观。</returns>
    public static StartupAppearance Read(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Default;
        }
    }

    /// <summary>
    /// 把设置镜像进文件;内容没变就不写(启动时每次都会调一遍,绝大多数时候是空操作)。
    /// 写失败只意味着下次启动画面沿用旧外观,不值得打断保存,因此永不抛异常。
    /// </summary>
    /// <param name="settings">应用设置。</param>
    /// <param name="path">镜像文件路径。</param>
    public static void Mirror(AppSettings settings, string path)
    {
        try
        {
            string content = From(settings).Serialize();
            if (File.Exists(path) && File.ReadAllText(path) == content)
            {
                return;
            }
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(path, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 尽力而为。
        }
    }

    /// <summary>值里不许有换行(否则会把下一行顶掉);来自设置的值本来也不该有。</summary>
    private static string OneLine(string? value) =>
        (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
}
