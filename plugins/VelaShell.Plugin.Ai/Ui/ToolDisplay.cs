using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 工具卡片上给人看的参数与结果:头部一行摘要、展开后的可读正文。
/// </summary>
/// <remarks>
/// <para>
/// 工具参数与结果是 JSON 字符串。直接贴出来有三处难读:默认编码把 <c>&lt; ' +</c> 和中文写成
/// <c>\u</c> 开头的转义码;脚本里的换行是字面的 <c>\n</c>,整段挤成一行;引号、花括号又占去大半宽度。
/// 这里把 JSON 解开,按「键: 值」逐行排,多行字符串原样分行缩进 —— 一段 heredoc 脚本在卡片里读起来就是脚本本身。
/// </para>
/// <para>
/// 只管显示。交给模型、写进历史的仍是原 JSON(模型侧的转义规则见 <c>AgentJson</c>)。
/// 不是 JSON 的文本(终端输出、错误句子)原样返回。
/// </para>
/// </remarks>
public static class ToolDisplay
{
    /// <summary>摘要优先取的参数:哪个最能说明「这次调用冲着什么去」。</summary>
    private static readonly string[] SummaryKeys =
    [
        "command", "path", "remotePath", "localPath", "url", "query", "pattern", "prompt",
        "message", "subagentId", "taskId", "transferId", "sessionId", "id", "name"
    ];

    private static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 把调用参数序列化成给人看、也给历史存的 JSON:不转义中文与 <c>&lt; ' +</c>。
    /// 不交给模型 —— 模型拿到的是 MEAI 自己的那份。
    /// </summary>
    /// <param name="arguments">调用参数。</param>
    public static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }
        try
        {
            return JsonSerializer.Serialize(arguments, Relaxed);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or JsonException)
        {
            return string.Join(", ", arguments.Keys);
        }
    }

    /// <summary>
    /// 头部一行的摘要:优先取命令、路径、URL 等主参数的值(已解转义、压成单行),
    /// 没有就把各个标量参数排成 <c>键=值</c>;不是 JSON 时退回原文单行。
    /// </summary>
    /// <param name="json">参数 JSON。</param>
    /// <param name="max">最大字符数,超出用省略号收尾。</param>
    public static string Summary(string? json, int max)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "";
        }
        if (!TryParse(json, out JsonDocument? document))
        {
            return Clip(Flatten(json), max);
        }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Clip(Flatten(Scalar(root) ?? root.GetRawText()), max);
            }
            foreach (string key in SummaryKeys)
            {
                if (root.TryGetProperty(key, out JsonElement value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } text
                    && !string.IsNullOrWhiteSpace(text))
                {
                    return Clip(Flatten(text), max);
                }
            }
            var parts = new List<string>();
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (Scalar(property.Value) is { } scalar)
                {
                    parts.Add($"{property.Name}={scalar}");
                }
            }
            return Clip(Flatten(string.Join(" · ", parts)), max);
        }
    }

    /// <summary>
    /// 展开详情的正文:JSON 按「键: 值」逐行排开,嵌套缩进两格,多行字符串原样分行;
    /// 不是 JSON 的文本原样返回。
    /// </summary>
    /// <param name="text">参数或结果。</param>
    public static string Readable(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        if (!TryParse(text, out JsonDocument? document))
        {
            return text;
        }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (Scalar(root) is { } scalar)
            {
                return scalar;
            }
            var sb = new StringBuilder();
            WriteBlock(sb, root, 0);
            return sb.ToString().TrimEnd();
        }
    }

    private static void WriteBlock(StringBuilder sb, JsonElement element, int indent)
    {
        string pad = new(' ', indent);
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (!element.EnumerateObject().Any())
            {
                sb.Append(pad).AppendLine("{}");
                return;
            }
            foreach (JsonProperty property in element.EnumerateObject())
            {
                sb.Append(pad).Append(property.Name).Append(':');
                WriteValue(sb, property.Value, indent);
            }
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() == 0)
            {
                sb.Append(pad).AppendLine("[]");
                return;
            }
            foreach (JsonElement item in element.EnumerateArray())
            {
                sb.Append(pad).Append('-');
                WriteValue(sb, item, indent);
            }
            return;
        }
        sb.Append(pad).AppendLine(Scalar(element));
    }

    /// <summary>写在「键:」或「-」之后:单行标量同一行,多行字符串与嵌套结构另起几行缩进两格。</summary>
    private static void WriteValue(StringBuilder sb, JsonElement value, int indent)
    {
        string? scalar = Scalar(value);
        if (scalar is null)
        {
            bool empty = value.ValueKind == JsonValueKind.Object
                ? !value.EnumerateObject().Any()
                : value.GetArrayLength() == 0;
            if (empty)
            {
                sb.Append(' ').AppendLine(value.ValueKind == JsonValueKind.Object ? "{}" : "[]");
                return;
            }
            if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(i => Scalar(i) is { } s && !s.Contains('\n')))
            {
                string inline = "[" + string.Join(", ", value.EnumerateArray().Select(i => Scalar(i))) + "]";
                if (inline.Length <= 100)
                {
                    sb.Append(' ').AppendLine(inline);
                    return;
                }
            }
            sb.AppendLine();
            WriteBlock(sb, value, indent + 2);
            return;
        }
        if (!scalar.Contains('\n'))
        {
            sb.Append(' ').AppendLine(scalar);
            return;
        }
        sb.AppendLine();
        string pad = new(' ', indent + 2);
        foreach (string line in scalar.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'))
        {
            sb.Append(pad).AppendLine(line);
        }
    }

    /// <summary>标量的显示文本(字符串不带引号、已解转义);对象与数组返回 null。</summary>
    private static string? Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() is { Length: > 0 } text ? text : "\"\"",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => null
    };

    private static bool TryParse(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsonDocument? document)
    {
        document = null;
        string trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '[' or '"'))
        {
            return false;
        }
        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Flatten(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
