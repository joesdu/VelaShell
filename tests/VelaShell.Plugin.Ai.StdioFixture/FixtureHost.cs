using System.Text.Json;

namespace VelaShell.Plugin.Ai.StdioFixture;

public static class FixtureHost
{
    public static async Task Main(string[] args)
    {
        await File.WriteAllTextAsync(args[0], Environment.ProcessId.ToString());
        if (args.Contains("--hang")) await Task.Delay(Timeout.InfiniteTimeSpan);
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var request = JsonDocument.Parse(line);
            if (!request.RootElement.TryGetProperty("id", out JsonElement id)) continue;
            string? method = request.RootElement.GetProperty("method").GetString();
            object? result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = "2025-11-25",
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "stdio-test", version = "1.0" }
                },
                "tools/list" => new
                {
                    tools = new[] { new
                    {
                        name = "inspect",
                        description = "Return this test process identity and selected environment variable.",
                        inputSchema = new { type = "object", properties = new { } },
                        annotations = new { readOnlyHint = true }
                    } }
                },
                "tools/call" => new
                {
                    content = new[] { new
                    {
                        type = "text",
                        text = JsonSerializer.Serialize(new
                        {
                            pid = Environment.ProcessId,
                            directory = Environment.CurrentDirectory,
                            value = Environment.GetEnvironmentVariable(args[1])
                        })
                    } },
                    isError = false
                },
                "ping" => new { },
                _ => null
            };
            object response = result is null
                ? new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not found" } }
                : new { jsonrpc = "2.0", id, result };
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response));
            await Console.Out.FlushAsync();
        }
    }
}
