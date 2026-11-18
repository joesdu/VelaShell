using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 输入框上方的「你应该知道」条:主模型经 <c>note_for_user</c> 标出的风险、要用户去做的事与要紧发现。
/// </summary>
/// <remarks>
/// <para>
/// 长回复里真正要用户留意的往往只有一两句,回复一滚走就找不到了;这条把它们钉在输入框上方。
/// 数据在每份对话自己的 <see cref="Conversation.Notes" /> 里(后台对话写入不会出现在正看着的这份上),
/// 写入可能不在 UI 线程,由 <c>NewConversation</c> 挂的 <c>Changed</c> 投递回来重画。
/// </para>
/// <para>
/// 默认展开(要的就是被看见);标题行可折叠,每条可单独关掉,行尾 × 关掉全部。
/// 关掉只在本次打开期间有效,重新载入会话时从历史里的工具调用重建(见 <see cref="ConversationNotes.Restore" />)。
/// </para>
/// </remarks>
public partial class ChatPanelView
{
    private bool _notesExpanded = true;

    /// <summary>当前显示的这份对话的「你应该知道」(测试用)。</summary>
    internal ConversationNotes ActiveNotes => _active.Notes;

    /// <summary>按当前这份对话重画提示条;没有要显示的条目时整条隐藏。</summary>
    internal void RefreshNotes()
    {
        IReadOnlyList<ConversationNote> notes = _active.Notes.Visible;
        NotesBar.IsVisible = notes.Count > 0;
        if (notes.Count == 0)
        {
            NotesBar.Child = null;
            return;
        }

        var header = new Grid
        {
            Name = "NotesHeader",
            ColumnDefinitions = [with("Auto,Auto,Auto,*,Auto")],
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        AutomationProperties.SetName(header, _loc["NotesToggle"]);
        header.Children.Add(new Decorator
        {
            Child = MakeIcon(_notesExpanded ? "AiIcon.chevron-down" : "Icon.chevron-right", "VelaTextMuted", 10),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        var icon = new Decorator
        {
            Child = MakeIcon("AiIcon.sparkles", "VelaAccent", 12),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 1);
        header.Children.Add(icon);
        var title = new TextBlock
        {
            Name = "NotesTitle",
            Classes = { "changedFilesTitle" },
            Text = _loc.F("NotesTitle", notes.Count)
        };
        Grid.SetColumn(title, 2);
        header.Children.Add(title);

        var clear = new Button
        {
            Name = "NotesDismissAll",
            Content = MakeIcon("AiIcon.x", "VelaTextMuted", 12),
            Padding = new Thickness(4, 2),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        clear[!ThemeProperty] = new DynamicResourceExtension("AiGhostIconButtonTheme");
        ToolTip.SetTip(clear, _loc["NotesDismissAll"]);
        AutomationProperties.SetName(clear, _loc["NotesDismissAll"]);
        clear.Click += (_, e) =>
        {
            e.Handled = true;
            _active.Notes.DismissAll();
        };
        Grid.SetColumn(clear, 4);
        header.Children.Add(clear);
        header.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _notesExpanded = !_notesExpanded;
            RefreshNotes();
        };

        var rows = new StackPanel { Name = "NotesList", Spacing = 1 };
        foreach (ConversationNote note in notes)
        {
            rows.Children.Add(NoteRow(note));
        }
        var scroll = new ScrollViewer
        {
            Content = rows,
            MaxHeight = 168,
            Margin = new Thickness(0, 4, 0, 0),
            IsVisible = _notesExpanded,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(scroll);
        NotesBar.Child = stack;
    }

    private Border NoteRow(ConversationNote note)
    {
        (string iconKey, string brushKey, string kindKey) = note.Kind switch
        {
            "warning" => ("AiIcon.shield-alert", "VelaWarning", "NoteKindWarning"),
            "action" => ("AiIcon.list-todo", "VelaInfo", "NoteKindAction"),
            _ => ("AiIcon.sparkles", "VelaTextTertiary", "NoteKindInfo")
        };
        var grid = new Grid { ColumnDefinitions = [with("Auto,*,Auto")] };
        var kindIcon = new Decorator
        {
            Child = MakeIcon(iconKey, brushKey, 12),
            Margin = new Thickness(0, 1, 6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };
        ToolTip.SetTip(kindIcon, _loc[kindKey]);
        grid.Children.Add(kindIcon);

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Classes = { "noteTitle" }, Text = note.Title });
        if (note.Detail is { Length: > 0 } detail)
        {
            text.Children.Add(new TextBlock { Classes = { "noteDetail" }, Text = detail });
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var dismiss = new Button
        {
            Name = "NoteDismiss",
            Content = MakeIcon("AiIcon.x", "VelaTextMuted", 10),
            Padding = new Thickness(4, 2),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };
        dismiss[!ThemeProperty] = new DynamicResourceExtension("AiGhostIconButtonTheme");
        ToolTip.SetTip(dismiss, _loc["NoteDismiss"]);
        AutomationProperties.SetName(dismiss, _loc["NoteDismiss"]);
        dismiss.Click += (_, e) =>
        {
            e.Handled = true;
            _active.Notes.Dismiss(note.Id);
        };
        Grid.SetColumn(dismiss, 2);
        grid.Children.Add(dismiss);

        var row = new Border { Classes = { "noteRow" }, Child = grid, Tag = note };
        AutomationProperties.SetName(row, $"{_loc[kindKey]}: {note.Title}");
        return row;
    }
}
