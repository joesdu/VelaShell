using System.Text;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VelaShell.Plugin.Ai.Chat.Extensions;
using VelaShell.Plugin.Ai.Chat.Skills;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 「配置工具」的「扩展包」页(ai-plan §12.4):安装(文件夹或 .zip)、启停、卸载、MCP 启动信息的确认与重新确认。
/// </summary>
/// <remarks>
/// <para>
/// <b>确认的是什么</b>:扩展包带来的 MCP 服务器会在用户本机启动进程或连远端地址,命令由扩展包作者写。
/// 安装前把每台服务器的完整启动命令、参数、工作目录、环境变量、地址与请求头原样列出来,用户点「安装并启用」才装;
/// 记下的是 <see cref="ExtensionManifest.McpFingerprint" />。之后这部分变了(升级、或安装目录里的文件被改),
/// 这个扩展包的 MCP 服务器不启动,行上提示「需要重新确认」,点了再走一遍同样的确认。技能与命令只是给模型的文字,不在此列。
/// </para>
/// <para>
/// 确认框默认焦点在「取消」,Esc 取消;安装失败(清单不合格、压缩包越界等)原因写进状态行,不装任何东西。
/// </para>
/// </remarks>
public sealed partial class ToolPickerView
{
    private readonly ConfirmOverlay _confirm = new() { Name = "ExtensionConfirm", ScrimCornerRadius = new Avalonia.CornerRadius(0, 0, 7, 7) };
    private readonly Func<Task>? _mcpChanged;

    private ExtensionPackStore Extensions => field ??= new ExtensionPackStore(_context.DataDirectory);

    private void AddExtensionActions()
    {
        Button fromFolder = HeaderButton("ExtensionInstallFolder", _loc["ExtensionInstallFolder"]);
        fromFolder.Click += async (_, _) => await PickAndInstallAsync(folder: true);
        Button fromZip = HeaderButton("ExtensionInstallZip", _loc["ExtensionInstallZip"]);
        fromZip.Click += async (_, _) => await PickAndInstallAsync(folder: false);
        _pageActions.Children.Add(fromFolder);
        _pageActions.Children.Add(fromZip);
    }

    /// <summary>「扩展包」页:每个已安装的扩展包一行(说明 + 版本与内容摘要,行尾卸载与开关);MCP 需要重新确认时下面多一行提示与按钮。</summary>
    private void BuildExtensionsPage()
    {
        IReadOnlyList<InstalledPack> installed = Extensions.List();
        if (installed.Count == 0)
        {
            _content.Children.Add(new TextBlock
            {
                Name = "ExtensionsEmpty",
                Classes = { "dim" },
                Text = _loc["ExtensionsEmpty"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(6, 4)
            });
            return;
        }
        var rows = new List<Control>();
        foreach (InstalledPack pack in installed)
        {
            if (!Matches(pack.Id, pack.Manifest?.Name, pack.Manifest?.Description)) continue;
            rows.Add(BuildPackRow(pack));
        }
        if (rows.Count == 0) return;
        AddSection(_loc["ExtensionsInstalled"], rows);
    }

    private ExtensionPackState? StateOf(string id) => _settings.ExtensionPacks.FirstOrDefault(s => s.Id == id);

    private Border BuildPackRow(InstalledPack pack)
    {
        Button uninstall = HeaderButton("ExtensionUninstall", _loc["ExtensionUninstall"]);
        uninstall.Click += async (_, _) => await UninstallAsync(pack.Id, pack.Manifest?.Name ?? pack.Id);

        if (pack.Manifest is not { } manifest)
        {
            // 清单读不出来:说明原因,只能卸载
            Border broken = SourceRow("pack:" + pack.Id, pack.Id, null, null, uninstall, null, null);
            ((StackPanel)broken.Child!).Children.Add(new Border
            {
                Padding = new Avalonia.Thickness(24, 0, 8, 6),
                Child = WarningText("ExtensionError", _loc.F("ExtensionInvalid", pack.Id, pack.Error ?? ""))
            });
            return broken;
        }

        ExtensionPackState? state = StateOf(pack.Id);
        string summary = _loc.F("ExtensionSummary", manifest.Version,
            ExtensionRuntime.SkillDirectories([new ActivePack(manifest, pack.Directory, true)])
                .Sum(dir => SkillCatalog.ScanDirectory(dir, SkillSource.Extension).Count(s => s.IsValid)),
            manifest.Commands.Count, manifest.McpServers.Count);
        string subtitle = manifest.Description.Length > 0 ? $"{manifest.Description} · {summary}" : summary;
        Border row = SourceRow("pack:" + pack.Id, manifest.Name, subtitle, null, uninstall, state is { Enabled: true }, isEnabled =>
        {
            ExtensionPackState current = StateOf(pack.Id) ?? AddState(pack.Id);
            current.Enabled = isEnabled;
            AfterExtensionChange();
        });
        ToolTip.SetTip(row, pack.Directory);

        // MCP 启动信息与用户确认过的不一致:这个包的 MCP 不启动,给出重新确认的入口
        if (manifest.HasMcp && state?.ApprovedMcpFingerprint != manifest.McpFingerprint())
        {
            Button reapprove = HeaderButton("ExtensionReapprove", _loc["ExtensionReapprove"]);
            reapprove.Click += async (_, _) => await ReapproveAsync(pack.Id);
            var line = new Grid { ColumnDefinitions = [with("*,Auto")], Margin = new Avalonia.Thickness(24, 0, 8, 6) };
            TextBlock warning = WarningText("ExtensionNeedsApproval", _loc["ExtensionMcpNeedsApproval"]);
            warning.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(warning);
            Grid.SetColumn(reapprove, 1);
            line.Children.Add(reapprove);
            ((StackPanel)row.Child!).Children.Add(line);
        }
        return row;
    }

    private ExtensionPackState AddState(string id)
    {
        var state = new ExtensionPackState { Id = id };
        _settings.ExtensionPacks.Add(state);
        return state;
    }

    private void AfterExtensionChange()
    {
        AfterChange(); // 保存设置并重建
        if (_mcpChanged is not null) _ = _mcpChanged();
    }

    private async Task PickAndInstallAsync(bool folder)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        string? path;
        if (folder)
        {
            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = _loc["ExtensionInstallFolder"],
                AllowMultiple = false
            });
            path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        else
        {
            IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = _loc["ExtensionInstallZip"],
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }]
            });
            path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        if (path is not null) await InstallFromAsync(path);
    }

    /// <summary>从文件夹或 .zip 安装:校验清单 → 列出将带来的东西请用户确认 → 安装并启用。返回是否装上了。</summary>
    internal async Task<bool> InstallFromAsync(string source)
    {
        StagedPack staged;
        try
        {
            staged = ExtensionPackStore.Stage(source);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _status.Text = _loc.F("ExtensionInstallFailed", ex.Message);
            return false;
        }
        using (staged)
        {
            ExtensionManifest manifest = staged.Manifest;
            bool upgrade = Extensions.List().Any(p => p.Id == manifest.Id);
            string title = _loc.F(upgrade ? "ExtensionUpgradeTitle" : "ExtensionInstallTitle", manifest.Name, manifest.Version);
            if (!await _confirm.AskAsync(title, DescribeForConsent(manifest, staged.Directory), _loc["ExtensionInstallConfirm"], _loc["Cancel"]))
            {
                return false;
            }
            try
            {
                Extensions.Install(staged);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _status.Text = _loc.F("ExtensionInstallFailed", ex.Message);
                return false;
            }
            ExtensionPackState state = StateOf(manifest.Id) ?? AddState(manifest.Id);
            state.Enabled = true;
            state.ApprovedMcpFingerprint = manifest.HasMcp ? manifest.McpFingerprint() : null;
            AfterExtensionChange();
            _status.Text = _loc.F("ExtensionInstalled", manifest.Name, manifest.Version);
            return true;
        }
    }

    /// <summary>MCP 启动信息变了之后重新确认:同一份确认内容,点了才记下新的指纹。</summary>
    internal async Task<bool> ReapproveAsync(string id)
    {
        InstalledPack? pack = Extensions.List().FirstOrDefault(p => p.Id == id);
        if (pack?.Manifest is not { } manifest) return false;
        if (!await _confirm.AskAsync(_loc.F("ExtensionReapproveTitle", manifest.Name), DescribeForConsent(manifest, pack.Directory),
                _loc["ExtensionReapprove"], _loc["Cancel"]))
        {
            return false;
        }
        (StateOf(id) ?? AddState(id)).ApprovedMcpFingerprint = manifest.McpFingerprint();
        AfterExtensionChange();
        return true;
    }

    private async Task UninstallAsync(string id, string name)
    {
        if (!await _confirm.AskAsync(_loc.F("ExtensionUninstallTitle", name), _loc["ExtensionUninstallBody"], _loc["ExtensionUninstall"], _loc["Cancel"]))
        {
            return;
        }
        try
        {
            Extensions.Uninstall(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"{_loc["Error"]}: {ex.Message}";
            return;
        }
        _settings.ExtensionPacks.RemoveAll(s => s.Id == id);
        AfterExtensionChange();
    }

    /// <summary>确认框里的说明:它会带来哪些技能与命令,以及每台 MCP 服务器原样的启动信息。</summary>
    internal string DescribeForConsent(ExtensionManifest manifest, string packDirectory)
    {
        var text = new StringBuilder();
        if (manifest.Description.Length > 0) text.Append(manifest.Description).Append("\n\n");
        List<string> skills = [.. ExtensionRuntime.SkillDirectories([new ActivePack(manifest, packDirectory, true)])
            .SelectMany(dir => SkillCatalog.ScanDirectory(dir, SkillSource.Extension)).Where(s => s.IsValid).Select(s => s.Name)];
        text.Append(_loc.F("ExtensionConsentSkills", skills.Count > 0 ? string.Join(", ", skills) : "—")).Append('\n');
        text.Append(_loc.F("ExtensionConsentCommands",
            manifest.Commands.Count > 0 ? string.Join(", ", manifest.Commands.Select(c => "/" + c.Name)) : "—")).Append('\n');
        if (!manifest.HasMcp)
        {
            text.Append('\n').Append(_loc["ExtensionConsentNoMcp"]);
            return text.ToString();
        }
        text.Append('\n').Append(_loc["ExtensionConsentMcp"]).Append('\n');
        foreach (McpServerConfig server in manifest.ToMcpServers(packDirectory))
        {
            text.Append("\n• ").Append(server.Name);
            if (server.Transport == McpTransportType.Stdio)
            {
                text.Append(" (stdio)\n  ").Append(server.Command);
                if (server.Arguments.Length > 0) text.Append(' ').Append(server.Arguments);
                text.Append("\n  ").Append(_loc.F("ExtensionConsentWorkdir", server.WorkingDirectory));
                foreach (string line in server.EnvironmentVariables.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    text.Append("\n  ").Append(_loc.F("ExtensionConsentEnv", line));
            }
            else
            {
                text.Append(" (http)\n  ").Append(server.Url);
                foreach (string line in server.Headers.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    text.Append("\n  ").Append(_loc.F("ExtensionConsentHeader", line));
            }
        }
        return text.ToString();
    }
}
