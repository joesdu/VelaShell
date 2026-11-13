using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;

namespace VelaShell.Plugin.Ai.Ui;

public partial class ChatPanelView
{
    private ComposerImeClient? _inputImeClient;
    private bool IsInputComposing => !string.IsNullOrEmpty(InputPreedit.PreeditText);

    private void SetUpInputMethod()
    {
        InputBox.AddHandler(TextInputMethodClientRequestedEvent, (_, e) =>
        {
            if (_detached || e.Client is null || e.Client == _inputImeClient) return;
            if (_inputImeClient is null || _inputImeClient.Inner != e.Client)
            {
                _inputImeClient?.Dispose();
                _inputImeClient = new ComposerImeClient(this, e.Client);
            }
            e.Client = _inputImeClient;
        });
        InputBox.AddHandler(TextInputEvent, (_, _) => SetInputPreedit(null, null), RoutingStrategies.Tunnel);
        InputBox.TextArea.LostFocus += (_, _) => SetInputPreedit(null, null);
        InputBox.DetachedFromVisualTree += (_, _) => SetInputPreedit(null, null);
        InputPreeditLayer.LayoutUpdated += (_, _) => PositionInputPreedit();
    }

    private void UpdateInputPlaceholder() =>
        InputPlaceholder.IsVisible = InputBox.Document.TextLength == 0 && !IsInputComposing;

    private void SetInputPreedit(string? text, int? cursorPosition)
    {
        bool wasComposing = IsInputComposing;
        bool composing = !_detached && !string.IsNullOrEmpty(text);
        if (!wasComposing && !composing) return;
        InputPreedit.PreeditText = composing ? text : null;
        InputPreedit.PreeditTextCursorPosition = cursorPosition;
        InputPreedit.IsVisible = composing;
        UpdateInputPlaceholder();
        if (composing)
        {
            if (!wasComposing) InputBox.TextArea.Caret.BringCaretToView();
            InputBox.TextArea.Caret.Hide();
            PositionInputPreedit();
            InputPreedit.ShowCaret();
        }
        else
        {
            InputPreedit.HideCaret();
            if (!_detached && InputBox.TextArea.IsFocused) InputBox.TextArea.Caret.Show();
        }
        _inputImeClient?.NotifyCursorChanged();
    }

    private void PositionInputPreedit()
    {
        if (!IsInputComposing || _inputImeClient is not { } client) return;
        var view = InputBox.TextArea.TextView;
        if (view.TranslatePoint(default, InputPreeditLayer) is not { } origin
            || client.Inner.TextViewVisual.TranslatePoint(client.Inner.CursorRectangle.Position, InputPreeditLayer) is not { } caret)
            return;

        // 同终端:拼音只叠画,不写进文档/撤销栈;贴近右边缘时向左让位,候选窗跟随串内光标。
        double width = Math.Max(1, view.Bounds.Width);
        InputPreedit.MaxWidth = width;
        InputPreedit.Measure(new Size(width, double.PositiveInfinity));
        double x = Math.Clamp(caret.X, origin.X, origin.X + Math.Max(0, width - InputPreedit.DesiredSize.Width));
        double y = Math.Clamp(caret.Y, origin.Y, origin.Y + Math.Max(0, view.Bounds.Height - InputPreedit.DesiredSize.Height));
        Canvas.SetLeft(InputPreedit, x);
        Canvas.SetTop(InputPreedit, y);
        client.NotifyCursorChanged();
    }

    /// <summary>
    /// AvaloniaEdit 12 不支持预编辑;同终端声明 SupportsPreedit,否则 Win32 只显示候选窗、
    /// 丢掉拼音。已有环绕文本/选区仍交给编辑器,提交仍走它的 TextInput。
    /// </summary>
    private sealed class ComposerImeClient : TextInputMethodClient, IDisposable
    {
        private readonly ChatPanelView _owner;
        public TextInputMethodClient Inner { get; }

        public ComposerImeClient(ChatPanelView owner, TextInputMethodClient inner)
        {
            _owner = owner;
            Inner = inner;
            inner.TextViewVisualChanged += OnTextViewChanged;
            inner.CursorRectangleChanged += OnCursorChanged;
            inner.SurroundingTextChanged += OnSurroundingTextChanged;
            inner.SelectionChanged += OnSelectionChanged;
            inner.ResetRequested += OnResetRequested;
            inner.InputPaneActivationRequested += OnInputPaneRequested;
        }

        public override Visual TextViewVisual => Inner.TextViewVisual;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => Inner.SupportsSurroundingText;
        public override string SurroundingText => Inner.SurroundingText;
        public override TextSelection Selection
        {
            get => Inner.Selection;
            set => Inner.Selection = value;
        }

        public override Rect CursorRectangle
        {
            get
            {
                if (!_owner.IsInputComposing) return Inner.CursorRectangle;
                var presenter = _owner.InputPreedit;
                int length = presenter.PreeditText!.Length;
                int cursor = presenter.PreeditTextCursorPosition is { } position && position >= 0 && position <= length
                    ? position : length;
                Rect caret = presenter.TextLayout.HitTestTextPosition(cursor);
                Point? origin = _owner.InputPreeditLayer.TranslatePoint(
                    new Point(Canvas.GetLeft(presenter), Canvas.GetTop(presenter)), TextViewVisual);
                return origin is { } point ? new Rect(point + caret.Position, new Size(0, caret.Height)) : Inner.CursorRectangle;
            }
        }

        public override void SetPreeditText(string? preeditText) => _owner.SetInputPreedit(preeditText, null);
        public override void SetPreeditText(string? preeditText, int? cursorPosition) =>
            _owner.SetInputPreedit(preeditText, cursorPosition);
        public override void ExecuteContextMenuAction(ContextMenuAction action) => Inner.ExecuteContextMenuAction(action);

        public void NotifyCursorChanged() => RaiseCursorRectangleChanged();
        private void OnTextViewChanged(object? sender, EventArgs e) => RaiseTextViewVisualChanged();
        private void OnCursorChanged(object? sender, EventArgs e)
        {
            _owner.PositionInputPreedit();
            RaiseCursorRectangleChanged();
        }
        private void OnSurroundingTextChanged(object? sender, EventArgs e) => RaiseSurroundingTextChanged();
        private void OnSelectionChanged(object? sender, EventArgs e) => RaiseSelectionChanged();
        private void OnResetRequested(object? sender, EventArgs e) => RequestReset();
        private void OnInputPaneRequested(object? sender, EventArgs e) => RaiseInputPaneActivationRequested();

        public void Dispose()
        {
            _owner.SetInputPreedit(null, null);
            Inner.TextViewVisualChanged -= OnTextViewChanged;
            Inner.CursorRectangleChanged -= OnCursorChanged;
            Inner.SurroundingTextChanged -= OnSurroundingTextChanged;
            Inner.SelectionChanged -= OnSelectionChanged;
            Inner.ResetRequested -= OnResetRequested;
            Inner.InputPaneActivationRequested -= OnInputPaneRequested;
        }
    }
}
