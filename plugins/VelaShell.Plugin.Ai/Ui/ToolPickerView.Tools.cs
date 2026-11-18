using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Chat.Extensions;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>「配置工具」的「工具」页与「MCP 服务器」页。</summary>
public sealed partial class ToolPickerView
{
    // ───────────────────────── 工具 ─────────────────────────

    /// <summary>
    /// 「工具」页:内置工具按类别一行(类别取自 <see cref="ToolIcons" />,与工具卡片的图标同一套;
    /// 行尾开关一次启停这一类),MCP 每台服务器一行(行尾开关是服务器本身的启停,展开逐个工具)。
    /// </summary>
    private void BuildToolsPage()
    {
        HashSet<string> disabled = Lines(_settings.DisabledBuiltinTools);
        var builtin = new List<Control>();
        foreach (IGrouping<ToolKind, (string Name, string Description, bool ReadOnly)> category in AgentToolbox.Catalog.GroupBy(t => ToolIcons.KindOf(t.Name)))
        {
            string label = _loc[ToolIcons.LabelKey(category.Key)];
            bool categoryMatches = Matches(label);
            List<(string Name, string Description, bool ReadOnly)> tools = [.. category.Where(t => categoryMatches || Matches(t.Name, t.Description))];
            if (tools.Count == 0) continue;
            var items = tools.Select(tool => (Control)ItemRow(tool.Name, tool.Description, tool.ReadOnly, !disabled.Contains(tool.Name), on =>
            {
                Toggle(disabled, tool.Name, on);
                _settings.DisabledBuiltinTools = string.Join('\n', disabled);
                AfterChange();
            })).ToList();
            int all = category.Count();
            int enabled = category.Count(t => !disabled.Contains(t.Name));
            builtin.Add(SourceRow("builtin:" + category.Key, label, string.Join(", ", category.Select(t => t.Name)), $"{enabled}/{all}", null,
                Aggregate(enabled, all), on =>
                {
                    foreach ((string name, _, _) in category) Toggle(disabled, name, on);
                    _settings.DisabledBuiltinTools = string.Join('\n', disabled);
                    AfterChange();
                }, items));
        }

        var mcp = new List<Control>();
        foreach (McpServerConfig server in _settings.McpServers)
        {
            HashSet<string> off = Lines(server.DisabledTools);
            string name = string.IsNullOrWhiteSpace(server.Name) ? _loc["Unnamed"] : server.Name;
            bool serverMatches = Matches(name);
            List<McpToolInfo> tools = [.. server.KnownTools.Where(t => serverMatches || Matches(t.Name, t.Description))];
            if (Query.Length > 0 && !serverMatches && tools.Count == 0) continue;
            List<Control> items = server.KnownTools.Count == 0
                ? [new TextBlock { Classes = { "sourceSub" }, Text = _loc["ToolsNotLoaded"], TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Avalonia.Thickness(30, 4, 8, 6) }]
                : [.. tools.Select(tool => (Control)ItemRow(tool.Name, tool.Description, tool.ReadOnly, !off.Contains(tool.Name), on =>
                {
                    Toggle(off, tool.Name, on);
                    server.DisabledTools = string.Join('\n', off);
                    AfterChange();
                }))];
            var refresh = HeaderButton("McpRefresh", _loc["RefreshTools"]);
            refresh.Click += async (_, _) => await RefreshAsync(server, refresh);
            string? count = server.KnownTools.Count > 0 ? $"{server.KnownTools.Count(t => !off.Contains(t.Name))}/{server.KnownTools.Count}" : null;
            mcp.Add(SourceRow("mcp:" + server.Id, name, ServerDetail(server), count, refresh, server.Enabled, on => SetServerEnabled(server, on), items));
        }

        if (builtin.Count > 0)
        {
            _content.Children.Add(SectionLabel(_loc["ToolsBuiltin"], builtin.Count));
            foreach (Control row in builtin) _content.Children.Add(row);
        }
        if (mcp.Count > 0)
        {
            _content.Children.Add(SectionLabel("MCP", mcp.Count));
            foreach (Control row in mcp) _content.Children.Add(row);
        }
    }

    private void SetServerEnabled(McpServerConfig server, bool on)
    {
        server.Enabled = on;
        AfterChange();
        // 停用的服务器立即断开(不等闲置);启用的下一轮按需连接
        if (_mcpChanged is not null) _ = _mcpChanged();
    }

    private async Task RefreshAsync(McpServerConfig server, Button trigger)
    {
        trigger.IsEnabled = false;
        _status.Text = _loc["RefreshingTools"];
        try
        {
            IReadOnlyList<McpToolInfo> tools = await McpManager.RefreshToolsAsync(server, CancellationToken.None);
            server.KnownTools = [.. tools];
            server.ToolsRefreshedAt = DateTimeOffset.UtcNow;
            await _persist();
            Rebuild();
            _status.Text = _loc.F("ToolsRefreshed", server.Name, tools.Count);
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Refreshing MCP tools for '{server.Name}' failed: {ex.Message}");
            _status.Text = $"{_loc["Error"]}: {ex.Message}";
        }
        finally
        {
            trigger.IsEnabled = true;
        }
    }

    // ───────────────────────── MCP 服务器 ─────────────────────────

    private void AddServerActions()
    {
        var add = HeaderButton("McpAddButton", _loc["McpAdd"]);
        add.Click += (_, _) => _openServerEditor(null);
        _pageActions.Children.Add(add);
    }

    /// <summary>
    /// 「MCP 服务器」页:手工配置的每台一行(状态点在副标题里说;点行打开配置表单,行尾开关启停),
    /// 已启用扩展包带来的服务器另列一区(只读:在「扩展包」页管理)。
    /// </summary>
    private void BuildServersPage()
    {
        var mine = new List<Control>();
        foreach (McpServerConfig server in _settings.McpServers)
        {
            string name = string.IsNullOrWhiteSpace(server.Name) ? _loc["Unnamed"] : server.Name;
            if (!Matches(name, server.Command, server.Url)) continue;
            string id = server.Id;
            mine.Add(SourceRow("srv:" + id, name, ServerDetail(server), null, null, server.Enabled,
                on => SetServerEnabled(server, on), onClick: () => _openServerEditor(id)));
        }
        if (mine.Count > 0)
        {
            _content.Children.Add(SectionLabel(_loc["McpUserServers"], mine.Count));
            foreach (Control row in mine) _content.Children.Add(row);
        }
        else if (Query.Length == 0)
        {
            _content.Children.Add(new TextBlock { Name = "McpEmpty", Classes = { "dim" }, Text = _loc["McpNoServersPage"], TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(6, 4) });
        }

        var fromPacks = new List<Control>();
        foreach (ActivePack pack in ExtensionRuntime.Active(_settings, Extensions))
        {
            foreach (McpServerConfig server in pack.Manifest.ToMcpServers(pack.Directory))
            {
                if (!Matches(server.Name, pack.Manifest.Name)) continue;
                string transport = server.Transport == McpTransportType.Http ? "HTTP" : "Stdio";
                string state = pack.McpApproved ? _loc.F("McpFromPack", pack.Manifest.Name) : _loc["ExtensionMcpNeedsApproval"];
                fromPacks.Add(SourceRow("pack:" + server.Id, server.Name, $"{transport} · {state}", null, null, null, null));
            }
        }
        if (fromPacks.Count > 0)
        {
            _content.Children.Add(SectionLabel(_loc["McpPackServers"], fromPacks.Count));
            foreach (Control row in fromPacks) _content.Children.Add(row);
        }
    }

    /// <summary>服务器的副标题:传输方式 · 工具库状态 [· 已停用]。</summary>
    private string ServerDetail(McpServerConfig server)
    {
        string transport = server.Transport == McpTransportType.Http ? "HTTP" : "Stdio";
        string tools = server.KnownTools.Count > 0
            ? _loc.F("McpToolCount", server.KnownTools.Count)
            : _loc["McpToolsNotLoaded"];
        string detail = $"{transport} · {tools}";
        return server.Enabled ? detail : $"{detail} · {_loc["McpDisabledMark"]}";
    }
}
