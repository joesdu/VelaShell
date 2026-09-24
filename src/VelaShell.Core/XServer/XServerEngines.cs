namespace VelaShell.Core.XServer;

/// <summary>
/// <see cref="Models.XServerOptions.Engine" /> 的合法取值。顺序即设置页下拉的条目顺序。
/// </summary>
public static class XServerEngines
{
    /// <summary>内置的 X 服务端(<c>VelaShell.XServer</c>,随程序分发,各平台都能用)。默认。</summary>
    public const string BuiltIn = "builtin";

    /// <summary>用户自己装的 VcXsrv(只在 Windows 上)。</summary>
    public const string VcXsrv = "vcxsrv";

    /// <summary>全部取值,按下拉顺序。</summary>
    public static IReadOnlyList<string> All { get; } = [BuiltIn, VcXsrv];
}
