namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 集中解析并暴露 VelaShell 各项持久化文件与目录的绝对路径(以 LocalApplicationData/VelaShell 为根)。
/// </summary>
public sealed class VelaShellStoragePaths
{
    /// <summary>
    /// 基于当前用户的本地应用数据目录构造所有存储路径。
    /// </summary>
    public VelaShellStoragePaths()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VelaShell");
        RootDirectory = root;
        SettingsFile = Path.Combine(root, "settings.json");
        StateFile = Path.Combine(root, "state.json");
        SessionsFile = Path.Combine(root, "sessions.json");
        SonnetDbDirectory = Path.Combine(root, "sonnetdb");
        SecretKeyFile = Path.Combine(root, "secret.key");
        LegacyDotDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".velashell");
    }

    /// <summary>VelaShell 所有持久化数据的根目录。</summary>
    public string RootDirectory { get; }

    /// <summary>历史 JSON 设置文件(仅用于一次性迁移导入)。</summary>
    public string SettingsFile { get; }

    /// <summary>历史 JSON 状态文件(仅用于一次性迁移导入)。</summary>
    public string StateFile { get; }

    /// <summary>历史 JSON 会话文件(仅用于一次性迁移导入)。</summary>
    public string SessionsFile { get; }

    /// <summary>嵌入式 SonnetDB 数据目录(唯一的持久化存储)。</summary>
    public string SonnetDbDirectory { get; }

    /// <summary>AES-256 敏感字段加密的本地密钥文件。</summary>
    public string SecretKeyFile { get; }

    /// <summary>早期版本使用的 ~/.velashell 目录(仅用于一次性迁移导入)。</summary>
    public string LegacyDotDirectory { get; }
}
