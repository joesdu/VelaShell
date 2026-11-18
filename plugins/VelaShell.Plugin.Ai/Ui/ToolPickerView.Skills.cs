using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Chat.Skills;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 「配置工具」里的技能组(ai-plan §12.3 的 2c):逐个启停、来源、解析失败的原因、打开技能目录、重新扫描。
/// </summary>
/// <remarks>
/// 停用记在 <c>AiSettings.DisabledSkills</c>(与内置工具的停用清单同一种「每行一条」),随工具配置一起保存。
/// 列的是与模型看到的同一套来源:用户级目录 + 当前选中、允许读取的本地工作区;每次重建现扫,所以「重新扫描」就是重建。
/// 技能整体的开关仍是内置工具组里的 <c>load_skill</c>。
/// </remarks>
public sealed partial class ToolPickerView
{
    private Border BuildSkillsGroup()
    {
        HashSet<string> disabled = Lines(_settings.DisabledSkills);
        IReadOnlyList<SkillInfo> skills = SkillCatalog.Scan(SkillCatalog.UserDirectory,
            LocalPathScope.ActiveWorkspaces(_settings.LocalWorkspaces, _settings.LocalDefaultWorkspaceId)
                .Where(w => w.AllowRead).Select(w => w.RootPath));
        var rows = new StackPanel { Spacing = 8, Classes = { "toolRows" } };
        if (skills.Count == 0)
        {
            rows.Children.Add(new TextBlock
            {
                Classes = { "dim" },
                Text = _loc.F("SkillsEmpty", Path.Combine(SkillCatalog.UserDirectory, "<name>", SkillCatalog.SkillFileName)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(20, 2, 0, 2)
            });
        }
        foreach (SkillInfo skill in skills)
        {
            string source = skill.Source == SkillSource.Workspace ? _loc["SkillSourceWorkspace"] : _loc["SkillSourceUser"];
            if (!skill.IsValid)
            {
                // 解析失败的不能勾选:说清楚哪个文件、为什么,用户才知道去改哪儿
                var broken = new TextBlock
                {
                    Name = "SkillError",
                    Classes = { "hint" },
                    Text = _loc.F("SkillInvalid", skill.Name, source, skill.Error ?? ""),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(20, 2, 0, 2)
                };
                broken[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaWarning");
                ToolTip.SetTip(broken, skill.SkillFile);
                rows.Children.Add(broken);
                continue;
            }
            CheckBox row = BuildRow(skill.Name, $"{skill.Description} · {source}", readOnly: false, !disabled.Contains(skill.Name), enabled =>
            {
                Toggle(disabled, skill.Name, enabled);
                _settings.DisabledSkills = string.Join('\n', disabled);
                AfterChange();
            });
            ToolTip.SetTip(row, skill.SkillFile);
            rows.Children.Add(row);
        }

        var open = HeaderButton("SkillsOpenFolder", _loc["SkillsOpenFolder"]);
        open.Click += async (_, _) => await OpenSkillsFolderAsync();
        var rescan = HeaderButton("SkillsRescan", _loc["SkillsRescan"]);
        rescan.Click += (_, _) => Rebuild();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(open);
        buttons.Children.Add(rescan);

        List<SkillInfo> valid = [.. skills.Where(s => s.IsValid)];
        string subtitle = _loc.F("ToolsChecked", valid.Count(s => !disabled.Contains(s.Name)), valid.Count);
        return BuildGroup("skills", _loc["SkillsGroup"], subtitle, rows, buttons, defaultCollapsed: false);
    }

    private static Button HeaderButton(string name, string text)
    {
        var button = new Button
        {
            Name = name,
            Content = text,
            Height = 26,
            Padding = new Avalonia.Thickness(12, 0),
            [!FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11"),
            VerticalAlignment = VerticalAlignment.Center
        };
        button[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        return button;
    }

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
