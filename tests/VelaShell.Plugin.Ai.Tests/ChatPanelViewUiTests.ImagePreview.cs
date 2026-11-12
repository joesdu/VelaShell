using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public void ImagePreview_OpensOriginalClosesOnEscapeAndDetachAndHandlesMissingFile()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                var bubble = new ChatPanelView.AssistantBubble(panel);
                Find<StackPanel>(panel, "MessagesPanel").Children.Add(bubble.Root);
                bubble.AddImage(new Microsoft.Extensions.AI.DataContent(Convert.FromBase64String(GeneratedPng), "image/png"));
                Button full = bubble.Root.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ImageFullScreenAction");
                RaiseClick(full);
                await PumpAsync(5);
                FakePanel opened = context.FakeUi.LastPanel;
                var view = (ImagePreviewView)opened.CreateContent();
                var original = (Image)view.Children.OfType<ScrollViewer>().Single().Content!;
                Assert.IsInstanceOfType<Bitmap>(original.Source);
                Assert.AreEqual(Avalonia.Media.Stretch.Uniform, original.Stretch);
                Assert.IsLessThan(1000, opened.Options.WindowWidth);
                Assert.IsLessThan(800, opened.Options.WindowHeight);
                host = Host(view);
                await PumpAsync(3);
                host.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = host });
                await PumpAsync(5);
                Assert.IsFalse(opened.IsOpen);
                Assert.IsNull(original.Source);
                host.Close();
                host = null;
                RaiseClick(full);
                await PumpAsync(5);
                opened = context.FakeUi.LastPanel;
                original = (Image)((ImagePreviewView)opened.CreateContent()).Children.OfType<ScrollViewer>().Single().Content!;
                panel.Detach();
                await PumpAsync(5);
                Assert.IsFalse(opened.IsOpen);
                Assert.IsNull(original.Source);

                int before = context.FakeUi.Panels.Count;
                MethodInfo preview = typeof(ChatPanelView.AssistantBubble).GetMethod("OpenImagePreview", BindingFlags.Instance | BindingFlags.NonPublic)!;
                preview.Invoke(bubble, [new ChatGeneratedImage(new string('a', 32) + ".png", "image/png")]);
                await PumpAsync(3);
                Assert.HasCount(before, context.FakeUi.Panels);
            }
            finally { host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GeneratedImage_CopyFromChatOrPreviewRetainsIndependentBitmapsAfterClosing(bool fromPreview)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                var bubble = new ChatPanelView.AssistantBubble(panel);
                Find<StackPanel>(panel, "MessagesPanel").Children.Add(bubble.Root);
                bubble.AddImage(new Microsoft.Extensions.AI.DataContent(Convert.FromBase64String(GeneratedPng), "image/png"));
                await PumpAsync(3);
                Control root = bubble.Root;
                string buttonName = "ImageCopyAction";
                if (fromPreview)
                {
                    RaiseClick(root.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ImageFullScreenAction"));
                    await PumpAsync(5);
                    root = (ImagePreviewView)context.FakeUi.LastPanel.CreateContent();
                    host = Host(root);
                    buttonName = "ImagePreviewCopyAction";
                }
                await window.Clipboard!.SetTextAsync("old clipboard");
                RaiseClick(root.GetVisualDescendants().OfType<Button>().Single(b => b.Name == buttonName));
                Assert.IsTrue(await WaitForAsync(() => Find<TextBlock>(panel, "StatusText").Text == new Loc("en")["Copied"]));
                panel.Detach();
                await PumpAsync(3);
                host?.Close();
                host = null;
                Bitmap? previous = null;
                for (int i = 0; i < 2; i++)
                {
                    using Bitmap? copied = await window.Clipboard.TryGetBitmapAsync();
                    Assert.IsNotNull(copied, "关闭视图后复制仍必须提供图片格式，而不是文件路径或文字");
                    Assert.AreNotSame(previous, copied, "每次读取应获取独立位图，不能复用已经释放的图片");
                    previous = copied;
                }
                Assert.IsNull(await window.Clipboard.TryGetTextAsync());
            }
            finally { await window.Clipboard!.ClearAsync(); host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void GeneratedImage_CopyMissingImagePreservesClipboardAndReportsError()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                await window.Clipboard!.SetTextAsync("keep clipboard");
                var bubble = new ChatPanelView.AssistantBubble(panel);
                MethodInfo copy = typeof(ChatPanelView.AssistantBubble).GetMethod("CopyImageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await (Task)copy.Invoke(bubble, [new ChatGeneratedImage(new string('a', 32) + ".png", "image/png"), panel])!;
                Assert.AreEqual("keep clipboard", await window.Clipboard.TryGetTextAsync());
                Assert.IsTrue(Find<TextBlock>(panel, "StatusText").Text?.StartsWith(new Loc("en")["Error"] + ":", StringComparison.Ordinal));
            }
            finally { await window.Clipboard!.ClearAsync(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void GeneratedImage_ActionsOverlayImageAndAreAccessible()
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((_, _) => (200,
                "{\"created\":1,\"data\":[{\"b64_json\":\"" + GeneratedPng + "\"}]}"));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("images", endpoint.BaseUrl + "/v1", "gpt-image-2");
            await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("draw a cat");
                Assert.IsTrue(await WaitForAsync(() => endpoint.Requests.Count > 0
                    && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Image image = Find<StackPanel>(panel, "MessagesPanel").GetVisualDescendants()
                    .OfType<Image>().Single(i => i.Source is not null);
                var overlay = (Grid)image.Parent!;
                StackPanel actions = overlay.Children.OfType<StackPanel>().Single();
                Button[] buttons = [.. actions.Children.OfType<Button>()];
                Assert.HasCount(3, buttons);
                Assert.AreEqual(Avalonia.Layout.HorizontalAlignment.Right, actions.HorizontalAlignment);
                Assert.AreEqual(Avalonia.Layout.VerticalAlignment.Bottom, actions.VerticalAlignment);
                foreach (Button button in buttons)
                {
                    Assert.IsTrue(button.Focusable);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                    Assert.IsNotNull(ToolTip.GetTip(button));
                    Assert.IsInstanceOfType<Viewbox>(button.Content, "操作只显示图标，不能再退回文字按钮");
                    Assert.AreEqual(34d, button.Bounds.Width);
                    Assert.AreEqual(34d, button.Bounds.Height);
                    Border surface = button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Surface");
                    Assert.AreEqual(new Avalonia.CornerRadius(17), surface.CornerRadius);
                    Assert.IsLessThan(1, surface.Opacity, "只有圆形底面半透明，不应出现不透明底板");
                    var icon = (Avalonia.Controls.Shapes.Path)((Viewbox)button.Content!).Child!;
                    Assert.IsNotNull(icon.Data);
                    string key = button.Name switch
                    {
                        "ImageCopyAction" => "AiIcon.copy",
                        "ImageSaveAction" => "AiIcon.image-down",
                        _ => "AiIcon.fullscreen"
                    };
                    Assert.IsTrue(panel.TryFindResource(key, out object? geometry));
                    Assert.AreSame(geometry, icon.Data);
                    Assert.AreEqual(1d, button.Opacity, "图标本身不跟着底面一起变透明");
                }
            }
            finally { panel.Detach(); window.Close(); }
        });
    }
}
