using Avalonia.Controls;
using Avalonia.Platform.Storage;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Chat.Extensions;
using VelaShell.Plugin.Ai.Chat.Skills;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 「配置工具」的「技能」页(ai-plan §12.3 的 2c):逐个启停、来源、解析失败的原因;页头「打开目录」「重新扫描」。
/// </summary>
/// <remarks>
/// 停用记在 <c>AiSettings.DisabledSkills</c>,随工具配置一起保存。列的是与模型看到的同一套来源:
/// 用户级目录、当前选中且允许读取的工作区、已启用的扩展包;每次重建现扫,所以「重新扫描」就是重建。
/// 技能整体的开关仍是「工具」页里的 <c>load_skill</c>。
/// </remarks>
public sealed partial class ToolPickerView
{
    /// <summary>与模型看到的同一套来源现扫(含解析失败的)。</summary>
    private IReadOnlyList<SkillInfo> ScanSkills()
        => SkillCatalog.Scan(SkillCatalog.UserDirectory,
            LocalPathScope.ActiveWorkspaces(_settings.LocalWorkspaces, _settings.LocalDefaultWorkspaceId)
                .Where(w => w.AllowRead).Select(w => w.RootPath),
            ExtensionRuntime.SkillDirectories(ExtensionRuntime.Active(_settings, Extensions)));

    private void AddSkillActions()
    {
        var open = HeaderButton("SkillsOpenFolder", _loc["SkillsOpenFolder"]);
        open.Click += async (_, _) => await OpenSkillsFolderAsync();
        var rescan = HeaderButton("SkillsRescan", _loc["SkillsRescan"]);
        rescan.Click += (_, _) => Rebuild();
        _pageActions.Children.Add(open);
        _pageActions.Children.Add(rescan);
    }

    private void BuildSkillsPage()
    {
        HashSet<string> disabled = Lines(_settings.DisabledSkills);
        IReadOnlyList<SkillInfo> skills = ScanSkills();
        if (skills.Count == 0)
        {
            _content.Children.Add(new TextBlock
            {
                Name = "SkillsEmpty",
                Classes = { "dim" },
                Text = _loc.F("SkillsEmpty", Path.Combine(SkillCatalog.UserDirectory, "<name>", SkillCatalog.SkillFileName)),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(6, 4)
            });
            return;
        }
        foreach (IGrouping<SkillSource, SkillInfo> group in skills.GroupBy(s => s.Source).OrderBy(g => g.Key))
        {
            string section = SourceLabel(group.Key);
            var rows = new List<Control>();
            foreach (SkillInfo skill in group)
            {
                if (!Matches(skill.Name, skill.Description)) continue;
                if (!skill.IsValid)
                {
                    // 解析失败的不能启用:说清楚哪个文件、为什么,用户才知道去改哪儿
                    var broken = new StackPanel { Margin = new Avalonia.Thickness(24, 4, 8, 4) };
                    broken.Children.Add(new TextBlock { Classes = { "sourceName" }, Text = skill.Name });
                    broken.Children.Add(WarningText("SkillError", _loc.F("SkillInvalid", skill.Name, section, skill.Error ?? "")));
                    ToolTip.SetTip(broken, skill.SkillFile);
                    rows.Add(broken);
                    continue;
                }
                Border row = SourceRow("skill:" + skill.Name, skill.Name, skill.Description, null, null, !disabled.Contains(skill.Name), on =>
                {
                    Toggle(disabled, skill.Name, on);
                    _settings.DisabledSkills = string.Join('\n', disabled);
                    AfterChange();
                });
                ToolTip.SetTip(row, skill.SkillFile);
                rows.Add(row);
            }
            if (rows.Count == 0) continue;
            AddSection(section, rows);
        }
    }

    private string SourceLabel(SkillSource source) => source switch
    {
        SkillSource.Workspace => _loc["SkillSourceWorkspace"],
        SkillSource.Extension => _loc["SkillSourceExtension"],
        _ => _loc["SkillSourceUser"]
    };

    /// <summary>打开用户级技能目录(还没有就先建出来),在系统文件管理器里放技能文件夹。</summary>
    private async Task OpenSkillsFolderAsync()
    {
        string folder = SkillCatalog.UserDirectory;
        try
        {
            Directory.CreateDirectory(folder);
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher && await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)))
            {
                return;
            }
            // 拿不到 Launcher 或系统没打开:把路径显示出来让用户自己去,别静默失败
            _status.Text = _loc.F("SkillsFolderAt", folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.Log.Warn($"Opening the skills folder '{folder}' failed: {ex.Message}");
            _status.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }
}
