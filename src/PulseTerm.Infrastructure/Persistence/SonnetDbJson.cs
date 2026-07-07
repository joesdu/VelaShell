using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulseTerm.Infrastructure.Persistence;

/// <summary>SonnetDB 文档统一的 JSON 序列化约定(camelCase,与既有 JsonDataStore 一致)。</summary>
internal static class SonnetDbJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
