using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Chat.Extensions;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>「配置工具」的「扩展包」页(ai-plan §12.4):安装前确认 MCP 启动信息、启停、启动信息被改后重新确认、卸载。</summary>
public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public void ToolsWindow_ExtensionPacks_ConsentInstallReapproveDisableUninstall()
    {
        OnUi(async () =>
        {
            string source = Path.Combine(Path.GetTempPath(), "ext-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(source, "skills", "incident"));
            await File.WriteAllTextAsync(Path.Combine(source, "skills", "incident", "SKILL.md"), "---\nname: incident\ndescription: Handle an incident.\n---");
            object Manifest(string command) => new
            {
                id = "acme-ops", name = "Acme Ops", version = "1.0", description = "Ops helpers",
                commands = new[] { new { name = "deploy", prompt = "Deploy {{args}}." } },
                mcpServers = new[] { new { name = "tickets", command, args = new[] { "server.js", "--read only" }, env = new Dictionary<string, string> { ["MODE"] = "ro" } } }
            };
            await File.WriteAllTextAsync(Path.Combine(source, ExtensionManifest.FileName), JsonSerializer.Serialize(Manifest("node")));
            using var context = new TestPluginContext();
            var settings = new AiSettings();
            var store = new AiSettingsStore(context);
            int mcpChanges = 0;
            var picker = new ToolPickerView(context, settings, new Loc("en"), () => store.SaveAsync(settings), _ => { },
                () => { mcpChanges++; return Task.CompletedTask; });
            Window host = Host(picker);
            picker.ShowPage(ToolPickerView.Page.Extensions);
            try
            {
                ConfirmOverlay confirm = picker.GetVisualDescendants().OfType<ConfirmOverlay>().Single(o => o.Name == "ExtensionConfirm");
                Button Accept() => confirm.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmAcceptButton");
                Button Cancel() => confirm.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmCancelButton");
                static void Press(Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

                // 取消:什么都不装
                Task<bool> cancelled = picker.InstallFromAsync(source);
                await PumpAsync(3);
                Assert.IsTrue(confirm.IsVisible);
                Press(Cancel());
                Assert.IsFalse(await cancelled);
                Assert.IsEmpty(settings.ExtensionPacks);

                // 确认框里原样列出启动命令、参数、工作目录与环境变量
                Task<bool> install = picker.InstallFromAsync(source);
                await PumpAsync(3);
                string message = confirm.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ConfirmMessage").Text!;
                StringAssert.Contains(message, "Skills: incident");
                StringAssert.Contains(message, "Commands: /deploy");
                StringAssert.Contains(message, "node server.js \"--read only\"");
                StringAssert.Contains(message, "environment: MODE=ro");
                StringAssert.Contains(message, "working directory:");
                Press(Accept());
                Assert.IsTrue(await install);

                ExtensionPackState state = settings.ExtensionPacks.Single();
                Assert.IsTrue(state.Enabled);
                string installedDir = Path.Combine(context.DataDirectory, "extensions", "acme-ops");
                Assert.AreEqual(ExtensionManifest.Load(installedDir).McpFingerprint(), state.ApprovedMcpFingerprint, "记下确认过的指纹");
                Assert.AreEqual(1, mcpChanges);
                Assert.IsTrue(ExtensionRuntime.Active(settings, new ExtensionPackStore(context.DataDirectory)).Single().McpApproved);
                Assert.IsEmpty(picker.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "ExtensionNeedsApproval"));

                // 安装目录里的启动命令被改:MCP 暂停,提示重新确认
                await File.WriteAllTextAsync(Path.Combine(installedDir, ExtensionManifest.FileName), JsonSerializer.Serialize(Manifest("python")));
                picker.Rebuild();
                await PumpAsync(3);
                Assert.IsFalse(ExtensionRuntime.Active(settings, new ExtensionPackStore(context.DataDirectory)).Single().McpApproved);
                Assert.HasCount(1, picker.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "ExtensionNeedsApproval"));
                Task<bool> reapprove = picker.ReapproveAsync("acme-ops");
                await PumpAsync(3);
                StringAssert.Contains(confirm.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ConfirmMessage").Text, "python server.js");
                Press(Accept());
                Assert.IsTrue(await reapprove);
                Assert.IsTrue(ExtensionRuntime.Active(settings, new ExtensionPackStore(context.DataDirectory)).Single().McpApproved);
                await PumpAsync(3);
                Assert.IsEmpty(picker.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "ExtensionNeedsApproval"));

                // 停用:全部撤下,并通知面板对齐 MCP 连接
                int before = mcpChanges;
                SwitchOf(Source(picker, "pack:acme-ops")).IsChecked = false;
                await PumpAsync(3);
                Assert.IsFalse(settings.ExtensionPacks.Single().Enabled);
                Assert.IsEmpty(ExtensionRuntime.Active(settings, new ExtensionPackStore(context.DataDirectory)));
                Assert.AreEqual(before + 1, mcpChanges);
                Assert.IsFalse((await store.LoadAsync()).ExtensionPacks.Single().Enabled, "随设置保存");

                // 卸载:确认后删文件、去掉状态
                picker.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ExtensionUninstall")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync(3);
                Press(Accept());
                await PumpAsync(5);
                Assert.IsFalse(Directory.Exists(installedDir));
                Assert.IsEmpty(settings.ExtensionPacks);
            }
            finally
            {
                host.Close();
                try { Directory.Delete(source, recursive: true); } catch (IOException) { }
            }
        });
    }

    [TestMethod]
    public void ToolsWindow_ExtensionPacks_BadSourceIsReportedAndNothingInstalled()
    {
        OnUi(async () =>
        {
            string source = Path.Combine(Path.GetTempPath(), "ext-bad-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, ExtensionManifest.FileName), """{"id":"Bad Id","name":"x","version":"1"}""");
            using var context = new TestPluginContext();
            var settings = new AiSettings();
            var picker = new ToolPickerView(context, settings, new Loc("en"), () => Task.CompletedTask, _ => { });
            Window host = Host(picker);
            picker.ShowPage(ToolPickerView.Page.Extensions);
            try
            {
                Assert.IsFalse(await picker.InstallFromAsync(source));
                Assert.Contains(t => (t.Text ?? "").StartsWith("Could not install the extension pack:", StringComparison.Ordinal),
                    picker.GetVisualDescendants().OfType<TextBlock>(), "原因写进状态行");
                Assert.IsFalse(picker.GetVisualDescendants().OfType<ConfirmOverlay>().Single().IsVisible, "清单不合格不弹确认");
                Assert.IsEmpty(settings.ExtensionPacks);
            }
            finally
            {
                host.Close();
                try { Directory.Delete(source, recursive: true); } catch (IOException) { }
            }
        });
    }
}
