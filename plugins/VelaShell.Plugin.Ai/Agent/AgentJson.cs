using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace VelaShell.Plugin.Ai.Agent;

/// <summary>
/// 交给模型的工具结果与后台通知统一用的 JSON 序列化。
/// </summary>
/// <remarks>
/// <para>
/// 默认编码器把一切非 ASCII 字符写成 <c>\uXXXX</c>:一个汉字 6 个字符,中文输出的 token 成倍上涨,
/// 模型读回来的也是一串转义码。这里放行全部 Unicode 区段,中文、日文、表情原样写出。
/// </para>
/// <para>
/// <b>HTML 敏感字符照旧转义</b>(<c>&lt; &gt; &amp; ' +</c>):<see cref="JavaScriptEncoder.Create(UnicodeRange[])" />
/// 无论放行哪些区段都会转义它们。后台完成通知靠这一点把远端输出框成数据 ——
/// 输出里伪造不出 <c>&lt;/untrusted-command-output&gt;</c>(见 <see cref="Chat.AgentRunCoordinator" />)。
/// 所以这里不能换成 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping" />。
/// </para>
/// </remarks>
internal static class AgentJson
{
    /// <summary>放行全部 Unicode、仍转义 HTML 敏感字符的选项。</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>按 <see cref="Options" /> 序列化。</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
