using Avalonia.Controls;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    private static Button ReviewAction(Control root, string name) => root.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    [TestMethod]
    public void DiffReview_OpeningAnAgentChangeAllowsImmediateKeepOrDiscard()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window? window, ChatPanelView? panel, string? root) = await ShowWithWorkspaceAsync(context);
            try
            {
                var change = new FileChange("patch_local_file", "local:w", "app.txt", FileChangeKind.Modified, "before\r\n", "after\r\n");
                string path = Path.Combine(root, change.Path);
                await File.WriteAllTextAsync(path, change.After);
                FileDiffDocumentView view = await OpenChangedAsync(context, panel, change);
                Assert.IsFalse(view.IsDirty);
                Assert.IsTrue(ReviewAction(view, "DiffSave").IsEnabled);
                Assert.IsTrue(ReviewAction(view, "DiffRevert").IsEnabled);
                RaiseClick(ReviewAction(view, "DiffInlineToggle"));
                RaiseClick(ReviewAction(view, "DiffSave"));
                Assert.IsEmpty(panel.ActiveChangedFiles.Entries);
                Assert.AreEqual(change.After, await File.ReadAllTextAsync(path));

                view = await OpenChangedAsync(context, panel, change);
                await view.RevertOrDiscardAsync();
                Assert.IsEmpty(panel.ActiveChangedFiles.Entries);
                Assert.AreEqual(change.Before, await File.ReadAllTextAsync(path));
                Assert.AreEqual(change.Before, view.Document.After);
                Assert.IsFalse(ReviewAction(view, "DiffRevert").IsEnabled);
            }
            finally { panel.Detach(); window.Close(); Directory.Delete(root, recursive: true); }
        });
    }

    [TestMethod]
    public void DiffReview_BulkDiscardRestoresOnlyConfirmedUnchangedFilesAndProtectsDrafts()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window? window, ChatPanelView? panel, string? root) = await ShowWithWorkspaceAsync(context);
            try
            {
                Border bar = Find<Border>(panel, "ChangedFilesBar");
                foreach (string name in new[] { "external", "ok", "draft" })
                {
                    await File.WriteAllTextAsync(Path.Combine(root, name), name == "external" ? "external edit" : "after");
                    FileDiffDocumentView view = await OpenChangedAsync(context, panel, new FileChange("patch_local_file", "local:w", name, FileChangeKind.Modified, "before", "after"));
                    if (name == "draft") view.RightEditor!.Text = "unsaved";
                }
                RaiseClick(ReviewAction(bar, "ChangedFilesDiscardAll"));
                RaiseClick(ReviewAction(bar, "ChangedFilesCancelDiscard"));
                Assert.HasCount(3, panel.ActiveChangedFiles.Entries);
                RaiseClick(ReviewAction(bar, "ChangedFilesDiscardAll"));
                panel.OnToolFileChanged(new FileChange("write_local_file", "local:w", "later", FileChangeKind.Created, null, "new"));
                await PumpAsync(3);
                RaiseClick(ReviewAction(bar, "ChangedFilesConfirmDiscard"));
                for (int i = 0; i < 60 && panel.ActiveChangedFiles.Find("local:w", "external")!.ReviewError is null; i++) await PumpAsync(1);
                Assert.AreEqual("before", await File.ReadAllTextAsync(Path.Combine(root, "ok")));
                Assert.AreEqual("external edit", await File.ReadAllTextAsync(Path.Combine(root, "external")));
                Assert.AreEqual("after", await File.ReadAllTextAsync(Path.Combine(root, "draft")));
                Assert.IsNotNull(panel.ActiveChangedFiles.Find("local:w", "draft")!.ReviewError);
                Assert.IsNotNull(panel.ActiveChangedFiles.Find("local:w", "external")!.ReviewError);
                Assert.IsNotNull(panel.ActiveChangedFiles.Find("local:w", "later"));
                RaiseClick(ReviewAction(bar, "ChangedFilesKeepAll"));
                Assert.AreEqual("draft", panel.ActiveChangedFiles.Entries.Single().Path);
            }
            finally { panel.Detach(); window.Close(); Directory.Delete(root, recursive: true); }
        });
    }
}
