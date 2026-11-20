using System.Globalization;
using System.Text;
using VelaShell.Core.Credentials;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;

namespace VelaShell.Core.Import;

/// <summary>
/// 连接的 CSV 表格(#571):给「两百台设备、不想一条条点」的人用 Excel 批量编辑。
/// </summary>
/// <remarks>
/// <para>
/// 导出的文件就是模板:表头固定、顺序固定,<c>password</c> 列在导出时恒为空 —— 留着这一列是告诉人「这里可以填」。
/// 有 <c>id</c> 列,导出 → Excel 改 → 导回时据此精确对上原来那条,改了主机名也认得出是同一条。
/// </para>
/// <para>
/// 读的时候尽量宽容:分隔符自动认(逗号 / 分号 / 制表符,德语区的 Excel 默认是分号),
/// 表头认常见的别名与中日韩写法,列的顺序随意、不认识的列忽略。
/// </para>
/// </remarks>
public static class SessionCsv
{
    /// <summary>导出与模板的表头,按这个顺序写。</summary>
    public static IReadOnlyList<string> Header { get; } =
    [
        "id", "name", "group", "group_path", "protocol", "host", "port", "username", "auth", "password",
        "private_key", "certificate", "credential", "jump_host", "tags", "notes"
    ];

    private static readonly SessionCsvColumns[] HeaderColumns =
    [
        SessionCsvColumns.Id, SessionCsvColumns.Name, SessionCsvColumns.Group, SessionCsvColumns.GroupPath,
        SessionCsvColumns.Protocol, SessionCsvColumns.Host, SessionCsvColumns.Port, SessionCsvColumns.Username,
        SessionCsvColumns.Auth, SessionCsvColumns.Password, SessionCsvColumns.PrivateKey, SessionCsvColumns.Certificate,
        SessionCsvColumns.Credential, SessionCsvColumns.JumpHost, SessionCsvColumns.Tags, SessionCsvColumns.Notes
    ];

    /// <summary>
    /// 表头别名(已归一化:小写、去空白与 <c>_ - .</c>)。中日韩写法是给自己从零做表的人用的;
    /// 导出永远写 <see cref="Header" /> 里的英文名,不随界面语言变 —— 否则换个语言导出的文件就对不上了。
    /// </summary>
    private static readonly Dictionary<string, SessionCsvColumns> Aliases = BuildAliases();

    /// <summary>
    /// 把导出内容写成 CSV(不含 BOM,编码见 <see cref="SessionFileText.EncodeCsv" />)。机密一律不写。
    /// </summary>
    /// <param name="archive">导出内容。</param>
    /// <returns>CSV 文本,CRLF 换行(Excel 的习惯)。</returns>
    public static string Write(SessionArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var groupRecords = archive.Groups
            .GroupBy(static g => g.Id)
            .ToDictionary(static g => g.Key, static g => g.First());
        var groupTree = new ServerGroupTree(groupRecords.Values.Select(static group => group.ToGroup()));
        var groupPaths = groupRecords.Keys.ToDictionary(static id => id, id => string.Join(" > ",
            groupTree.AncestorsOf(id).Select(static g => g.Name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(">", "\\>", StringComparison.Ordinal))));
        var credentialNames = (archive.SharedCredentials ?? [])
            .GroupBy(static c => c.Id)
            .ToDictionary(static g => g.Key, static g => g.First().Name);
        var byId = archive.Sessions
            .GroupBy(static s => s.Id)
            .ToDictionary(static g => g.Key, static g => g.First());
        var nameCounts = archive.Sessions
            .GroupBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        AppendRow(builder, Header);
        foreach (SessionProfile session in archive.Sessions)
        {
            string credential = session.AuthMethod != AuthMethod.Tailscale && session.CredentialSource is { } reference
                                && reference.TryGetSharedId(out Guid credentialId)
                                && credentialNames.TryGetValue(credentialId, out string? credentialName)
                ? credentialName
                : string.Empty;
            string jumpHost = string.Empty;
            if (session.JumpHostProfileId is { } jumpId && byId.TryGetValue(jumpId, out SessionProfile? jump))
            {
                // 名称在这份文件里唯一就写名称(人看得懂);重名时写 Id,导回时才不会接错跳板。
                jumpHost = nameCounts.GetValueOrDefault(jump.Name) == 1 ? jump.Name : jump.Id.ToString("D");
            }
            AppendRow(builder,
            [
                session.Id.ToString("D"),
                session.Name,
                session.GroupId is { } groupId && groupRecords.TryGetValue(groupId, out SessionArchiveGroup? group)
                    ? group.Name
                    : string.Empty,
                session.GroupId is { } pathGroupId && groupPaths.TryGetValue(pathGroupId, out string? groupPath)
                    ? groupPath
                    : string.Empty,
                FormatProtocol(session),
                session.Host,
                session.Port.ToString(CultureInfo.InvariantCulture),
                session.Username,
                // 引用共享凭据时认证方式由凭据决定,这一列导入时不作数;照样写出来,是为了凭据万一对不上
                // (改了名、本机重名)时导回去的连接还是原来的认证方式,而不是被推断成密码。
                FormatAuth(session.AuthMethod),
                string.Empty,
                // 系统密钥库的钥没有文件:私钥那一列写它的引用(cng:密钥库/钥名),导回来才不会变成一条没有钥的配置。
                session.AuthMethod switch
                {
                    AuthMethod.Tailscale => string.Empty,
                    AuthMethod.KeyStore => session.KeyStoreKey?.ToText() ?? string.Empty,
                    _ => session.PrivateKeyPath ?? string.Empty
                },
                session.AuthMethod == AuthMethod.Tailscale ? string.Empty : session.CertificatePath ?? string.Empty,
                credential,
                jumpHost,
                string.Join(';', session.Tags),
                session.Notes ?? string.Empty
            ]);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 导入模板:表头 + 两行示例。示例用文档专用的 192.0.2.x 地址(RFC 5737)、名字以 <c>example-</c> 开头,
    /// 忘了删就导进来时一眼看得出不是真机器。
    /// </summary>
    /// <returns>CSV 文本。</returns>
    public static string Template()
    {
        var builder = new StringBuilder();
        AppendRow(builder, Header);
        AppendRow(builder, ["", "example-web", "Examples", "Examples", "ssh", "192.0.2.10", "22", "root", "password", "", "", "", "", "", "web;nginx", ""]);
        AppendRow(builder, ["", "example-db", "Examples", "Examples", "ssh", "192.0.2.20", "2222", "admin", "key", "", "~/.ssh/id_ed25519", "", "", "example-web", "db", ""]);
        return builder.ToString();
    }

    /// <summary>
    /// 读入一份 CSV。没有表头或缺 <c>host</c> 列时抛 <see cref="SessionFileFormatException" />;
    /// 单行的问题记在那一行的 <see cref="SessionImportCandidate.Errors" /> / <see cref="SessionImportCandidate.Warnings" /> 里。
    /// </summary>
    /// <param name="fileName">文件名(只用于显示)。</param>
    /// <param name="text">文件内容(已解码)。</param>
    /// <returns>解析结果。</returns>
    public static SessionImportDocument Read(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }
        char delimiter = DetectDelimiter(text);
        List<CsvRecord> records = Tokenize(text, delimiter);
        int headerAt = records.FindIndex(static r => r.Cells.Exists(static c => c.Trim().Length > 0));
        if (headerAt < 0)
        {
            throw new SessionFileFormatException(Strings.Get("SessFile_CsvEmpty"));
        }

        CsvRecord header = records[headerAt];
        var columnAt = new Dictionary<SessionCsvColumns, int>();
        List<string> unknown = [];
        for (int i = 0; i < header.Cells.Count; i++)
        {
            string raw = header.Cells[i].Trim();
            if (raw.Length == 0)
            {
                continue;
            }
            if (Aliases.TryGetValue(NormalizeHeader(raw), out SessionCsvColumns column))
            {
                _ = columnAt.TryAdd(column, i);
            }
            else
            {
                unknown.Add(raw);
            }
        }
        if (!columnAt.ContainsKey(SessionCsvColumns.Host))
        {
            throw new SessionFileFormatException(Strings.Get("SessFile_CsvNoHostColumn"));
        }

        SessionCsvColumns present = columnAt.Keys.Aggregate(SessionCsvColumns.None, static (all, c) => all | c);
        List<SessionImportCandidate> candidates = [];
        for (int r = headerAt + 1; r < records.Count; r++)
        {
            CsvRecord record = records[r];
            if (!record.Cells.Exists(static c => c.Trim().Length > 0))
            {
                continue; // Excel 末尾常带几行空行。
            }
            candidates.Add(ReadRow(candidates.Count, record, columnAt, present));
        }

        var document = new SessionImportDocument
        {
            FileName = fileName,
            Format = SessionFileFormat.Csv,
            Candidates = candidates
        };
        if (unknown.Count > 0)
        {
            document.Warnings.Add(Strings.Format("SessFile_CsvUnknownColumns", string.Join(", ", unknown)));
        }
        return document;
    }

    /// <summary>协议列的写法:ssh / sftp / ftp / ftp-plain / ftps / ftps-implicit / plugin:&lt;id&gt;。</summary>
    /// <param name="profile">连接。</param>
    /// <returns>协议列的值。</returns>
    public static string FormatProtocol(SessionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.ConnectionType switch
        {
            ConnectionType.SFTP => "sftp",
            ConnectionType.FTP => profile.Ftp?.EncryptionMode switch
            {
                FtpEncryptionMode.None => "ftp-plain",
                FtpEncryptionMode.Explicit => "ftps",
                FtpEncryptionMode.Implicit => "ftps-implicit",
                _ => "ftp"
            },
            ConnectionType.Plugin => "plugin:" + profile.PluginProtocolId,
            _ => "ssh"
        };
    }

    private static string FormatAuth(AuthMethod method) => method switch
    {
        AuthMethod.PrivateKey => "key",
        AuthMethod.Certificate => "certificate",
        AuthMethod.Agent => "agent",
        AuthMethod.Tailscale => "tailscale",
        AuthMethod.KeyStore => "keystore",
        _ => "password"
    };

    private static SessionImportCandidate ReadRow(
        int index,
        CsvRecord record,
        Dictionary<SessionCsvColumns, int> columnAt,
        SessionCsvColumns present)
    {
        string Cell(SessionCsvColumns column) =>
            columnAt.TryGetValue(column, out int at) && at < record.Cells.Count
                ? Unescape(record.Cells[at]).Trim()
                : string.Empty;

        List<string> errors = [];
        List<string> warnings = [];
        var profile = new SessionProfile();
        bool hasFileId = false;

        string id = Cell(SessionCsvColumns.Id);
        if (id.Length > 0)
        {
            if (Guid.TryParse(id, out Guid parsedId))
            {
                profile.Id = parsedId;
                hasFileId = true;
            }
            else
            {
                warnings.Add(Strings.Format("SessImport_WarnBadId", id));
            }
        }

        string protocol = Cell(SessionCsvColumns.Protocol);
        if (!TryParseProtocol(protocol, out ConnectionType type, out FtpEncryptionMode? ftpMode, out string? pluginId))
        {
            errors.Add(Strings.Format("SessImport_ErrBadProtocol", protocol));
        }
        profile.ConnectionType = type;
        if (type == ConnectionType.FTP)
        {
            profile.Ftp = new FtpSettings { EncryptionMode = ftpMode ?? FtpEncryptionMode.Auto };
        }
        else if (type == ConnectionType.Plugin)
        {
            profile.PluginProtocolId = pluginId;
        }

        string host = Cell(SessionCsvColumns.Host);
        string username = Cell(SessionCsvColumns.Username);
        string portText = Cell(SessionCsvColumns.Port);
        // 主机一栏里顺手写成 user@host:port 的也认(从命令行抄过来的常是这个样子),
        // 但只在对应那一栏空着时才拆 —— 两处都写了,以专门那一栏为准。
        // 主机一栏里拆出来的用户名 / 端口也算「这一行给了这一列」:覆盖已有连接时只改文件里有的列,
        // 而列是否存在本来按整份文件的表头定 —— 不按行补上,`id,host` 两列的表里写 `admin@10.0.0.9:2200` 就只改了主机。
        SessionCsvColumns rowColumns = present;
        if (username.Length == 0 && host.LastIndexOf('@') is > 0 and var at && at < host.Length - 1)
        {
            username = host[..at];
            host = host[(at + 1)..];
            rowColumns |= SessionCsvColumns.Username;
        }
        if (portText.Length == 0 && TrySplitHostPort(host, out string bareHost, out string embeddedPort))
        {
            host = bareHost;
            portText = embeddedPort;
            rowColumns |= SessionCsvColumns.Port;
        }
        // 不带端口的 [2001:db8::1] 上面不拆,方括号在这里去掉:配置里存裸地址(见 HostAddress)。
        host = HostAddress.Normalize(host);
        if (host.Length == 0)
        {
            errors.Add(Strings.Get("SessImport_ErrNoHost"));
        }
        else if (host.Any(char.IsWhiteSpace))
        {
            errors.Add(Strings.Format("SessImport_ErrHostSpaces", host));
        }
        profile.Host = host;
        profile.Username = username;

        if (portText.Length == 0)
        {
            if (type == ConnectionType.Plugin)
            {
                // 插件协议的默认端口写在插件的声明里,这里不知道;宁可让人填,也不替他猜一个。
                errors.Add(Strings.Get("SessImport_ErrPortRequired"));
            }
            profile.Port = DefaultPort(type, ftpMode);
        }
        else if (int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) && port is >= 1 and <= 65535)
        {
            profile.Port = port;
        }
        else
        {
            errors.Add(Strings.Format("SessImport_ErrBadPort", portText));
        }

        string name = Cell(SessionCsvColumns.Name);
        profile.Name = name.Length > 0 ? name : host;

        string privateKey = Cell(SessionCsvColumns.PrivateKey);
        string certificate = Cell(SessionCsvColumns.Certificate);
        profile.PrivateKeyPath = privateKey.Length > 0 ? privateKey : null;
        profile.CertificatePath = certificate.Length > 0 ? certificate : null;

        string auth = Cell(SessionCsvColumns.Auth);
        AuthMethod method;
        if (auth.Length == 0)
        {
            // 没写认证方式就按给了什么推断:有证书是证书,有私钥是私钥,否则密码。
            method = certificate.Length > 0 ? AuthMethod.Certificate
                : privateKey.Length > 0 ? AuthMethod.PrivateKey
                : AuthMethod.Password;
        }
        else if (!TryParseAuth(auth, out method))
        {
            errors.Add(Strings.Format("SessImport_ErrBadAuth", auth));
        }
        profile.AuthMethod = method;
        if (method == AuthMethod.KeyStore)
        {
            // 私钥那一列是系统密钥库的引用,不是文件路径。
            profile.PrivateKeyPath = null;
            if (SystemKeyReference.TryParse(privateKey, out SystemKeyReference? keyStoreKey))
            {
                profile.KeyStoreKey = keyStoreKey;
            }
            else
            {
                errors.Add(Strings.Format("SessImport_ErrBadKeyStoreKey", privateKey));
            }
        }
        string credential = Cell(SessionCsvColumns.Credential);
        // Tailscale 使用本机身份,credential 列不能覆盖它,也不能绕过协议兼容检查。
        if (method == AuthMethod.Tailscale)
        {
            credential = string.Empty;
        }
        // 其它认证引用共享凭据时,认证方式由凭据决定。
        if (credential.Length == 0
            && !CredentialCompatibility.Supports(type, method, anonymousFtp: false))
        {
            errors.Add(Strings.Get("SessImport_ErrAuthNotSupported"));
        }

        // 密码不去首尾空白:空格可以是密码的一部分,替人修剪掉就再也登不上了。
        string password = columnAt.TryGetValue(SessionCsvColumns.Password, out int passwordAt) && passwordAt < record.Cells.Count
            ? Unescape(record.Cells[passwordAt])
            : string.Empty;
        if (password.Length > 0)
        {
            profile.Password = password;
            profile.RememberPassword = true;
        }
        CredentialMaterial.ClearTailscaleCredentials(profile);

        profile.Tags = [.. Cell(SessionCsvColumns.Tags)
            .Split([';', ',', '|', '，', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)];
        string notes = columnAt.TryGetValue(SessionCsvColumns.Notes, out int notesAt) && notesAt < record.Cells.Count
            ? Unescape(record.Cells[notesAt]).Replace("\r\n", "\n", StringComparison.Ordinal).Trim()
            : string.Empty;
        profile.Notes = notes.Length > 0 ? notes : null;

        string groupPathText = present.HasFlag(SessionCsvColumns.GroupPath) ? Cell(SessionCsvColumns.GroupPath) : string.Empty;
        string? groupName = present.HasFlag(SessionCsvColumns.Group) ? Cell(SessionCsvColumns.Group) : null;
        string[] groupPath = present.HasFlag(SessionCsvColumns.GroupPath)
            ? ParseGroupPath(groupPathText)
            : groupName is { Length: > 0 } literalGroup ? [literalGroup] : [];
        var candidate = new SessionImportCandidate
        {
            Index = index,
            LineNumber = record.Line,
            Profile = profile,
            HasFileId = hasFileId,
            GroupName = groupPath.Length > 0 ? groupPath[^1] : null,
            GroupPath = groupPath,
            JumpHostReference = Cell(SessionCsvColumns.JumpHost) is { Length: > 0 } jump ? jump : null,
            CredentialReference = credential.Length > 0 ? credential : null,
            Columns = rowColumns
        };
        if (groupPath.Length > ServerGroupTree.MaxDepth + 1)
        {
            errors.Add(Strings.Format("SessFile_GroupPathTooDeep", ServerGroupTree.MaxDepth + 1));
        }
        candidate.Errors.AddRange(errors);
        candidate.Warnings.AddRange(warnings);
        return candidate;
    }

    private static string[] ParseGroupPath(string value)
    {
        List<string> segments = [];
        var segment = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] is '>' or '\\')
            {
                segment.Append(value[++i]);
            }
            else if (value[i] == '>')
            {
                AddSegment();
            }
            else
            {
                segment.Append(value[i]);
            }
        }
        AddSegment();
        return [.. segments];

        void AddSegment()
        {
            string name = segment.ToString().Trim();
            if (name.Length > 0)
            {
                segments.Add(name);
            }
            segment.Clear();
        }
    }

    /// <summary>
    /// 解析协议列。空 = SSH。不认识的返回 false(<paramref name="type" /> 仍给 SSH,行会因错误而不导入)。
    /// </summary>
    internal static bool TryParseProtocol(string text, out ConnectionType type, out FtpEncryptionMode? ftpMode, out string? pluginId)
    {
        ftpMode = null;
        pluginId = null;
        type = ConnectionType.SSH;
        string value = text.Trim();
        if (value.Length == 0)
        {
            return true;
        }
        if (value.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase))
        {
            pluginId = value["plugin:".Length..].Trim();
            type = ConnectionType.Plugin;
            return pluginId.Length > 0;
        }
        switch (value.ToLowerInvariant())
        {
            case "ssh":
                return true;
            case "sftp":
                type = ConnectionType.SFTP;
                return true;
            case "ftp":
                type = ConnectionType.FTP;
                ftpMode = FtpEncryptionMode.Auto;
                return true;
            case "ftp-plain" or "ftp-none":
                type = ConnectionType.FTP;
                ftpMode = FtpEncryptionMode.None;
                return true;
            case "ftps" or "ftpes" or "ftps-explicit":
                type = ConnectionType.FTP;
                ftpMode = FtpEncryptionMode.Explicit;
                return true;
            case "ftps-implicit":
                type = ConnectionType.FTP;
                ftpMode = FtpEncryptionMode.Implicit;
                return true;
        }
        // 带点的当作插件协议 id(velashell.telnet 之类),省掉 plugin: 前缀也认。
        if (value.Contains('.', StringComparison.Ordinal) && !value.Any(char.IsWhiteSpace))
        {
            pluginId = value;
            type = ConnectionType.Plugin;
            return true;
        }
        return false;
    }

    internal static bool TryParseAuth(string text, out AuthMethod method)
    {
        switch (NormalizeHeader(text))
        {
            case "password" or "pass" or "pwd":
                method = AuthMethod.Password;
                return true;
            case "key" or "privatekey" or "publickey" or "pubkey":
                method = AuthMethod.PrivateKey;
                return true;
            case "certificate" or "cert":
                method = AuthMethod.Certificate;
                return true;
            case "agent" or "sshagent":
                method = AuthMethod.Agent;
                return true;
            case "tailscale":
                method = AuthMethod.Tailscale;
                return true;
            case "keystore" or "systemkeystore" or "cng":
                method = AuthMethod.KeyStore;
                return true;
            default:
                method = AuthMethod.Password;
                return false;
        }
    }

    private static int DefaultPort(ConnectionType type, FtpEncryptionMode? ftpMode) => type switch
    {
        ConnectionType.FTP => ftpMode == FtpEncryptionMode.Implicit ? FtpSettings.DefaultImplicitPort : FtpSettings.DefaultPort,
        _ => 22
    };

    /// <summary>拆 <c>host:port</c> 与 <c>[v6]:port</c>;裸的 IPv6(多个冒号)不拆。</summary>
    private static bool TrySplitHostPort(string value, out string host, out string port)
    {
        host = value;
        port = string.Empty;
        if (value.StartsWith('[') && value.IndexOf("]:", StringComparison.Ordinal) is > 1 and var close)
        {
            host = value[1..close];
            port = value[(close + 2)..];
            return port.Length > 0 && port.All(char.IsAsciiDigit);
        }
        int colon = value.IndexOf(':');
        if (colon <= 0 || colon != value.LastIndexOf(':') || colon == value.Length - 1)
        {
            return false;
        }
        string tail = value[(colon + 1)..];
        if (!tail.All(char.IsAsciiDigit))
        {
            return false;
        }
        host = value[..colon];
        port = tail;
        return true;
    }

    // ———— 写 ————

    /// <summary>写一行;引号规则与 CSV 注入防护见 <see cref="CsvText.Cell" />(读回来时 <see cref="Unescape" /> 去掉补的单引号,往返不丢字)。</summary>
    private static void AppendRow(StringBuilder builder, IReadOnlyList<string> cells) => CsvText.AppendRow(builder, cells);

    private static string Unescape(string value) =>
        value.Length >= 2 && value[0] == '\'' && CsvText.FormulaLeads.Contains(value[1], StringComparison.Ordinal) ? value[1..] : value;

    // ———— 读 ————

    /// <summary>一条 CSV 记录:起始行号(从 1 起)与各单元格。</summary>
    internal readonly record struct CsvRecord(int Line, List<string> Cells);

    /// <summary>按第一行里出现最多的那个候选字符定分隔符(逗号 / 分号 / 制表符);一个都没有或打平时用逗号。</summary>
    internal static char DetectDelimiter(string text)
    {
        int comma = 0, semicolon = 0, tab = 0;
        bool quoted = false;
        foreach (char ch in text)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (quoted)
            {
                continue;
            }
            if (ch is '\r' or '\n')
            {
                break;
            }
            switch (ch)
            {
                case ',':
                    comma++;
                    break;
                case ';':
                    semicolon++;
                    break;
                case '\t':
                    tab++;
                    break;
            }
        }
        if (semicolon > comma && semicolon >= tab)
        {
            return ';';
        }
        return tab > comma && tab > semicolon ? '\t' : ',';
    }

    /// <summary>RFC 4180 的拆分:双引号包住的单元格里可以有分隔符、换行与成对的双引号。</summary>
    internal static List<CsvRecord> Tokenize(string text, char delimiter)
    {
        var records = new List<CsvRecord>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;
        bool quotedCell = false;
        int line = 1;
        int recordLine = 1;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                    continue;
                }
                if (ch == '\n')
                {
                    line++;
                }
                cell.Append(ch);
                continue;
            }
            if (ch == '"' && !quotedCell && IsBlank(cell))
            {
                // 「a, "b"」这种分隔符后面带空格再起引号的也认:引号之前的空白丢掉。
                cell.Clear();
                inQuotes = true;
                quotedCell = true;
                continue;
            }
            if (ch == delimiter)
            {
                cells.Add(cell.ToString());
                cell.Clear();
                quotedCell = false;
                continue;
            }
            if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                cells.Add(cell.ToString());
                cell.Clear();
                quotedCell = false;
                records.Add(new CsvRecord(recordLine, cells));
                cells = [];
                line++;
                recordLine = line;
                continue;
            }
            cell.Append(ch);
        }
        if (cell.Length > 0 || cells.Count > 0 || quotedCell)
        {
            cells.Add(cell.ToString());
            records.Add(new CsvRecord(recordLine, cells));
        }
        return records;
    }

    private static bool IsBlank(StringBuilder builder)
    {
        for (int i = 0; i < builder.Length; i++)
        {
            if (!char.IsWhiteSpace(builder[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static string NormalizeHeader(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (char.IsWhiteSpace(ch) || ch is '_' or '-' or '.' or '﻿')
            {
                continue;
            }
            builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }

    private static Dictionary<string, SessionCsvColumns> BuildAliases()
    {
        var aliases = new Dictionary<string, SessionCsvColumns>(StringComparer.Ordinal);
        void Add(SessionCsvColumns column, params string[] names)
        {
            foreach (string name in names)
            {
                aliases[NormalizeHeader(name)] = column;
            }
        }
        for (int i = 0; i < Header.Count; i++)
        {
            Add(HeaderColumns[i], Header[i]);
        }
        foreach ((SessionCsvColumns column, string[] names) in SessionCsvHeaderAliases.All)
        {
            Add(column, names);
        }
        return aliases;
    }
}
