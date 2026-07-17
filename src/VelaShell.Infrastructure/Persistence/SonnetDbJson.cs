using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>SonnetDB 文档统一的 JSON 序列化约定(camelCase,兼容旧 JSON 存储时代写入的存量数据)。</summary>
internal static class SonnetDbJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
