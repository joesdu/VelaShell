using Avalonia.Controls;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Chat.Skills;

namespace VelaShell.Plugin.Ai.Ui;

public partial class ChatPanelView
{
    private readonly List<string> _commandCandidates = [];
    private static readonly string[] LocalCommands = ["/clear", "/resume", "/help"];

    /// <summary>候选里的技能命令 → 它的一句说明(内置命令的说明走 <see cref="CommandDescription" />)。</summary>
    private readonly Dictionary<string, string> _skillCommandDescriptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 可以用 <c>/技能名</c> 调用的技能:与模型看到的技能目录同一套(用户级 + 当前工作区,去掉停用的);
    /// 在「配置工具」里停用了 <c>load_skill</c>(整体关闭技能)时为空。
    /// </summary>
    private IReadOnlyList<SkillInfo> InvocableSkills()
        => (SplitLines(_settings.DisabledBuiltinTools) ?? []).Contains(SkillToolSource.LoadToolName, StringComparer.OrdinalIgnoreCase)
            ? []
            : SkillCatalog.Enabled(_settings.DisabledSkills, GetActiveLocalWorkspaces());

    private bool TryShowCommandPicker(string text, int caret)
    {
        if (!text.StartsWith('/') || caret < 1 || text[..caret].Any(char.IsWhiteSpace)) return false;
        _commandCandidates.Clear();
        _skillCommandDescriptions.Clear();
        _commandCandidates.AddRange(LocalCommands.Where(command => command.StartsWith(text[..caret], StringComparison.OrdinalIgnoreCase)));
        // 技能排在内置命令之后;与内置命令同名的技能用不了斜杠调用(内置的优先),不列出来
        foreach (SkillInfo skill in InvocableSkills())
        {
            string command = "/" + skill.Name;
            if (LocalCommands.Contains(command, StringComparer.OrdinalIgnoreCase)
                || !command.StartsWith(text[..caret], StringComparison.OrdinalIgnoreCase)) continue;
            _commandCandidates.Add(command);
            _skillCommandDescriptions[command] = skill.Description;
        }
        _imageMentionCandidates.Clear();
        _fileCandidates.Clear();
        _pickerGeneration++;
        _fileTokenStart = 0;
        _fileIndex = 0;
        FileList.Children.Clear();
        foreach (string command in _commandCandidates)
        {
            int index = FileList.Children.Count;
            var row = new Border
            {
                Classes = { "fileRow" },
                Child = new TextBlock
                {
                    Classes = { "fileName" },
                    Text = command + " · " + (_skillCommandDescriptions.TryGetValue(command, out string? skillDescription)
                        ? _loc.F("CommandSkill", skillDescription)
                        : _loc[CommandDescription(command)]),
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
                }
            };
            row.PointerPressed += (_, e) => { e.Handled = true; AcceptCommand(index); };
            FileList.Children.Add(row);
        }
        FilePopupHeader.Text = _loc["CommandPickerHeader"];
        FilePopup.IsOpen = _commandCandidates.Count > 0;
        HighlightCandidate();
        return true;
    }

    private static string CommandDescription(string command) => command switch
    {
        "/clear" => "CommandClear",
        "/resume" => "CommandResume",
        _ => "CommandHelp"
    };

    private void AcceptCommand(int index)
    {
        if (index < 0 || index >= _commandCandidates.Count) return;
        string replacement = _commandCandidates[index] + " ";
        int caret = Math.Clamp(InputBox.CaretOffset, 0, InputBox.Text.Length);
        _pickerSuspended = true;
        try
        {
            InputBox.Document.Replace(0, caret, replacement);
            InputBox.CaretOffset = replacement.Length;
        }
        finally { _pickerSuspended = false; }
        CloseFilePicker();
        InputBox.TextArea.Focus();
    }

    private async Task<bool> TryRunLocalCommandAsync(string text)
    {
        if (!text.StartsWith('/')) return false;
        string command = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!LocalCommands.Contains(command, StringComparer.OrdinalIgnoreCase))
        {
            // /技能名:不是本地命令,照常发给模型,由 SendAsync 把技能指令附上(见 SkillInvocation)
            if (SkillInvocation.Match(text, InvocableSkills()) is not null) return false;
            SetStatus(_loc["CommandUnknown"]);
            return true;
        }
        if (Busy)
        {
            SetStatus(_loc["CommandBusy"]);
            return true;
        }
        if (command.Equals("/clear", StringComparison.OrdinalIgnoreCase))
        {
            if (!text.Equals(command, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus(_loc["CommandClear"]);
                return true;
            }
            ClearAttachments();
            InputBox.Text = "";
            CloseFilePicker();
            StartNewChat();
            return true;
        }
        if (command.Equals("/resume", StringComparison.OrdinalIgnoreCase))
        {
            string query = text[command.Length..].Trim();
            InputBox.Text = "";
            CloseFilePicker();
            if (query.Length > 0)
            {
                IReadOnlyList<ChatSessionSummary> sessions = await _historyStore.ListSessionsAsync();
                ChatSessionSummary? selected = sessions.FirstOrDefault(session => session.Id.Equals(query, StringComparison.Ordinal));
                if (selected is not null) { ClearAttachments(); await LoadConversationAsync(selected); return true; }
            }
            ClearAttachments();
            HistorySearchBox.Text = query;
            SetActiveView(PanelView.History);
            await RefreshHistoryListAsync();
            return true;
        }
        InputBox.Text = "";
        CloseFilePicker();
        string help = string.Join(" · ", LocalCommands.Select(item => item + ": " + _loc[CommandDescription(item)]));
        List<string> skillCommands = [.. InvocableSkills().Select(s => "/" + s.Name)
            .Where(c => !LocalCommands.Contains(c, StringComparer.OrdinalIgnoreCase))];
        if (skillCommands.Count > 0) help += " · " + _loc.F("CommandSkillsHelp", string.Join(" ", skillCommands));
        SetStatus(help);
        return true;
    }
}
