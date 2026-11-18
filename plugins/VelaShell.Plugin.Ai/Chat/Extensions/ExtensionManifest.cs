using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VelaShell.Plugin.Ai.Chat.Skills;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Chat.Extensions;

/// <summary>扩展包带来的一条斜杠命令:<c>/名字 参数</c> → 按模板展开的请求。</summary>
internal sealed class ExtensionCommand
{
    /// <summary>命令名(不含斜杠;字母数字 <c>-</c> <c>_</c>,64 字符内)。</summary>
    public string Name { get; set; } = "";

    /// <summary>一句说明,显示在 <c>/</c> 候选里。</summary>
    public string Description { get; set; } = "";

    /// <summary>提示词模板;<c>{{args}}</c> 换成用户在命令后写的内容,没写占位符时把它接在后面。</summary>
    public string Prompt { get; set; } = "";
}

/// <summary>扩展包带来的一台 MCP 服务器(结构与手工配置的 MCP 对应,但参数是数组、环境变量与请求头是字典)。</summary>
internal sealed class ExtensionMcpServer
{
    /// <summary>服务器名(包内唯一;工具名前缀为 <c>包id_名字</c>)。</summary>
    public string Name { get; set; } = "";

    /// <summary><c>stdio</c>(默认)或 <c>http</c>。</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>stdio:启动命令。</summary>
    public string Command { get; set; } = "";

    /// <summary>stdio:参数(逐个,不经 shell)。</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>stdio:额外的环境变量。</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>stdio:工作目录(相对扩展包目录;空 = 扩展包目录本身)。</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>http:地址。</summary>
    public string Url { get; set; } = "";

    /// <summary>http:请求头。</summary>
    public Dictionary<string, string> Headers { get; set; } = [];
}

/// <summary>
/// 扩展包清单 <c>velashell-extension.json</c>(ai-plan §12.4):一个扩展包打包若干技能、斜杠命令与 MCP 服务器。
/// </summary>
/// <remarks>
/// <para>
/// 叫「扩展包」而不叫「插件」:VelaShell 宿主已经有插件体系(这个 AI 插件本身就是其中之一),同名会混淆。
/// </para>
/// <para>
/// 清单只是数据:技能与命令是给模型的文字;MCP 服务器会在用户本机启动进程或连远端地址,
/// 所以安装时把完整的启动命令、参数、工作目录、环境变量与地址列给用户确认,确认的是 <see cref="McpFingerprint" />。
/// 之后清单里这部分变了(升级、或文件被改)就要重新确认,在那之前这个扩展包的 MCP 服务器不启动。
/// </para>
/// </remarks>
internal sealed class ExtensionManifest
{
    /// <summary>清单文件名(扩展包根目录下)。</summary>
    public const string FileName = "velashell-extension.json";

    /// <summary>清单大小上限(字节)。</summary>
    public const int MaxManifestBytes = 256 * 1024;

    /// <summary>命令提示词模板上限(字符)。</summary>
    public const int MaxPromptLength = 16 * 1024;

    /// <summary>扩展包 id:小写字母数字 <c>-</c> <c>_</c>,64 字符内;也是安装目录名与 MCP 工具名前缀的一部分。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>版本(只显示,不比较)。</summary>
    public string Version { get; set; } = "";

    /// <summary>一句说明。</summary>
    public string Description { get; set; } = "";

    /// <summary>技能目录(相对扩展包根目录);空 = <c>skills</c>(存在时)。</summary>
    public string Skills { get; set; } = "";

    /// <summary>斜杠命令。</summary>
    public List<ExtensionCommand> Commands { get; set; } = [];

    /// <summary>MCP 服务器。</summary>
    public List<ExtensionMcpServer> McpServers { get; set; } = [];

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>读并校验一个扩展包目录的清单;不合格时抛 <see cref="InvalidDataException" />,消息说明哪里不对。</summary>
    public static ExtensionManifest Load(string packDirectory)
    {
        var file = new FileInfo(Path.Combine(packDirectory, FileName));
        if (!file.Exists) throw new InvalidDataException($"{FileName} was not found at the root of the extension pack.");
        if (file.Length > MaxManifestBytes) throw new InvalidDataException($"{FileName} is larger than {MaxManifestBytes / 1024} KB.");
        ExtensionManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(file.FullName, Encoding.UTF8), ReadOptions)
                       ?? throw new InvalidDataException($"{FileName} is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName} is not valid JSON: {ex.Message}");
        }
        manifest.Validate(packDirectory);
        return manifest;
    }

    /// <summary>id 是否合规。</summary>
    public static bool IsValidId(string id)
        => id.Length is > 0 and <= 64 && (char.IsAsciiLetterLower(id[0]) || char.IsAsciiDigit(id[0]))
           && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    private void Validate(string packDirectory)
    {
        Id = Id.Trim();
        if (!IsValidId(Id)) throw new InvalidDataException("'id' must be 1–64 lowercase letters, digits, '-' or '_', starting with a letter or digit.");
        Name = Name.Trim();
        if (Name.Length is 0 or > 80) throw new InvalidDataException("'name' is required (at most 80 characters).");
        Version = Version.Trim();
        if (Version.Length is 0 or > 32) throw new InvalidDataException("'version' is required (at most 32 characters).");
        Description = Description.Trim();
        if (SkillsDirectory(packDirectory) is null && Skills.Trim().Length > 0)
            throw new InvalidDataException("'skills' must be a folder inside the extension pack.");

        var commandNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ExtensionCommand command in Commands)
        {
            command.Name = command.Name.Trim().TrimStart('/');
            if (!SkillCatalog.IsValidName(command.Name)) throw new InvalidDataException($"Command name '{command.Name}' is invalid.");
            if (!commandNames.Add(command.Name)) throw new InvalidDataException($"Command '{command.Name}' is declared twice.");
            if (command.Prompt.Trim().Length == 0) throw new InvalidDataException($"Command '{command.Name}' has no 'prompt'.");
            if (command.Prompt.Length > MaxPromptLength) throw new InvalidDataException($"Command '{command.Name}' prompt is too long.");
            command.Description = command.Description.Trim();
        }

        var serverNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ExtensionMcpServer server in McpServers)
        {
            server.Name = server.Name.Trim();
            if (!SkillCatalog.IsValidName(server.Name)) throw new InvalidDataException($"MCP server name '{server.Name}' is invalid.");
            if (!serverNames.Add(server.Name)) throw new InvalidDataException($"MCP server '{server.Name}' is declared twice.");
            server.Transport = server.Transport.Trim().ToLowerInvariant();
            switch (server.Transport)
            {
                case "stdio":
                    if (server.Command.Trim().Length == 0) throw new InvalidDataException($"MCP server '{server.Name}' has no 'command'.");
                    if (ResolveWorkingDirectory(packDirectory, server) is null)
                        throw new InvalidDataException($"MCP server '{server.Name}': 'workingDirectory' must stay inside the extension pack.");
                    break;
                case "http":
                    if (!Uri.TryCreate(server.Url.Trim(), UriKind.Absolute, out Uri? url) || url.Scheme is not ("https" or "http"))
                        throw new InvalidDataException($"MCP server '{server.Name}' needs an http(s) 'url'.");
                    break;
                default:
                    throw new InvalidDataException($"MCP server '{server.Name}': 'transport' must be 'stdio' or 'http'.");
            }
            // 配置里的环境变量与请求头是「每行一条」:值里不能有换行,键里不能有 = / :
            foreach ((string key, string value) in server.Env)
            {
                if (key.Trim().Length == 0 || key.Contains('=') || key.Contains('\n') || value.Contains('\n') || value.Contains('\r'))
                    throw new InvalidDataException($"MCP server '{server.Name}': invalid environment variable '{key}'.");
            }
            foreach ((string key, string value) in server.Headers)
            {
                if (key.Trim().Length == 0 || key.Contains(':') || key.Contains('\n') || value.Contains('\n') || value.Contains('\r'))
                    throw new InvalidDataException($"MCP server '{server.Name}': invalid header '{key}'.");
            }
        }
    }

    /// <summary>技能目录的完整路径;没有(或越出扩展包)时为 null。</summary>
    public string? SkillsDirectory(string packDirectory)
    {
        string relative = Skills.Trim().Length > 0 ? Skills.Trim() : "skills";
        string? full = Inside(packDirectory, relative);
        if (full is null) return null;
        return Directory.Exists(full) || Skills.Trim().Length == 0 ? full : null;
    }

    private static string? ResolveWorkingDirectory(string packDirectory, ExtensionMcpServer server)
        => server.WorkingDirectory.Trim().Length == 0 ? Path.GetFullPath(packDirectory) : Inside(packDirectory, server.WorkingDirectory.Trim());

    /// <summary>相对路径解析到扩展包目录内;越界(含绝对路径)返回 null。</summary>
    private static string? Inside(string packDirectory, string relative)
    {
        if (Path.IsPathRooted(relative)) return null;
        string root = Path.GetFullPath(packDirectory);
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        bool inside = string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                      || full.StartsWith(rootWithSeparator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return inside ? full : null;
    }

    /// <summary>
    /// 这个扩展包在<b>指定安装目录</b>下会启动 / 连接的 MCP 服务器,转成与手工配置同构的 <see cref="McpServerConfig" />,
    /// 复用现有的审批、超时、并发与生命周期。id 固定为 <c>ext-包id-名字</c>(重启后连接复用、停用时能对上),
    /// 名字(工具名前缀)为 <c>包id_名字</c>,与手工配置的服务器区分开。
    /// </summary>
    public List<McpServerConfig> ToMcpServers(string packDirectory) =>
    [
        .. McpServers.Select(server => new McpServerConfig
        {
            Id = $"ext-{Id}-{server.Name}",
            Name = $"{Id}_{server.Name}",
            Enabled = true,
            AutoStart = true,
            KeepAlive = true,
            Transport = server.Transport == "http" ? McpTransportType.Http : McpTransportType.Stdio,
            Command = server.Command.Trim(),
            Arguments = JoinArguments(server.Args),
            WorkingDirectory = ResolveWorkingDirectory(packDirectory, server) ?? Path.GetFullPath(packDirectory),
            EnvironmentVariables = string.Join('\n', server.Env.Select(p => $"{p.Key.Trim()}={p.Value.Trim()}")),
            Url = server.Url.Trim(),
            Headers = string.Join('\n', server.Headers.Select(p => $"{p.Key.Trim()}: {p.Value.Trim()}"))
        })
    ];

    /// <summary>
    /// 用户确认过的那部分的指纹:每台 MCP 服务器的传输方式、命令、参数、工作目录(相对)、环境变量、地址与请求头。
    /// 变了就要重新确认;只改技能、命令或说明不影响它。
    /// </summary>
    public string McpFingerprint()
    {
        var payload = McpServers
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new
            {
                s.Name,
                s.Transport,
                Command = s.Command.Trim(),
                s.Args,
                WorkingDirectory = s.WorkingDirectory.Trim(),
                Env = s.Env.OrderBy(p => p.Key, StringComparer.Ordinal).ToList(),
                Url = s.Url.Trim(),
                Headers = s.Headers.OrderBy(p => p.Key, StringComparer.Ordinal).ToList()
            });
        byte[] hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// 参数数组拼成 <see cref="McpConfigParser.SplitArguments" /> 认的单行写法:含空白或引号的(以及空串)加双引号,内部引号写成 <c>""</c>。
    /// 拆回去与原数组逐项相同。
    /// </summary>
    public static string JoinArguments(IEnumerable<string> args)
        => string.Join(' ', args.Select(arg => arg.Length == 0 || arg.Any(c => char.IsWhiteSpace(c) || c == '"')
            ? "\"" + arg.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : arg));

    /// <summary>这个扩展包带不带 MCP 服务器(带的才需要确认启动命令)。</summary>
    [JsonIgnore]
    public bool HasMcp => McpServers.Count > 0;
}
