using System.Text.Json;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

internal sealed partial class RemoteFsCapability
{
    // 与 AI 插件 mcp-server 配置的持久化字段一致;入口提示本身不授予权限。
    private sealed record McpTransferPolicy
    {
        public bool Enabled { get; init; } = true;
        public bool AutoApproveUploads { get; init; }
        public bool AutoApproveDownloads { get; init; }
    }

    private async Task<bool> AutoApproveTransferAsync(RemoteTransferApprovalContext context,
        RemoteTransferDirection direction, CancellationToken cancellationToken)
    {
        if (pluginId != "velashell.ai" || context != RemoteTransferApprovalContext.ExternalMcp || storage is null)
            return false;

        JsonElement raw = await storage.GetAsync<JsonElement>("mcp-server", cancellationToken).ConfigureAwait(false);
        if (raw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return false;
        McpTransferPolicy? policy = raw.Deserialize<McpTransferPolicy>();
        return policy is { Enabled: true } && direction switch
        {
            RemoteTransferDirection.Upload => policy.AutoApproveUploads,
            RemoteTransferDirection.Download => policy.AutoApproveDownloads,
            _ => false
        };
    }
}
