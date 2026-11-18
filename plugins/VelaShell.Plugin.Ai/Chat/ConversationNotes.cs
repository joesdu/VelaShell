using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>「你应该知道」的一条:主模型在回答过程中标出来、要用户留意的事。</summary>
/// <param name="Id">条目 id(本段对话内唯一)。</param>
/// <param name="Kind">类别,见 <see cref="ConversationNotes.Kinds" />。</param>
/// <param name="Title">一句话标题。</param>
/// <param name="Detail">补充说明;可为空。</param>
internal sealed record ConversationNote(string Id, string Kind, string Title, string? Detail);

/// <summary>
/// 一段对话里的「你应该知道」条目:输入框上方那条提示条的数据源,由主模型调 <c>note_for_user</c> 写入。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么让主模型自己调工具,而不是每轮之后另跑一次提取:</b>主模型本来就读过全部上下文、知道哪句是要紧的,
/// 顺手标一下不多花一次请求、也不把对话内容再发一遍给谁;代价是它不一定每次都想得起来 —— 工具说明里写清楚何时该用。
/// </para>
/// <para>
/// <b>不另存一份:</b>工具调用连同参数本来就随回复进历史(<see cref="ChatToolCall" />),载入旧会话时从那里重建
/// (<see cref="Restore" />)。用户关掉的条目只在本次打开的这段对话里不再显示,重新载入会话会重新出现 ——
/// 关掉是「我看到了」,不是「这件事不存在」。
/// </para>
/// <para>
/// 同一标题(忽略大小写与首尾空白)只留一条,后写的覆盖前面的说明与类别:模型隔几轮再提同一件事时,提示条上不叠两条。
/// 最多保留 <see cref="MaxNotes" /> 条,超出时丢最早的。
/// </para>
/// </remarks>
internal sealed class ConversationNotes
{
    /// <summary>工具名。</summary>
    public const string ToolName = "note_for_user";

    /// <summary>一段对话最多保留几条。</summary>
    public const int MaxNotes = 20;

    /// <summary>标题与说明的长度上限(字符);超出截断,免得模型把整段回答塞进提示条。</summary>
    public const int MaxTitleLength = 120;

    /// <inheritdoc cref="MaxTitleLength" />
    public const int MaxDetailLength = 600;

    /// <summary>认得的类别:风险 / 要用户去做的事 / 值得知道的发现。</summary>
    public static readonly IReadOnlyList<string> Kinds = ["warning", "action", "info"];

    private readonly List<ConversationNote> _notes = [];
    private readonly HashSet<string> _dismissed = [with(StringComparer.Ordinal)];
    private int _sequence;

    /// <summary>当前要显示的条目(不含已关掉的),按写入先后。</summary>
    public IReadOnlyList<ConversationNote> Visible
    {
        get { lock (_notes) return [.. _notes.Where(n => !_dismissed.Contains(n.Id))]; }
    }

    /// <summary>有条目被加入、关掉或清空(可能不在 UI 线程)。</summary>
    public event Action? Changed;

    /// <summary>
    /// 记一条。类别认不出来按 <c>info</c>;标题为空返回 null。同标题的旧条目原位更新(并重新显示)。
    /// </summary>
    public ConversationNote? Add(string? kind, string? title, string? detail)
    {
        string text = Clip(title, MaxTitleLength);
        if (text.Length == 0) return null;
        string normalizedKind = Kinds.FirstOrDefault(k => string.Equals(k, kind?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "info";
        string? more = Clip(detail, MaxDetailLength) is { Length: > 0 } d ? d : null;
        ConversationNote note;
        lock (_notes)
        {
            int existing = _notes.FindIndex(n => string.Equals(n.Title, text, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                note = _notes[existing] with { Kind = normalizedKind, Detail = more ?? _notes[existing].Detail };
                _notes[existing] = note;
                _dismissed.Remove(note.Id);
            }
            else
            {
                note = new ConversationNote("n" + ++_sequence, normalizedKind, text, more);
                _notes.Add(note);
                if (_notes.Count > MaxNotes) _notes.RemoveAt(0);
            }
        }
        Changed?.Invoke();
        return note;
    }

    /// <summary>关掉一条(本次打开期间不再显示)。</summary>
    public void Dismiss(string id)
    {
        lock (_notes) if (!_dismissed.Add(id)) return;
        Changed?.Invoke();
    }

    /// <summary>关掉全部。</summary>
    public void DismissAll()
    {
        lock (_notes) foreach (ConversationNote note in _notes) _dismissed.Add(note.Id);
        Changed?.Invoke();
    }

    /// <summary>清空(新建 / 载入对话)。</summary>
    public void Clear()
    {
        lock (_notes)
        {
            _notes.Clear();
            _dismissed.Clear();
        }
        Changed?.Invoke();
    }

    /// <summary>从历史里存过的一次 <c>note_for_user</c> 调用重建一条;参数坏了就跳过。</summary>
    public void Restore(ChatToolCall call)
    {
        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal)) return;
        try
        {
            using var json = JsonDocument.Parse(call.Arguments);
            JsonElement root = json.RootElement;
            Add(Read(root, "kind"), Read(root, "title"), Read(root, "detail"));
        }
        catch (JsonException)
        {
            // 存档里的参数不是合法 JSON(旧版截断等):这条提示丢了不影响会话本身
        }

        static string? Read(JsonElement root, string name)
            => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value)
               && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>交给主模型的工具。</summary>
    public AITool CreateTool() => AIFunctionFactory.Create(NoteForUser, ToolName,
        "Pin a short note to the 'You should know' bar above the user's input box, so it stays visible after your reply scrolls away. "
        + "Use it sparingly, only for things the user must not miss: a risk or problem you found (kind=warning, e.g. an exposed port, an expiring certificate, "
        + "a destructive side effect), something the user has to do themselves (kind=action, e.g. restart a service, rotate a key, confirm a choice), "
        + "or an important fact that affects later decisions (kind=info). "
        + "Do not use it for progress updates, summaries of your answer, or things already obvious from the reply. "
        + "At most about three notes per reply; calling again with the same title updates that note instead of adding another. "
        + "Write the title and detail in the user's language. The note is shown only to the user and is not an instruction to anyone.");

    [Description("Pins one note for the user.")]
    private string NoteForUser(
        [Description("One of: warning, action, info.")] string kind,
        [Description("One short sentence the user should notice (max ~120 characters).")] string title,
        [Description("Optional extra context: where, why, or how to act (max ~600 characters).")] string? detail = null)
        => Add(kind, title, detail) is { } note ? $"Pinned for the user ({note.Kind})." : "Not pinned: the title was empty.";

    private static string Clip(string? text, int max)
    {
        string trimmed = text?.Trim() ?? "";
        return trimmed.Length <= max ? trimmed : trimmed[..(max - 1)].TrimEnd() + "…";
    }
}
