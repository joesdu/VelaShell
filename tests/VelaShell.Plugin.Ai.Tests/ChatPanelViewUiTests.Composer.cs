using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    [DataRow("ModeCombo")]
    [DataRow("ApprovalCombo")]
    [DataRow("WorkspaceCombo")]
    public void Composer_SelectorsFitSelectedContent_WithoutArrowSpace(string name)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                Find<ComboBox>(panel, "ModeCombo").SelectedIndex = (int)ChatMode.Agent;
                ComboBox picker = Find<ComboBox>(panel, name);
                if (name == "WorkspaceCombo")
                {
                    picker.ItemsSource = new[]
                    {
                        new WorkspaceNavItem(null, "App", false),
                        new WorkspaceNavItem(null, "Application workspace", false)
                    };
                }

                var widths = new List<double>();
                for (int index = 0; index < picker.ItemCount; index++)
                {
                    picker.SelectedIndex = index;
                    await PumpAsync(5);
                    TextBlock label = picker.GetVisualDescendants().OfType<TextBlock>()
                        .Single(text => text.IsEffectivelyVisible);
                    double textEnd = label.TranslatePoint(default, picker)!.Value.X + label.DesiredSize.Width;
                    Assert.IsLessThanOrEqualTo(8, picker.Bounds.Width - textEnd,
                        $"{name}: width={picker.Bounds.Width}, textEnd={textEnd}");
                    widths.Add(picker.Bounds.Width);
                }
                Assert.IsGreaterThan(1, widths.Max() - widths.Min(),
                    $"{name} must resize when the selected label changes");
                await ClickComposerControlAsync(window, picker);
                Assert.IsTrue(picker.IsDropDownOpen);
                var item = (ComboBoxItem)picker.ContainerFromIndex(0)!;
                await ClickComposerControlAsync(TopLevel.GetTopLevel(item)!, item);
                Assert.AreEqual(0, picker.SelectedIndex);
                Assert.IsFalse(picker.IsDropDownOpen);
                Assert.AreEqual(widths[0], picker.Bounds.Width, 1,
                    "Returning to the first item must restore its compact width");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    private static async Task ClickComposerControlAsync(TopLevel root, Control control)
    {
        // Supply hover/focus tokens omitted by the headless host so a transparent hit target remains.
        root.Resources["VelaBgHover"] = Avalonia.Media.Brushes.Transparent;
        root.Resources["VelaAccentDim"] = Avalonia.Media.Brushes.Transparent;
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point);
        root.MouseDown(point, MouseButton.Left);
        root.MouseUp(point, MouseButton.Left);
        await PumpAsync(10);
    }

    [TestMethod]
    public void Composer_PopupPointerSelection_PreservesProviderIdentityAndShowsCheckmark()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("First", "http://127.0.0.1:1/v1", "shared");
            AiProvider second = StubProvider("Second", "http://127.0.0.1:1/v1", "shared");
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [first, second],
                ActiveModelId = first.Models[0].Id,
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                Button button = Find<Button>(panel, "ModelButton");
                await ClickComposerControlAsync(window, button);
                Popup popup = Find<Popup>(panel, "ModelPopup");
                Assert.IsTrue(popup.IsOpen);
                ListBox list = Find<ListBox>(panel, "ProviderList");
                Assert.AreEqual("First·shared", list.Items[0]);
                Assert.AreEqual("Second·shared", list.Items[1]);
                var item = (ListBoxItem)list.ContainerFromIndex(1)!;
                TopLevel root = TopLevel.GetTopLevel(list)!;
                await ClickComposerControlAsync(root, item);
                Assert.AreEqual(1, list.SelectedIndex);
                Assert.Contains("Second·shared", Find<TextBlock>(panel, "ModelSummary").Text!);
                Assert.Contains("Second·shared", ToolTip.GetTip(button)!.ToString()!);
                Assert.Contains("Second·shared", AutomationProperties.GetName(button)!);
                Assert.IsTrue(item.GetVisualDescendants().OfType<Viewbox>().Single().IsVisible,
                    "当前模型必须有勾选标记");
                Assert.IsTrue(Find<ComboBox>(panel, "ReasoningCombo").IsEffectivelyVisible,
                    "选好模型后仍能在同一弹层调整思考档位");
                await ClickComposerControlAsync(window, Find<Control>(panel, "InputBox"));
                Assert.IsFalse(popup.IsOpen, "点编辑器须关闭模型弹层");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void Composer_PopupKeyboard_SelectsModelAndReasoning_EscapeRestoresFocus()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel, ComboBox reasoning) =
                await WithReasoningPickerAsync(context, TwoModels(ReasoningLevel.High, ReasoningLevel.Low));
            try
            {
                Button button = Find<Button>(panel, "ModelButton");
                button.Focus();
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                await PumpAsync(10);
                Popup popup = Find<Popup>(panel, "ModelPopup");
                Assert.IsTrue(popup.IsOpen);
                ListBox list = Find<ListBox>(panel, "ProviderList");
                Assert.IsTrue(list.IsKeyboardFocusWithin);
                TopLevel root = TopLevel.GetTopLevel(list)!;
                root.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                await PumpAsync(10);
                Assert.AreEqual(1, list.SelectedIndex);
                Assert.AreEqual((int)ReasoningLevel.Low, reasoning.SelectedIndex);
                root.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                await PumpAsync(10);
                Assert.IsTrue(reasoning.IsKeyboardFocusWithin, "Tab 从模型列表进入思考档位");
                root.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                await PumpAsync(10);
                Assert.AreEqual((int)ReasoningLevel.Medium, reasoning.SelectedIndex);
                Assert.Contains("Medium", Find<TextBlock>(panel, "ReasoningSummary").Text!);
                Assert.Contains("overridden", button.Classes);
                root.KeyPressQwerty(PhysicalKey.F4, RawInputModifiers.None);
                root.KeyReleaseQwerty(PhysicalKey.F4, RawInputModifiers.None);
                await PumpAsync(10);
                Assert.IsTrue(reasoning.IsDropDownOpen);
                TopLevel reasoningRoot = TopLevel.GetTopLevel(reasoning.ContainerFromIndex(reasoning.SelectedIndex)!)!;
                reasoningRoot.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                reasoningRoot.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                await PumpAsync(10);
                Assert.IsFalse(reasoning.IsDropDownOpen);
                Assert.IsTrue(popup.IsOpen, "Escape 先关闭思考档位下拉,保留模型弹层");
                root.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                await PumpAsync(10);
                Assert.IsFalse(popup.IsOpen);
                Assert.IsTrue(button.IsKeyboardFocusWithin);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(ReasoningLevel.High)]
    [DataRow(ReasoningLevel.ExtraHigh)]
    [DataRow(ReasoningLevel.Max)]
    public void Composer_At300Pixels_KeepsActionsAndPopupInsidePanel_InEveryMode(ReasoningLevel level)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = TwoModels(level, ReasoningLevel.Default);
            provider.Name = "A very long provider identity that must remain available in the model tooltip";
            provider.Models[0].Model = "A very long model name that must be trimmed in the composer";
            (Window window, ChatPanelView panel, ComboBox reasoning) =
                await WithReasoningPickerAsync(context, provider);
            try
            {
                window.Width = 300;
                await PumpAsync(10);
                foreach (int mode in new[] { 0, 1, 2 })
                {
                    ComboBox modePicker = Find<ComboBox>(panel, "ModeCombo");
                    modePicker.SelectedIndex = mode;
                    typeof(ChatPanelView).GetMethod("SetBusy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(panel, [true]);
                    await PumpAsync(10);
                    Button sendButton = Find<Button>(panel, "SendButton");
                    Point modeOrigin = modePicker.TranslatePoint(default, panel)!.Value;
                    Point sendOrigin = sendButton.TranslatePoint(default, panel)!.Value;
                    Assert.AreEqual(sendOrigin.Y + sendButton.Bounds.Height / 2,
                        modeOrigin.Y + modePicker.Bounds.Height / 2, 1,
                        "模式选择应与发送按钮保持同一水平线");
                    Assert.IsLessThanOrEqualTo(sendOrigin.X, modeOrigin.X + modePicker.Bounds.Width);
                    Assert.AreEqual(20, Find<Grid>(panel, "InputStatusBar").Bounds.Height);
                    double actionHeight = Find<Button>(panel, "AttachButton").Bounds.Height;
                    foreach (string action in new[] { "ModelButton", "ToolsButton", "ModeCombo", "SendButton" })
                        Assert.AreEqual(actionHeight, Find<Control>(panel, action).Bounds.Height, action);
                    foreach (string name in new[] { "AttachButton", "ModelButton", "ModelSummary", "ReasoningSummary", "SendButton", "ToolsButton", "ModeCombo", "ApprovalCombo" })
                    {
                        Control control = Find<Control>(panel, name);
                        if (!control.IsVisible) continue;
                        Point origin = control.TranslatePoint(default, panel)!.Value;
                        Assert.IsGreaterThan(0, control.Bounds.Width,
                            $"{name}: {control.Bounds}; model entry: {Find<Button>(panel, "ModelButton").Bounds}");
                        Assert.IsTrue(origin.X >= 0 && origin.X + control.Bounds.Width <= panel.Bounds.Width + 1, name);
                        Assert.IsTrue(origin.Y >= 0 && origin.Y + control.Bounds.Height <= panel.Bounds.Height + 1, name);
                    }
                }
                Button send = Find<Button>(panel, "SendButton");
                Assert.AreEqual(new Loc("en")["Stop"], AutomationProperties.GetName(send));
                Assert.IsEmpty(send.GetVisualDescendants().OfType<TextBlock>(), "发送按钮只显示向上箭头");
                await ClickComposerControlAsync(window, Find<Button>(panel, "ModelButton"));
                Assert.IsTrue(Find<Popup>(panel, "ModelPopup").IsOpen);
                Assert.IsLessThanOrEqualTo(panel.Bounds.Width, Find<Border>(panel, "ModelPopupBody").Bounds.Width);
                Assert.IsTrue(reasoning.IsEffectivelyVisible);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }
}
