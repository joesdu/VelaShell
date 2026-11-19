using Avalonia.Controls;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Chat.Extensions;
using VelaShell.Plugin.Ai.Chat.Memory;
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
            : SkillCatalog.Enabled(_settings.DisabledSkills, GetActiveLocalWorkspaces(), ExtensionRuntime.SkillDirectories(ActivePacks()));

    /// <summary>记忆(插件数据目录的 <c>memory.json</c>;与「配置工具」窗口共用同一实例)。</summary>
    internal MemoryStore Memories => MemoryStore.For(_context.DataDirectory);

    /// <summary>已安装扩展包的文件(插件数据目录的 <c>extensions/</c>)。</summary>
    internal ExtensionPackStore Extensions => field ??= new ExtensionPackStore(_context.DataDirectory);

    /// <summary>现在生效的扩展包(已安装、启用;MCP 是否已确认各自标明)。</summary>
    internal IReadOnlyList<ActivePack> ActivePacks() => ExtensionRuntime.Active(_settings, Extensions);

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
        // 扩展包的命令排在技能之后;与内置命令或技能同名的被它们盖住,不列
        foreach ((string name, string description, string pack) in ExtensionRuntime.Commands(ActivePacks()))
        {
            string command = "/" + name;
            if (_commandCandidates.Contains(command, StringComparer.OrdinalIgnoreCase)
                || LocalCommands.Contains(command, StringComparer.OrdinalIgnoreCase)
                || !command.StartsWith(text[..caret], StringComparison.OrdinalIgnoreCase)) continue;
            _commandCandidates.Add(command);
            _skillCommandDescriptions[command] = _loc.F("CommandFromPack", description.Length > 0 ? description : name, pack);
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
            // /技能名 或扩展包的 /命令:不是本地命令,照常发给模型,由 SendAsync 展开(见 SkillInvocation / ExtensionRuntime)
            if (SkillInvocation.Match(text, InvocableSkills()) is not null
                || ExtensionRuntime.TryExpandCommand(text, ActivePacks()) is not null) return false;
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
        List<string> packCommands = [.. ExtensionRuntime.Commands(ActivePacks()).Select(c => "/" + c.Name)
            .Where(c => !LocalCommands.Contains(c, StringComparer.OrdinalIgnoreCase) && !skillCommands.Contains(c, StringComparer.OrdinalIgnoreCase))];
        if (packCommands.Count > 0) help += " · " + _loc.F("CommandPacksHelp", string.Join(" ", packCommands));
        SetStatus(help);
        return true;
    }
}
