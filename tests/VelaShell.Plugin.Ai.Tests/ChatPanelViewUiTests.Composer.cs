using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.VisualTree;
using AvaloniaEdit;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("已有正文")]
    [DataRow("#/root/notes.txt ")]
    public void Composer_ImePreedit_IsVisibleWithoutChangingTheDocument(string draft)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.Text = draft;
                input.CaretOffset = draft.Length;
                input.TextArea.Focus();
                await PumpAsync(3);
                TextInputMethodClient client = RequestComposerIme(input);
                Assert.IsTrue(client.SupportsPreedit, "Win32 只有在客户端支持预编辑时才交付拼音");
                bool canUndo = input.CanUndo;
                client.SetPreeditText("ce'shi", 2);
                await PumpAsync(3);

                TextPresenter preedit = Find<TextPresenter>(panel, "InputPreedit");
                Assert.IsTrue(preedit.IsEffectivelyVisible);
                Assert.AreEqual("ce'shi", preedit.PreeditText);
                Assert.IsGreaterThan(0, preedit.TextLayout.Width);
                Assert.IsGreaterThan(0, preedit.Bounds.Height);
                Assert.IsFalse(Find<TextBlock>(panel, "InputPlaceholder").IsVisible);
                Assert.AreEqual(draft, input.Text);
                Assert.AreEqual(canUndo, input.CanUndo, "预编辑不得进入撤销栈");
                double cursorAtTwo = client.CursorRectangle.X;
                client.SetPreeditText("ce'shi", 6);
                Assert.IsGreaterThan(cursorAtTwo, client.CursorRectangle.X, "候选窗跟随拼音串内光标");
                client.SetPreeditText("ce'shi");
                Assert.AreEqual(6, preedit.PreeditText!.Length);
                Assert.IsGreaterThan(cursorAtTwo, client.CursorRectangle.X, "无光标参数时定位到末尾");

                client.SetPreeditText(null, null);
                await PumpAsync(3);
                Assert.IsFalse(preedit.IsVisible);
                Assert.AreEqual(draft.Length == 0, Find<TextBlock>(panel, "InputPlaceholder").IsVisible);
                Assert.AreEqual(draft, input.Text);

                client.SetPreeditText("ce", 2);
                client.SetPreeditText("", 0);
                Assert.IsFalse(preedit.IsVisible, "空串与 null 都结束组字");
                client.SetPreeditText("ce'shi", 6);
                window.KeyTextInput("测试");
                await PumpAsync(3);
                Assert.AreEqual(draft + "测试", input.Text, "确认后的中文沿编辑器原路径提交一次");
                Assert.IsFalse(preedit.IsVisible);
                input.Undo();
                Assert.AreEqual(draft, input.Text, "一次撤销只撤销提交的中文");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void Composer_ImeCommit_ReplacesSelectionAndPreservesUndo()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.Text = "前文旧字后文";
                input.Select(2, 2);
                input.TextArea.Focus();
                TextInputMethodClient client = RequestComposerIme(input);
                client.SetPreeditText("xin'zi", 6);
                Assert.AreEqual("前文旧字后文", input.Text);
                Assert.AreEqual("旧字", input.SelectedText);
                window.KeyTextInput("新字");
                await PumpAsync(3);
                Assert.AreEqual("前文新字后文", input.Text);
                input.Undo();
                Assert.AreEqual("前文旧字后文", input.Text);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void Composer_ImePreedit_OwnsKeysAndClearsOnFocusLoss()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.Text = "#/root/notes.txt ";
                input.CaretOffset = input.Text.Length;
                input.TextArea.Focus();
                TextInputMethodClient client = RequestComposerIme(input);
                client.SetPreeditText("ce", 2);
                foreach (PhysicalKey key in new[] { PhysicalKey.Enter, PhysicalKey.ArrowUp, PhysicalKey.ArrowDown,
                             PhysicalKey.Backspace, PhysicalKey.Delete, PhysicalKey.Tab, PhysicalKey.Escape })
                {
                    window.KeyPressQwerty(key, RawInputModifiers.None);
                    window.KeyReleaseQwerty(key, RawInputModifiers.None);
                }
                await PumpAsync(3);
                Assert.AreEqual("#/root/notes.txt ", input.Text);
                Assert.AreEqual(input.Text.Length, input.CaretOffset);
                Assert.IsEmpty(Find<StackPanel>(panel, "MessagesPanel").Children);
                Assert.IsTrue(input.TextArea.IsFocused);
                Find<Button>(panel, "ModelButton").Focus();
                await PumpAsync(3);
                Assert.IsFalse(Find<TextPresenter>(panel, "InputPreedit").IsVisible);
                input.TextArea.Focus();
                window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
                await PumpAsync(3);
                Assert.AreEqual("", input.Text, "组字结束后恢复引用整块删除");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void Composer_ImePreedit_FitsNarrowScrolledInput_AndClearsOnDetach()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                window.Width = 300;
                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.Text = string.Join("\n", Enumerable.Repeat("已有正文，继续输入测试", 30));
                input.CaretOffset = input.Text.Length;
                input.TextArea.Focus();
                input.TextArea.Caret.BringCaretToView();
                await PumpAsync(5);
                TextInputMethodClient client = RequestComposerIme(input);
                client.SetPreeditText("zhong'wen'shu'ru'ce'shi", 5);
                await PumpAsync(5);
                TextPresenter preedit = Find<TextPresenter>(panel, "InputPreedit");
                Canvas layer = Find<Canvas>(panel, "InputPreeditLayer");
                Point origin = preedit.TranslatePoint(default, layer)!.Value;
                Assert.IsGreaterThan(0, input.TextArea.TextView.ScrollOffset.Y);
                Assert.IsTrue(origin.X >= 0 && origin.X + preedit.Bounds.Width <= layer.Bounds.Width + 1);
                Assert.IsTrue(origin.Y >= 0 && origin.Y + preedit.Bounds.Height <= layer.Bounds.Height + 1);
                int cursorChanges = 0;
                client.CursorRectangleChanged += (_, _) => cursorChanges++;
                window.Width = 500;
                await PumpAsync(5);
                Assert.IsGreaterThan(0, cursorChanges, "布局变化应重新定位候选窗");
                panel.Detach();
                Assert.IsFalse(preedit.IsVisible);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    private static TextInputMethodClient RequestComposerIme(TextEditor input)
    {
        var request = new TextInputMethodClientRequestedEventArgs
        {
            RoutedEvent = InputElement.TextInputMethodClientRequestedEvent
        };
        input.TextArea.RaiseEvent(request);
        return request.Client ?? throw new AssertFailedException("输入框未提供 IME 客户端");
    }

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
