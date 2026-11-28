using System.Text;

namespace VelaShell.Plugin.Ai.Chat.Skills;

/// <summary>技能来自哪里。</summary>
internal enum SkillSource
{
    /// <summary>用户级目录 <c>~/.velashell/skills</c>。</summary>
    User,

    /// <summary>已授权本地工作区里的 <c>.velashell/skills</c>。</summary>
    Workspace,

    /// <summary>已启用的扩展包带来的技能(ai-plan §12.4)。</summary>
    Extension
}

/// <summary>扫描到的一个技能(解析失败时 <see cref="Error" /> 不为空,其余字段尽量填)。</summary>
/// <param name="Name">技能名(<c>SKILL.md</c> 头里的 <c>name</c>;没写时取目录名)。</param>
/// <param name="Description">一句话说明:模型据此判断什么时候用它。</param>
/// <param name="Directory">技能目录的完整路径。</param>
/// <param name="Source">来源。</param>
/// <param name="Error">解析失败的原因;正常为 null。</param>
internal sealed record SkillInfo(string Name, string Description, string Directory, SkillSource Source, string? Error = null)
{
    /// <summary><c>SKILL.md</c> 的完整路径。</summary>
    public string SkillFile => Path.Combine(Directory, SkillCatalog.SkillFileName);

    /// <summary>能不能交给模型用。</summary>
    public bool IsValid => Error is null;
}

/// <summary>
/// 技能目录:扫描用户级与工作区里的技能文件夹,读出各自 <c>SKILL.md</c> 头里的名字与说明(ai-plan §12.3)。
/// </summary>
/// <remarks>
/// <para>
/// 格式与 Anthropic Agent Skills 的 <c>SKILL.md</c> 兼容:文件开头一段 <c>---</c> 包起来的头,至少有 <c>name</c> 与
/// <c>description</c>,后面是正文指令;同目录可以放参考文件与脚本。这里<b>只读</b>,不执行任何附带的脚本。
/// </para>
/// <para>
/// 头只按「<c>键: 值</c>」逐行读(值可加引号,支持 <c>&gt;</c> / <c>|</c> 引出的多行块),不是完整的 YAML;
/// 技能头用到的就这几种写法,不值得为它引入一个 YAML 库。读不懂的键忽略。
/// </para>
/// <para>
/// 同名时工作区的覆盖用户级的(项目里的技能更贴近当前任务),界面上标出来源。
/// 一个技能解析失败只影响它自己:带着原因留在清单里,不进模型的技能目录。
/// </para>
/// </remarks>
internal static class SkillCatalog
{
    /// <summary>技能文件名。</summary>
    public const string SkillFileName = "SKILL.md";

    /// <summary>名字长度上限;超出视为无效(名字要当斜杠命令用)。</summary>
    public const int MaxNameLength = 64;

    /// <summary>说明长度上限(字符);超出截断 —— 它每轮都进系统提示词。</summary>
    public const int MaxDescriptionLength = 300;

    /// <summary><c>SKILL.md</c> 大小上限(字节);超出视为无效。</summary>
    public const int MaxSkillFileBytes = 256 * 1024;

    /// <summary>用户级技能目录:<c>~/.velashell/skills</c>(与 <c>~/.velashell/mcp</c> 同一棵树,卸载插件不会删掉用户写的技能)。</summary>
    public static string UserDirectory
        => UserDirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".velashell", "skills");

    /// <summary>
    /// 测试用:把用户级技能目录指到别处。测试程序集启动时设一次(指向空的临时目录),
    /// 免得开发机上真实的 <c>~/.velashell/skills</c> 混进每条用例的系统提示词。正式运行为 null。
    /// </summary>
    internal static string? UserDirectoryOverride { get; set; }

    /// <summary>
    /// 本轮可用的技能:用户级目录 + 允许读取的已授权工作区,去掉解析失败的与用户停用的。
    /// </summary>
    /// <param name="disabledSkills">停用的技能名(每行一条,见 <c>AiSettings.DisabledSkills</c>)。</param>
    /// <param name="workspaces">本轮生效的本地工作区;只扫描允许读取的。</param>
    /// <param name="extensionSkillDirectories">已启用扩展包的技能目录(直接含各技能文件夹)。</param>
    public static IReadOnlyList<SkillInfo> Enabled(string? disabledSkills, IEnumerable<Configuration.LocalWorkspace> workspaces,
        IEnumerable<string>? extensionSkillDirectories = null)
    {
        HashSet<string> disabled = new(
            (disabledSkills ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        return [.. Scan(UserDirectory, workspaces.Where(w => w.AllowRead && w.RootPath.Length > 0).Select(w => w.RootPath), extensionSkillDirectories)
            .Where(s => s.IsValid && !disabled.Contains(s.Name))];
    }

    /// <summary>工作区里技能目录的相对位置。</summary>
    public static readonly string WorkspaceRelativeDirectory = Path.Combine(".velashell", "skills");

    /// <summary>
    /// 扫描全部技能。同名时工作区的优先;返回按名字排序,含解析失败的(带原因)。
    /// </summary>
    /// <param name="userDirectory">用户级技能目录(测试可替换)。</param>
    /// <param name="workspaceRoots">已授权的本地工作区根目录;只扫描它们下面的 <c>.velashell/skills</c>。</param>
    /// <param name="extensionSkillDirectories">已启用扩展包的技能目录;同名时优先级最低(扩展包 &lt; 用户级 &lt; 工作区,越具体越优先)。</param>
    public static IReadOnlyList<SkillInfo> Scan(string userDirectory, IEnumerable<string> workspaceRoots,
        IEnumerable<string>? extensionSkillDirectories = null)
    {
        var byName = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
        var failed = new List<SkillInfo>();
        foreach (SkillInfo skill in (extensionSkillDirectories ?? []).SelectMany(dir => ScanDirectory(dir, SkillSource.Extension))
                     .Concat(ScanDirectory(userDirectory, SkillSource.User))
                     .Concat(workspaceRoots.SelectMany(root => ScanDirectory(Path.Combine(root, WorkspaceRelativeDirectory), SkillSource.Workspace))))
        {
            if (!skill.IsValid) failed.Add(skill);
            else byName[skill.Name] = skill; // 后扫到的覆盖先扫到的:扩展包 < 用户级 < 工作区
        }
        return [.. byName.Values.Concat(failed).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>扫描一个技能目录(直接含各技能文件夹);目录不存在或读不了时为空。</summary>
    public static IEnumerable<SkillInfo> ScanDirectory(string directory, SkillSource source)
    {
        string[] folders;
        try
        {
            if (!Directory.Exists(directory)) yield break;
            folders = Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (string folder in folders.Order(StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(folder, SkillFileName))) yield return Read(folder, source);
        }
    }

    /// <summary>读一个技能目录的头。</summary>
    public static SkillInfo Read(string folder, SkillSource source)
    {
        string fallbackName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        try
        {
            var file = new FileInfo(Path.Combine(folder, SkillFileName));
            if (file.Length > MaxSkillFileBytes)
                return new SkillInfo(fallbackName, "", folder, source, $"{SkillFileName} is larger than {MaxSkillFileBytes / 1024} KB.");
            (Dictionary<string, string> header, _) = Parse(File.ReadAllText(file.FullName, Encoding.UTF8));
            string name = header.TryGetValue("name", out string? n) && n.Length > 0 ? n : fallbackName;
            if (!IsValidName(name))
            {
                return new SkillInfo(name, "", folder, source,
                    $"Invalid name \"{name}\": use letters, digits, '-' or '_' (at most {MaxNameLength} characters).");
            }

            if (!header.TryGetValue("description", out string? description) || description.Length == 0)
                return new SkillInfo(name, "", folder, source, "Missing 'description' in the SKILL.md header.");
            return new SkillInfo(name, Clip(description, MaxDescriptionLength), folder, source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return new SkillInfo(fallbackName, "", folder, source, ex.Message);
        }
    }

    /// <summary>读正文(头之后的部分)。</summary>
    public static string ReadBody(SkillInfo skill)
    {
        var file = new FileInfo(skill.SkillFile);
        if (file.Length > MaxSkillFileBytes) throw new InvalidOperationException($"{SkillFileName} is too large.");
        return Parse(File.ReadAllText(file.FullName, Encoding.UTF8)).Body;
    }

    /// <summary>名字只许字母、数字、<c>-</c>、<c>_</c>:要当斜杠命令,也要能安全地出现在提示词里。</summary>
    public static bool IsValidName(string name)
        => name.Length is > 0 and <= MaxNameLength && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// 拆出头与正文。没有头(不以 <c>---</c> 开头)时整份都是正文。
    /// </summary>
    public static (Dictionary<string, string> Header, string Body) Parse(string text)
    {
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string normalized = text.Replace("\r\n", "\n").TrimStart('﻿');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return (header, normalized.Trim());
        int end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (header, normalized.Trim());
        string[] lines = normalized[4..end].Split('\n');
        int bodyStart = normalized.IndexOf('\n', end + 4);
        string body = bodyStart < 0 ? "" : normalized[(bodyStart + 1)..].Trim();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.TrimStart().StartsWith('#')) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (value is ">" or "|" or ">-" or "|-" or ">+" or "|+")
            {
                // 多行块:后面缩进的行都属于它;> 折成一行,| 保留换行
                var block = new List<string>();
                while (i + 1 < lines.Length && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
                {
                    block.Add(lines[++i].Trim());
                }
                value = value.StartsWith('>') ? string.Join(' ', block.Where(l => l.Length > 0)) : string.Join('\n', block).Trim();
            }
            else if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
            {
                value = value[1..^1];
            }
            header[key] = value;
        }
        return (header, body);
    }

    private static string Clip(string text, int max)
    {
        string single = string.Join(' ', text.Split(['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return single.Length <= max ? single : single[..(max - 1)].TrimEnd() + "…";
    }
}
