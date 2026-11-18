namespace VelaShell.Plugin.Ai.Ui;

/// <summary>工具的类别:决定工具卡片头部那枚图标与它的提示文案。</summary>
internal enum ToolKind
{
    /// <summary>外部工具(MCP 服务器带来的)或未登记的工具。</summary>
    External,
    Read,
    Write,
    Delete,
    Rename,
    Directory,
    CreateDirectory,
    Search,
    TerminalRead,
    TerminalWrite,
    Command,
    Task,
    Upload,
    Download,
    Transfer,
    WebSearch,
    WebFetch,
    Session,
    System,
    SendFile,
    Image,
    Subagent,
    Team,

    /// <summary>「你应该知道」:钉到输入框上方提示条的条目(<c>note_for_user</c>)。</summary>
    Note
}

/// <summary>
/// 「工具名 → 类别 → 图标键」的静态映射。
/// </summary>
/// <remarks>
/// <para>
/// 只按<b>完整工具名</b>查表,不按前缀猜:MCP 工具的名字是 <c>服务器前缀_工具名</c>,
/// 一台文件系统 MCP 也可能带个 <c>read_file</c>,按前缀猜就会把外部工具画成内置工具的样子。
/// 没登记的一律是 <see cref="ToolKind.External" />(插头图标)。
/// </para>
/// <para>
/// 图标几何全部在 <c>AiTheme.axaml</c> 里以 <c>AiIcon.*</c> 内联:隔离进程只下发 <c>Vela*</c> 令牌,
/// 不能假定宿主的 <c>Icon.*</c> 一定在。新增内置工具时在 <see cref="Kinds" /> 里登记一行,
/// <c>ToolIconsTests</c> 会核对 <c>AgentToolbox.Catalog</c> 里的每个工具都有类别。
/// </para>
/// </remarks>
internal static class ToolIcons
{
    /// <summary>内置工具的类别表。</summary>
    private static readonly Dictionary<string, ToolKind> Kinds = new(StringComparer.Ordinal)
    {
        ["read_remote_file"] = ToolKind.Read,
        ["read_local_file"] = ToolKind.Read,

        ["write_remote_file"] = ToolKind.Write,
        ["patch_remote_file"] = ToolKind.Write,
        ["write_local_file"] = ToolKind.Write,
        ["patch_local_file"] = ToolKind.Write,

        ["delete_local_path"] = ToolKind.Delete,
        ["delete_remote_path"] = ToolKind.Delete,

        ["rename_remote_path"] = ToolKind.Rename,
        ["rename_local_path"] = ToolKind.Rename,

        ["list_remote_directory"] = ToolKind.Directory,
        ["list_local_directory"] = ToolKind.Directory,
        ["stat_remote_path"] = ToolKind.Directory,
        ["stat_local_path"] = ToolKind.Directory,
        ["get_working_directory"] = ToolKind.Directory,
        ["list_local_workspaces"] = ToolKind.Directory,

        ["make_remote_directory"] = ToolKind.CreateDirectory,
        ["make_local_directory"] = ToolKind.CreateDirectory,

        ["search_terminal"] = ToolKind.Search,
        ["search_local_files"] = ToolKind.Search,
        ["search_remote_files"] = ToolKind.Search,
        ["find_local_files"] = ToolKind.Search,
        ["find_remote_files"] = ToolKind.Search,

        ["read_terminal"] = ToolKind.TerminalRead,
        ["write_terminal"] = ToolKind.TerminalWrite,

        ["run_command"] = ToolKind.Command,
        ["run_local_command"] = ToolKind.Command,
        ["run_on_sessions"] = ToolKind.Command,

        ["task_status"] = ToolKind.Task,
        ["task_wait"] = ToolKind.Task,
        ["task_cancel"] = ToolKind.Task,

        ["upload_local_file"] = ToolKind.Upload,
        ["download_remote_file"] = ToolKind.Download,
        ["transfer_status"] = ToolKind.Transfer,
        ["transfer_wait"] = ToolKind.Transfer,
        ["transfer_cancel"] = ToolKind.Transfer,
        ["transfer_resume"] = ToolKind.Transfer,

        ["web_search"] = ToolKind.WebSearch,
        ["web_fetch"] = ToolKind.WebFetch,

        ["list_sessions"] = ToolKind.Session,
        ["list_saved_sessions"] = ToolKind.Session,
        ["open_session"] = ToolKind.Session,
        ["close_session"] = ToolKind.Session,

        ["system_overview"] = ToolKind.System,
        ["send_file"] = ToolKind.SendFile,

        ["generate_image"] = ToolKind.Image,
        ["edit_image"] = ToolKind.Image,

        ["delegate_subagent"] = ToolKind.Subagent,
        ["subagent_status"] = ToolKind.Subagent,
        ["subagent_wait"] = ToolKind.Subagent,
        ["subagent_cancel"] = ToolKind.Subagent,

        ["team_run"] = ToolKind.Team,
        ["team_status"] = ToolKind.Team,
        ["team_message"] = ToolKind.Team,
        ["team_cancel"] = ToolKind.Team,
        ["team_retry"] = ToolKind.Team,

        ["note_for_user"] = ToolKind.Note
    };

    /// <summary>工具的类别;没登记的(MCP 工具等)为 <see cref="ToolKind.External" />。</summary>
    /// <param name="toolName">工具名。</param>
    public static ToolKind KindOf(string toolName)
        => Kinds.TryGetValue(toolName, out ToolKind kind) ? kind : ToolKind.External;

    /// <summary>这个工具名是否登记过类别(给测试核对目录用)。</summary>
    /// <param name="toolName">工具名。</param>
    public static bool IsKnown(string toolName) => Kinds.ContainsKey(toolName);

    /// <summary>工具卡片头部的图标资源键(<c>AiTheme.axaml</c> 里的 <c>AiIcon.*</c>)。</summary>
    /// <param name="toolName">工具名。</param>
    public static string IconKey(string toolName) => IconKey(KindOf(toolName));

    /// <summary>类别对应的图标资源键。</summary>
    /// <param name="kind">类别。</param>
    public static string IconKey(ToolKind kind) => kind switch
    {
        ToolKind.Read => "AiIcon.file-text",
        ToolKind.Write => "AiIcon.file-pen",
        ToolKind.Delete => "AiIcon.trash-2",
        ToolKind.Rename => "AiIcon.arrow-right-left",
        ToolKind.Directory => "AiIcon.folder-open",
        ToolKind.CreateDirectory => "AiIcon.folder-plus",
        ToolKind.Search => "AiIcon.search",
        ToolKind.TerminalRead => "AiIcon.terminal",
        ToolKind.TerminalWrite => "AiIcon.keyboard",
        ToolKind.Command => "AiIcon.square-terminal",
        ToolKind.Task => "AiIcon.clock",
        ToolKind.Upload => "AiIcon.upload",
        ToolKind.Download => "AiIcon.download",
        ToolKind.Transfer => "AiIcon.arrow-up-down",
        ToolKind.WebSearch => "AiIcon.globe",
        ToolKind.WebFetch => "AiIcon.link",
        ToolKind.Session => "AiIcon.server",
        ToolKind.System => "AiIcon.activity",
        ToolKind.SendFile => "AiIcon.send",
        ToolKind.Image => "AiIcon.image",
        ToolKind.Subagent => "AiIcon.bot",
        ToolKind.Team => "AiIcon.users",
        ToolKind.Note => "AiIcon.sparkles",
        _ => "AiIcon.plug"
    };

    /// <summary>类别提示文案在 <see cref="Loc" /> 里的键(<c>ToolKind*</c>)。</summary>
    /// <param name="kind">类别。</param>
    public static string LabelKey(ToolKind kind) => "ToolKind" + kind;

    /// <summary>全部类别对应的图标键(给测试核对 AiTheme.axaml 用)。</summary>
    public static IEnumerable<string> AllIconKeys => Enum.GetValues<ToolKind>().Select(IconKey).Distinct();
}
