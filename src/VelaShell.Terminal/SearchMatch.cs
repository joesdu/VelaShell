namespace VelaShell.Terminal;

/// <summary>表示终端内容搜索命中的一处结果,包含所在行、起始列与匹配长度。</summary>
public class SearchMatch
{
    /// <summary>命中所在的行号(从 0 起)。</summary>
    public int Row { get; set; }

    /// <summary>命中在该行内的起始列号(从 0 起)。</summary>
    public int Column { get; set; }

    /// <summary>命中文本的字符长度。</summary>
    public int Length { get; set; }
}
