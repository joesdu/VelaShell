using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Chat.Extensions;

/// <summary>一个已安装且启用的扩展包。</summary>
/// <param name="Manifest">清单。</param>
/// <param name="Directory">安装目录。</param>
/// <param name="McpApproved">用户确认过的 MCP 指纹与清单当前的一致(不带 MCP 时为 true)。</param>
internal sealed record ActivePack(ExtensionManifest Manifest, string Directory, bool McpApproved);

/// <summary>
/// 扩展包在一轮里的样子(ai-plan §12.4):哪些包生效、带来哪些技能目录、斜杠命令与 MCP 服务器。
/// 桌面、IM 与 MCP 服务器窗口共用,规则只写在这里。
/// </summary>
/// <remarks>
/// 生效 = 已安装、清单可读、设置里启用。MCP 服务器还要求用户确认过的指纹与清单当前的一致 ——
/// 不一致(升级没确认、或安装目录里的文件被改过)时只撤下 MCP,技能与命令照常可用,界面上提示重新确认。
/// </remarks>
internal static class ExtensionRuntime
{
    /// <summary>生效的扩展包(按 id 排序)。</summary>
    public static IReadOnlyList<ActivePack> Active(AiSettings settings, ExtensionPackStore store)
    {
        if (settings.ExtensionPacks.Count == 0) return [];
        var result = new List<ActivePack>();
        foreach (InstalledPack pack in store.List())
        {
            if (pack.Manifest is not { } manifest) continue;
            ExtensionPackState? state = settings.ExtensionPacks.FirstOrDefault(s => s.Id == pack.Id);
            if (state is not { Enabled: true }) continue;
            bool approved = !manifest.HasMcp || state.ApprovedMcpFingerprint == manifest.McpFingerprint();
            result.Add(new ActivePack(manifest, pack.Directory, approved));
        }
        return result;
    }

    /// <summary>生效扩展包的技能目录(交给 <see cref="Skills.SkillCatalog.Enabled" />)。</summary>
    public static IEnumerable<string> SkillDirectories(IReadOnlyList<ActivePack> packs)
        => packs.Select(p => p.Manifest.SkillsDirectory(p.Directory)).OfType<string>().Where(Directory.Exists);

    /// <summary>
    /// 一轮要用的全部 MCP 服务器:手工配置的 + 生效且已确认的扩展包带来的。
    /// 交给 <c>McpToolSource</c>,也交给 <c>McpManager.ReconcileAsync</c> —— 少传了扩展包的,它们的连接会被当成已删除关掉。
    /// </summary>
    public static List<McpServerConfig> McpServers(AiSettings settings, IReadOnlyList<ActivePack> packs)
        => [.. settings.McpServers, .. packs.Where(p => p.McpApproved).SelectMany(p => p.Manifest.ToMcpServers(p.Directory))];

    /// <summary>
    /// <c>/命令 参数</c> 匹配到生效扩展包的命令时,按模板展开成交给模型的正文;不是返回 null。
    /// 模板里的 <c>{{args}}</c> 换成参数,没有占位符时把参数接在后面。
    /// </summary>
    public static string? TryExpandCommand(string text, IReadOnlyList<ActivePack> packs)
    {
        string trimmed = text.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != '/') return null;
        int end = 1;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end])) end++;
        string name = trimmed[1..end];
        string args = trimmed[end..].Trim();
        foreach (ActivePack pack in packs)
        {
            ExtensionCommand? command = pack.Manifest.Commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (command is null) continue;
            string prompt = command.Prompt.Contains("{{args}}", StringComparison.Ordinal)
                ? command.Prompt.Replace("{{args}}", args, StringComparison.Ordinal)
                : args.Length > 0 ? command.Prompt.TrimEnd() + "\n\n" + args : command.Prompt.TrimEnd();
            return $"(The user ran the command /{command.Name} from the extension pack \"{pack.Manifest.Name}\".)\n\n{prompt}";
        }
        return null;
    }

    /// <summary>生效扩展包的全部命令(给 <c>/</c> 候选与 <c>/help</c> 用),同名只取第一个。</summary>
    public static IEnumerable<(string Name, string Description, string Pack)> Commands(IReadOnlyList<ActivePack> packs)
        => packs.SelectMany(p => p.Manifest.Commands.Select(c => (c.Name, c.Description, Pack: p.Manifest.Name)))
            .DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
}
