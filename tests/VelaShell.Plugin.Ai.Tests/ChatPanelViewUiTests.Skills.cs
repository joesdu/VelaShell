using Avalonia.Controls;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>「配置工具」的「技能」页(ai-plan §12.3 的 2c):逐个启停并保存、按来源分区、解析失败给原因、重新扫描。</summary>
public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public void ToolsWindow_SkillsGroup_TogglesPersistsShowsErrorsAndRescans()
    {
        OnUi(async () =>
        {
            string workspace = Path.Combine(Path.GetTempPath(), "picker-skills-" + Guid.NewGuid().ToString("N"));
            string skills = Path.Combine(workspace, ".velashell", "skills");
            Directory.CreateDirectory(Path.Combine(skills, "deploy-check"));
            await File.WriteAllTextAsync(Path.Combine(skills, "deploy-check", "SKILL.md"), "---\nname: deploy-check\ndescription: Verify a deployment.\n---\nSteps.");
            Directory.CreateDirectory(Path.Combine(skills, "broken"));
            await File.WriteAllTextAsync(Path.Combine(skills, "broken", "SKILL.md"), "---\nname: broken\n---\nno description");
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            var local = new LocalWorkspace { Name = "repo", RootPath = workspace };
            await store.SaveAsync(new AiSettings { LocalWorkspaces = [local], LocalDefaultWorkspaceId = local.Id });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                Click(panel, "ToolsButton");
                await PumpAsync(5);
                var picker = (ToolPickerView)context.FakeUi.LastPanel.CreateContent();
                host = Host(picker);
                picker.ShowPage(ToolPickerView.Page.Skills);
                await PumpAsync(2);
                Assert.AreEqual("1", NavCount(picker, ToolPickerView.Page.Skills), "左栏只数能用的技能");

                Border skill = Source(picker, "skill:deploy-check");
                Assert.IsTrue(SwitchOf(skill).IsChecked, "默认启用");
                Assert.Contains(t => t.Text == "Verify a deployment.", skill.GetVisualDescendants().OfType<TextBlock>(), "副标题是说明");
                Assert.Contains(t => t.Text == "from workspace", picker.GetVisualDescendants().OfType<TextBlock>(), "按来源分区");
                TextBlock error = picker.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SkillError");
                StringAssert.Contains(error.Text, "broken");
                StringAssert.Contains(error.Text, "description", "解析失败说清原因");

                SwitchOf(skill).IsChecked = false;
                Assert.IsTrue(await WaitForAsync(() => PanelSettings(panel).DisabledSkills == "deploy-check"));
                await PumpAsync(5);
                Assert.AreEqual("deploy-check", (await new AiSettingsStore(context).LoadAsync()).DisabledSkills, "停用随工具配置保存");

                // 新放进一个技能,点「重新扫描」就出现
                Directory.CreateDirectory(Path.Combine(skills, "audit"));
                await File.WriteAllTextAsync(Path.Combine(skills, "audit", "SKILL.md"), "---\nname: audit\ndescription: Audit.\n---");
                picker.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SkillsRescan")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync(3);
                Assert.IsTrue(SwitchOf(Source(picker, "skill:audit")).IsChecked);
                Assert.IsFalse(SwitchOf(Source(picker, "skill:deploy-check")).IsChecked, "停用的那个仍是停用");
                Assert.AreEqual("2", NavCount(picker, ToolPickerView.Page.Skills));
            }
            finally
            {
                host?.Close();
                panel.Detach();
                window.Close();
                try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
            }
        });
    }
}
