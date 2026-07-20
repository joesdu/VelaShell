namespace VelaShell.Core.Localization;

/// <summary>
/// 运行时访问本地化字符串的服务
/// </summary>
public interface ILocalizationService
{
    /// <summary>
    /// 获取当前 UI 语言代码(如 "en"、"zh-CN")
    /// </summary>
    string CurrentLanguage { get; }

    /// <summary>
    /// 按键获取本地化字符串
    /// </summary>
    /// <param name="key">资源键</param>
    /// <returns>本地化字符串;未找到时返回键名</returns>
    string GetString(string key);

    /// <summary>
    /// 设置应用的 UI 语言
    /// </summary>
    /// <param name="language">语言代码(如 "en"、"zh-CN")</param>
    void SetLanguage(string language);

    /// <summary>UI 语言变更后触发,使实时绑定的文本可以刷新。</summary>
    event Action<string>? LanguageChanged;
}
