// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 一个在内存里说 SFTP v3 的服务端。挂在 TestChannelServer 的子系统钩子上。
//
// ⚠️ **只为测试存在，绝不发布。**
//    它没有任何访问控制,而且刻意保留了「可配置地做错事」的开关
//    (比如短读、乱序应答、故意不宣告扩展)。

using System.Buffers;
using System.IO.Pipelines;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Ssh.Tests.TestKit;

/// <summary>内存里的一个文件系统节点。</summary>
public sealed class TestSftpNode
{
    /// <summary>内容（目录与链接为空）。</summary>
    public List<byte> Content { get; } = [];

    /// <summary>是不是目录。</summary>
    public bool IsDirectory { get; init; }

    /// <summary>符号链接指向哪里；不是链接则为 <see langword="null"/>。</summary>
    public string? LinkTarget { get; set; }

    /// <summary>权限位（不含类型）。</summary>
    public uint Permissions { get; set; } = 0b110_100_100;

    /// <summary>最后修改时间。</summary>
    public int ModifyTime { get; set; } = 1_700_000_000;

    /// <summary>最后访问时间。</summary>
    public int AccessTime { get; set; } = 1_700_000_000;

    /// <summary>带上类型位之后的完整权限字段。</summary>
    public uint FullPermissions =>
        Permissions | (LinkTarget is not null
            ? SftpProtocol.FileTypeSymbolicLink
            : IsDirectory ? SftpProtocol.FileTypeDirectory : SftpProtocol.FileTypeRegular);
}

/// <summary>测试 SFTP 服务端的开关。</summary>
public sealed record TestSftpOptions
{
    /// <summary>宣告的版本号。</summary>
    public uint Version { get; init; } = 3;

    /// <summary>宣告哪些扩展。</summary>
    public IReadOnlyList<string> Extensions { get; init; } =
    [
        SftpExtensionNames.PosixRename,
        SftpExtensionNames.HardLink,
        SftpExtensionNames.Fsync,
        SftpExtensionNames.Limits,
    ];

    /// <summary><c>limits@openssh.com</c> 宣告的读写上限。</summary>
    public SftpLimits Limits { get; init; } = new(262_144, 261_120, 261_120, 0);

    /// <summary>工作目录（<c>REALPATH "."</c> 的答案）。</summary>
    public string WorkingDirectory { get; init; } = "/home/joe";

    /// <summary>
    /// 每次 <c>READ</c> 最多只回这么多字节（<c>0</c> = 不限）。
    /// </summary>
    /// <remarks>
    /// 用来构造「短读」：<b><c>READ</c> 返回的数据可以少于请求的长度</b>，
    /// 那不是错误。不会循环读的客户端会在这里丢数据。
    /// </remarks>
    public int ShortReadLimit { get; init; }

    /// <summary>每批 <c>READDIR</c> 最多回这么多项。</summary>
    public int ReadDirBatchSize { get; init; } = 2;

    /// <summary>
    /// 把应答<b>乱序</b>发出去（攒够两条再倒着发）。
    /// </summary>
    /// <remarks>
    /// SFTP 的应答顺序本来就不保证。靠顺序对齐而不是靠 request-id 的客户端，
    /// 会在这里把应答安到错误的请求上 —— <b>而且是静默的错误答案</b>。
    /// </remarks>
    public bool ShuffleResponses { get; init; }

    /// <summary>从第几个 <c>WRITE</c> 起不再应答（模拟中途断开）。</summary>
    public int FailWritesAfter { get; init; } = int.MaxValue;
}

/// <summary>在内存里说 SFTP v3 的测试服务端。</summary>
public sealed class TestSftpServer
{
    private readonly TestSftpOptions _options;
    // string 的默认相等比较器就是序数比较。
    private readonly Dictionary<string, TestSftpNode> _nodes = [];
    private readonly Dictionary<string, HandleState> _handles = [];
    private readonly List<byte[]> _deferred = [];

    private int _nextHandle = 1;
    private int _receivedRequests;
    private int _sentReplies;

    /// <summary>建一个测试 SFTP 服务端。</summary>
    public TestSftpServer(TestSftpOptions? options = null)
    {
        _options = options ?? new TestSftpOptions();
        _nodes[_options.WorkingDirectory] = new TestSftpNode { IsDirectory = true, Permissions = 0b111_101_101 };
        _nodes["/"] = new TestSftpNode { IsDirectory = true, Permissions = 0b111_101_101 };
    }

    /// <summary>当前的虚拟文件系统（测试可以直接读写它来布置场景或断言结果）。</summary>
    public IReadOnlyDictionary<string, TestSftpNode> Nodes => _nodes;

    /// <summary>收到的报文类型，按顺序。</summary>
    public List<SftpMessageType> ReceivedTypes { get; } = [];

    /// <summary>同时打开过的句柄峰值。</summary>
    public int PeakOpenHandles { get; private set; }

    /// <summary>当前还开着的句柄数 —— <b>泄漏检查看这个</b>。</summary>
    public int OpenHandleCount => _handles.Count;

    /// <summary>收到的 <c>WRITE</c> 次数。</summary>
    public int WriteCount { get; private set; }

    /// <summary>倒着发出去过几批应答（每批至少两条）。</summary>
    public int ReversedBatches { get; private set; }

    /// <summary>放一个文件进去。</summary>
    public TestSftpNode AddFile(string path, byte[] content, uint permissions = 0b110_100_100)
    {
        TestSftpNode node = new() { Permissions = permissions };
        node.Content.AddRange(content);
        _nodes[path] = node;
        return node;
    }

    /// <summary>放一个目录进去。</summary>
    public TestSftpNode AddDirectory(string path)
    {
        TestSftpNode node = new() { IsDirectory = true, Permissions = 0b111_101_101 };
        _nodes[path] = node;
        return node;
    }

    /// <summary>放一个符号链接进去。</summary>
    public TestSftpNode AddSymbolicLink(string path, string target)
    {
        TestSftpNode node = new() { LinkTarget = target, Permissions = 0b111_111_111 };
        _nodes[path] = node;
        return node;
    }

    /// <summary>跑服务端循环。</summary>
    public async Task RunAsync(PipeReader input, PipeWriter output, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult read = await input.ReadAsync(cancellationToken);
                ReadOnlySequence<byte> buffer = read.Buffer;
                SequencePosition consumed = buffer.Start;

                List<byte[]> replies = [];
                while (SftpWire.TryReadFrame(ref buffer, out SftpFrame frame))
                {
                    byte[]? reply = Handle(frame);
                    if (reply is not null)
                    {
                        replies.Add(reply);
                    }
                    consumed = buffer.Start;
                }

                input.AdvanceTo(consumed, buffer.End);

                foreach (byte[] reply in Order(replies))
                {
                    await output.WriteAsync(reply, cancellationToken);
                }
                if (replies.Count > 0)
                {
                    await output.FlushAsync(cancellationToken);
                }

                if (read.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 测试收尾。
        }
        finally
        {
            await output.CompleteAsync();
        }
    }

    /// <summary>按开关决定应答顺序。</summary>
    /// <remarks>
    /// 攒够两条就倒着发 —— 足以让「靠顺序对齐」的客户端露馅。
    /// <para>
    /// ⚠️ <b>只在客户端确实还有别的在途请求时才压住。</b>
    /// 无条件攒两条的话，握手（INIT → VERSION 是严格的一问一答）
    /// 会把唯一那条应答永远压在手里 —— 那是<b>挂死</b>，不是在测乱序。
    /// </para>
    /// </remarks>
    private List<byte[]> Order(List<byte[]> replies)
    {
        if (!_options.ShuffleResponses || replies.Count == 0)
        {
            _sentReplies += replies.Count;
            return replies;
        }

        _deferred.AddRange(replies);

        // 还有请求没产出应答 → 可以再等等；否则**必须**现在就发。
        if (_deferred.Count < 2 && _receivedRequests - _sentReplies > _deferred.Count)
        {
            return [];
        }

        List<byte[]> shuffled = [.. Enumerable.Reverse(_deferred)];
        _deferred.Clear();
        _sentReplies += shuffled.Count;
        ReversedBatches += shuffled.Count >= 2 ? 1 : 0;
        return shuffled;
    }

    // ------------------------------------------------------------ 分发

    private byte[]? Handle(SftpFrame frame)
    {
        ReceivedTypes.Add(frame.Type);
        _receivedRequests++;

        if (frame.Type == SftpMessageType.Init)
        {
            return BuildVersion();
        }

        SshDataReader reader = new(frame.Payload);
        uint id = reader.ReadUInt32();
        ReadOnlySequence<byte> rest = frame.Payload.Slice(4);

        return frame.Type switch
        {
            SftpMessageType.RealPath => HandleRealPath(id, rest),
            SftpMessageType.Stat => HandleStat(id, rest, follow: true),
            SftpMessageType.LStat => HandleStat(id, rest, follow: false),
            SftpMessageType.Open => HandleOpen(id, rest),
            SftpMessageType.Close => HandleClose(id, rest),
            SftpMessageType.Read => HandleRead(id, rest),
            SftpMessageType.Write => HandleWrite(id, rest),
            SftpMessageType.FStat => HandleFStat(id, rest),
            SftpMessageType.FSetStat => HandleFSetStat(id, rest),
            SftpMessageType.SetStat => HandleSetStat(id, rest),
            SftpMessageType.OpenDir => HandleOpenDir(id, rest),
            SftpMessageType.ReadDir => HandleReadDir(id, rest),
            SftpMessageType.MkDir => HandleMkDir(id, rest),
            SftpMessageType.RmDir => HandleRemove(id, rest, mustBeDirectory: true),
            SftpMessageType.Remove => HandleRemove(id, rest, mustBeDirectory: false),
            SftpMessageType.Rename => HandleRename(id, rest, overwrite: false),
            SftpMessageType.ReadLink => HandleReadLink(id, rest),
            SftpMessageType.SymLink => HandleSymLink(id, rest),
            SftpMessageType.Extended => HandleExtended(id, rest),
            _ => BuildStatus(id, SftpStatusCode.OperationUnsupported, $"不认识的报文类型 {frame.Type}"),
        };
    }

    // ------------------------------------------------------------ 各操作

    private byte[] BuildVersion()
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(_options.Version);
        foreach (string extension in _options.Extensions)
        {
            writer.WriteUtf8String(extension);
            writer.WriteUtf8String("1");
        }
        return Frame(SftpMessageType.Version, payload.WrittenSpan);
    }

    private byte[] HandleRealPath(uint id, ReadOnlySequence<byte> rest)
    {
        SshDataReader reader = new(rest);
        string path = reader.ReadUtf8String(SftpProtocol.MaxPathLength);
        string resolved = path == "." ? _options.WorkingDirectory : path;
        return BuildName(id, [new SftpNameEntry(resolved, resolved, AttributesOf(_nodes.GetValueOrDefault(resolved)))]);
    }

    private byte[] HandleStat(uint id, ReadOnlySequence<byte> rest, bool follow)
    {
        SshDataReader reader = new(rest);
        string path = reader.ReadUtf8String(SftpProtocol.MaxPathLength);

        TestSftpNode? node = Resolve(path, follow);
        return node is null
            ? BuildStatus(id, SftpStatusCode.NoSuchFile, $"没有 {path}")
            : BuildAttrs(id, AttributesOf(node));
    }

    private byte[] HandleOpen(uint id, ReadOnlySequence<byte> rest)
    {
        SshDataReader reader = new(rest);
        string path = reader.ReadUtf8String(SftpProtocol.MaxPathLength);
        var mode = (SftpOpenMode)reader.ReadUInt32();
        SftpFileAttributes attributes = ReadAttributes(ref reader);

        bool exists = _nodes.TryGetValue(path, out TestSftpNode? node);

        if (exists && (mode & SftpOpenMode.Exclusive) != 0)
        {
            return BuildStatus(id, SftpStatusCode.Failure, "文件已存在");
        }

        if (!exists)
        {
            if ((mode & SftpOpenMode.Create) == 0)
            {
                return BuildStatus(id, SftpStatusCode.NoSuchFile, $"没有 {path}");
            }
            node = new TestSftpNode
            {
                Permissions = attributes.HasPermissions ? attributes.PermissionBits : 0b110_100_100,
            };
            _nodes[path] = node;
        }
        else if ((mode & SftpOpenMode.Truncate) != 0)
        {
            node!.Content.Clear();
        }

        return BuildHandle(id, NewHandle(new HandleState(path, isDirectory: false)));
    }

    private byte[] HandleOpenDir(uint id, ReadOnlySequence<byte> rest)
    {
        SshDataReader reader = new(rest);
        string path = reader.ReadUtf8String(SftpProtocol.MaxPathLength);

        if (!_nodes.TryGetValue(path, out TestSftpNode? node) || !node.IsDirectory)
        {
            return BuildStatus(id, SftpStatusCode.NoSuchFile, $"没有目录 {path}");
        }

        HandleState state = new(path, isDirectory: true)
        {
            Entries = [.. ChildrenOf(path)],
        };
        return BuildHandle(id, NewHandle(state));
    }

    private byte[] HandleReadDir(uint id, ReadOnlySequence<byte> rest)
    {
        if (!TryGetHandle(rest, out HandleState? state))
        {
            return BuildStatus(id, SftpStatusCode.Failure, "无效的句柄");
        }

        if (state.DirectoryCursor >= state.Entries.Count)
        {
            return BuildStatus(id, SftpStatusCode.EndOfFile, "读完了");
        }

        int take = Math.Min(_options.ReadDirBatchSize, state.Entries.Count - state.DirectoryCursor);
        List<SftpNameEntry> batch = [];

        for (int i = 0; i < take; i++)
        {
            string name = state.Entries[state.DirectoryCursor + i];
            string full = state.Path == "/" ? "/" + name : $"{state.Path}/{name}";
            TestSftpNode? node = _nodes.GetValueOrDefault(full);
            batch.Add(new SftpNameEntry(name, $"-rw-r--r-- 1 joe joe 0 Jan 1 00:00 {name}", AttributesOf(node)));
        }

        state.DirectoryCursor += take;
        return BuildName(id, batch);
    }

    private byte[] HandleRead(uint id, ReadOnlySequence<byte> rest)
    {
        if (!TryGetHandle(rest, out HandleState? state))
        {
            return BuildStatus(id, SftpStatusCode.Failure, "无效的句柄");
        }

        SshDataReader reader = new(rest);
        _ = reader.ReadStringAsArray(SftpProtocol.MaxHandleLength);
        ulong offset = reader.ReadUInt64();
        uint length = reader.ReadUInt32();

        TestSftpNode node = _nodes[state.Path];
        if (offset >= (ulong)node.Content.Count)
        {
            return BuildStatus(id, SftpStatusCode.EndOfFile, "读完了");
        }

        int available = node.Content.Count - (int)offset;
        int take = (int)Math.Min(length, (uint)available);

        // 短读：**协议允许返回的数据少于请求的长度**。
        if (_options.ShortReadLimit > 0)
        {
            take = Math.Min(take, _options.ShortReadLimit);
        }

        byte[] data = [.. node.Content.GetRange((int)offset, take)];

        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(id);
        writer.WriteString(data);
        return Frame(SftpMessageType.Data, payload.WrittenSpan);
    }

    private byte[]? HandleWrite(uint id, ReadOnlySequence<byte> rest)
    {
        WriteCount++;

        if (WriteCount > _options.FailWritesAfter)
        {
            return null;   // 干脆不应答 —— 模拟中途断开
        }

        if (!TryGetHandle(rest, out HandleState? state))
        {
            return BuildStatus(id, SftpStatusCode.Failure, "无效的句柄");
        }

        SshDataReader reader = new(rest);
        _ = reader.ReadStringAsArray(SftpProtocol.MaxHandleLength);
        ulong offset = reader.ReadUInt64();
        byte[] data = reader.ReadStringAsArray(SftpProtocol.MaxMessageLength);

        TestSftpNode node = _nodes[state.Path];
        int end = (int)offset + data.Length;

        // 写到超出末尾就补 0 —— 与真实文件系统一样，中间会留空洞。
        while (node.Content.Count < end)
        {
            node.Content.Add(0);
        }

        for (int i = 0; i < data.Length; i++)
        {
            node.Content[(int)offset + i] = data[i];
        }

        return BuildStatus(id, SftpStatusCode.Ok, "");
    }

    private byte[] HandleClose(uint id, ReadOnlySequence<byte> rest)
    {
        SshDataReader reader = new(rest);
        string key = Convert.ToHexString(reader.ReadStringAsArray(SftpProtocol.MaxHandleLength));
        _handles.Remove(key);
        return BuildStatus(id, SftpStatusCode.Ok, "");
    }

    private byte[] HandleFStat(uint id, ReadOnlySequence<byte> rest) =>
        TryGetHandle(rest, out HandleState? state)
            ? BuildAttrs(id, AttributesOf(_nodes.GetValueOrDefault(state.Path)))
            : BuildStatus(id, SftpStatusCode.Failure, "无效的句柄");

    private byte[] HandleFSetStat(uint id, ReadOnlySequence<byte> rest)
    {
        if (!TryGetHandle(rest, out HandleState? state))
        {
            return BuildStatus(id, SftpStatusCode.Failure, "无效的句柄");
        }

        SshDataReader reader = new(rest);
        _ = reader.ReadStringAsArray(SftpProtocol.MaxHandleLength);
        SftpFileAttributes attributes = ReadAttributes(ref reader);
        ApplyAttributes(_nodes[state.Path], attributes);
        return BuildStatus(id, SftpStatusCode.Ok, "");
    }

    private byte[] HandleSetStat(uint id, ReadOnlySequence<byte> rest)
    {
        SshDataReader reader = new(rest);
        string path = reader.ReadUtf8String(SftpProtocol.MaxPathLength);
        SftpFileAttributes attributes = ReadAttributes(ref reader);

        if (!_nodes.TryGetValue(path, out TestSftpNode? node))
        {
            return BuildStatus(id, SftpStatusCode.NoSuchFile, $"没有 {path}");
        }

        ApplyAttributes(node, attributes);
        return BuildStatus(id, SftpStatusCode.Ok, "");
    }

    private byte[] HandleMkDir(uint id, ReadOnlySequence<byte> rest)
    {
        SshDataReader reader = new(rest);
        string path = reader.ReadUtf8String(SftpProtocol.MaxPathLength);

        if (_nodes.ContainsKey(path))
        {
            return BuildStatus(id, SftpStatusCode.Failure, "已存在");
        }

        AddDirectory(path);
        return BuildStatus(id, SftpStatusCode.Ok, "");
    }

    private byte[] HandleRemove(uint id, ReadOnlySequence<byte> rest, bool mustBeDirectory)
    {
        SshDataReader reader = new(rest);
        string path = reader.ReadUtf8String(SftpProtocol.MaxPathLength);

        if (!_nodes.TryGetValue(path, out TestSftpNode? node))
        {
            return BuildStatus(id, SftpStatusCode.NoSuchFile, $"没有 {path}");
        }

        if (mustBeDirectory)
        {
            if (!node.IsDirectory)
            {
                return BuildStatus(id, SftpStatusCode.Failure, "不是目录");
            }
            if (ChildrenOf(path).Any())
            {
                // 「目录非空」在 v3 里也只有码 4 —— 全靠这段文本区分。
                return BuildStatus(id, SftpStatusCode.Failure, "目录非空");
            }
        }

        _nodes.Remove(path);
        return BuildStatus(id, SftpStatusCode.Ok, "");
    }

    private byte[] HandleRename(uint id, ReadOnlySequence<byte> rest, bool overwrite)
    {
        SshDataReader reader = new(rest);
        string oldPath = reader.ReadUtf8String(SftpProtocol.MaxPathLength);
        string newPath = reader.ReadUtf8String(SftpProtocol.MaxPathLength);

        if (!_nodes.TryGetValue(oldPath, out TestSftpNode? node))
        {
            return BuildStatus(id, SftpStatusCode.NoSuchFile, $"没有 {oldPath}");
        }

        if (_nodes.ContainsKey(newPath) && !overwrite)
        {
            // 普通 RENAME 在目标存在时失败 —— 这正是它与 posix-rename 的语义差别。
            return BuildStatus(id, SftpStatusCode.Failure, "目标已存在");
        }

        _nodes.Remove(oldPath);
        _nodes[newPath] = node;
        return BuildStatus(id, SftpStatusCode.Ok, "");
    }

    private byte[] HandleReadLink(uint id, ReadOnlySequence<byte> rest)
    {
        SshDataReader reader = new(rest);
        string path = reader.ReadUtf8String(SftpProtocol.MaxPathLength);

        if (!_nodes.TryGetValue(path, out TestSftpNode? node) || node.LinkTarget is null)
        {
            return BuildStatus(id, SftpStatusCode.NoSuchFile, $"{path} 不是符号链接");
        }

        return BuildName(id, [new SftpNameEntry(node.LinkTarget, node.LinkTarget, SftpFileAttributes.Empty)]);
    }

    /// <summary>建符号链接。</summary>
    /// <remarks>
    /// <b>按 OpenSSH 的顺序解析：先 targetpath，后 linkpath。</b>
    /// 这是事实标准 —— 所有真实服务端都这么解析。
    /// 客户端若按 draft-02 的顺序发，链接就会建在它本想指向的位置上，
    /// 而且<b>不报错</b>，只是建错地方。这个桩要能把那种错误暴露出来。
    /// </remarks>
    private byte[] HandleSymLink(uint id, ReadOnlySequence<byte> rest)
    {
        SshDataReader reader = new(rest);
        string targetPath = reader.ReadUtf8String(SftpProtocol.MaxPathLength);
        string linkPath = reader.ReadUtf8String(SftpProtocol.MaxPathLength);

        if (_nodes.ContainsKey(linkPath))
        {
            return BuildStatus(id, SftpStatusCode.Failure, "已存在");
        }

        AddSymbolicLink(linkPath, targetPath);
        return BuildStatus(id, SftpStatusCode.Ok, "");
    }

    private byte[] HandleExtended(uint id, ReadOnlySequence<byte> rest)
    {
        SshDataReader reader = new(rest);
        string name = reader.ReadUtf8String(SftpProtocol.MaxPathLength);

        if (!_options.Extensions.Contains(name, StringComparer.Ordinal))
        {
            return BuildStatus(id, SftpStatusCode.OperationUnsupported, $"不支持 {name}");
        }

        if (name == SftpExtensionNames.Limits)
        {
            ArrayBufferWriter<byte> payload = new();
            SshDataWriter writer = new(payload);
            writer.WriteUInt32(id);
            writer.WriteUInt64(_options.Limits.MaxPacketLength);
            writer.WriteUInt64(_options.Limits.MaxReadLength);
            writer.WriteUInt64(_options.Limits.MaxWriteLength);
            writer.WriteUInt64(_options.Limits.MaxOpenHandles);
            return Frame(SftpMessageType.ExtendedReply, payload.WrittenSpan);
        }

        if (name == SftpExtensionNames.PosixRename)
        {
            string oldPath = reader.ReadUtf8String(SftpProtocol.MaxPathLength);
            string newPath = reader.ReadUtf8String(SftpProtocol.MaxPathLength);

            if (!_nodes.TryGetValue(oldPath, out TestSftpNode? node))
            {
                return BuildStatus(id, SftpStatusCode.NoSuchFile, $"没有 {oldPath}");
            }

            // 原子覆盖 —— 这正是它与普通 RENAME 的区别。
            _nodes.Remove(oldPath);
            _nodes[newPath] = node;
            return BuildStatus(id, SftpStatusCode.Ok, "");
        }

        if (name == SftpExtensionNames.HardLink)
        {
            string targetPath = reader.ReadUtf8String(SftpProtocol.MaxPathLength);
            string linkPath = reader.ReadUtf8String(SftpProtocol.MaxPathLength);

            if (!_nodes.TryGetValue(targetPath, out TestSftpNode? node))
            {
                return BuildStatus(id, SftpStatusCode.NoSuchFile, $"没有 {targetPath}");
            }

            _nodes[linkPath] = node;   // 硬链接就是同一个节点
            return BuildStatus(id, SftpStatusCode.Ok, "");
        }

        if (name == SftpExtensionNames.Fsync)
        {
            return BuildStatus(id, SftpStatusCode.Ok, "");
        }

        return BuildStatus(id, SftpStatusCode.OperationUnsupported, $"没实现 {name}");
    }

    // ------------------------------------------------------------ 工具

    private sealed class HandleState(string path, bool isDirectory)
    {
        public string Path { get; } = path;

        public bool IsDirectory { get; } = isDirectory;

        public List<string> Entries { get; init; } = [];

        public int DirectoryCursor { get; set; }
    }

    private byte[] NewHandle(HandleState state)
    {
        byte[] handle = System.Text.Encoding.ASCII.GetBytes($"h{_nextHandle++}");
        _handles[Convert.ToHexString(handle)] = state;
        PeakOpenHandles = Math.Max(PeakOpenHandles, _handles.Count);
        return handle;
    }

    private bool TryGetHandle(ReadOnlySequence<byte> rest, out HandleState state)
    {
        SshDataReader reader = new(rest);
        byte[] handle = reader.ReadStringAsArray(SftpProtocol.MaxHandleLength);
        return _handles.TryGetValue(Convert.ToHexString(handle), out state!);
    }

    private IEnumerable<string> ChildrenOf(string directory)
    {
        string prefix = directory == "/" ? "/" : directory + "/";
        foreach (string path in _nodes.Keys)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || path == directory)
            {
                continue;
            }
            string tail = path[prefix.Length..];
            if (!tail.Contains('/', StringComparison.Ordinal) && tail.Length > 0)
            {
                yield return tail;
            }
        }
    }

    private TestSftpNode? Resolve(string path, bool followLinks)
    {
        if (!_nodes.TryGetValue(path, out TestSftpNode? node))
        {
            return null;
        }

        if (!followLinks || node.LinkTarget is null)
        {
            return node;
        }

        // 只跟一层就够测了；断链返回 null。
        return _nodes.GetValueOrDefault(node.LinkTarget);
    }

    private static SftpFileAttributes AttributesOf(TestSftpNode? node)
    {
        if (node is null)
        {
            return SftpFileAttributes.Empty;
        }

        return new SftpFileAttributes
        {
            Flags = SftpAttributeFields.Size | SftpAttributeFields.Permissions | SftpAttributeFields.Times,
            Size = (ulong)node.Content.Count,
            Permissions = node.FullPermissions,
            AccessTime = node.AccessTime,
            ModifyTime = node.ModifyTime,
            Extended = [],
        };
    }

    private static void ApplyAttributes(TestSftpNode node, SftpFileAttributes attributes)
    {
        if (attributes.HasPermissions)
        {
            node.Permissions = attributes.PermissionBits;
        }

        if (attributes.HasTimes)
        {
            node.AccessTime = attributes.AccessTime;
            node.ModifyTime = attributes.ModifyTime;
        }

        if (attributes.HasSize)
        {
            int target = (int)attributes.Size;
            while (node.Content.Count > target)
            {
                node.Content.RemoveAt(node.Content.Count - 1);
            }
            while (node.Content.Count < target)
            {
                node.Content.Add(0);
            }
        }
    }

    private static SftpFileAttributes ReadAttributes(scoped ref SshDataReader reader)
    {
        // 与库内 SftpFileAttributes.Read 同样的结构，但那个是 internal。
        var flags = (SftpAttributeFields)reader.ReadUInt32();

        ulong size = 0;
        uint permissions = 0;
        int accessTime = 0;
        int modifyTime = 0;

        if ((flags & SftpAttributeFields.Size) != 0)
        {
            size = reader.ReadUInt64();
        }
        if ((flags & SftpAttributeFields.UidGid) != 0)
        {
            reader.ReadUInt32();
            reader.ReadUInt32();
        }
        if ((flags & SftpAttributeFields.Permissions) != 0)
        {
            permissions = reader.ReadUInt32();
        }
        if ((flags & SftpAttributeFields.Times) != 0)
        {
            accessTime = (int)reader.ReadUInt32();
            modifyTime = (int)reader.ReadUInt32();
        }

        return new SftpFileAttributes
        {
            Flags = flags,
            Size = size,
            Permissions = permissions,
            AccessTime = accessTime,
            ModifyTime = modifyTime,
            Extended = [],
        };
    }

    private static byte[] Frame(SftpMessageType type, ReadOnlySpan<byte> payload)
    {
        ArrayBufferWriter<byte> buffer = new();
        SftpWireAccess.WriteFrame(buffer, type, payload);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] BuildStatus(uint id, SftpStatusCode code, string message)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(id);
        writer.WriteUInt32((uint)code);
        writer.WriteUtf8String(message);
        writer.WriteUtf8String("");
        return Frame(SftpMessageType.Status, payload.WrittenSpan);
    }

    private static byte[] BuildHandle(uint id, byte[] handle)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(id);
        writer.WriteString(handle);
        return Frame(SftpMessageType.Handle, payload.WrittenSpan);
    }

    private static byte[] BuildAttrs(uint id, SftpFileAttributes attributes)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(id);
        WriteAttributes(ref writer, attributes);
        return Frame(SftpMessageType.Attrs, payload.WrittenSpan);
    }

    private static byte[] BuildName(uint id, List<SftpNameEntry> entries)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(id);
        writer.WriteUInt32((uint)entries.Count);
        foreach (SftpNameEntry entry in entries)
        {
            writer.WriteUtf8String(entry.FileName);
            writer.WriteUtf8String(entry.LongName);
            WriteAttributes(ref writer, entry.Attributes);
        }
        return Frame(SftpMessageType.Name, payload.WrittenSpan);
    }

    private static void WriteAttributes(scoped ref SshDataWriter writer, SftpFileAttributes attributes)
    {
        writer.WriteUInt32((uint)attributes.Flags);
        if (attributes.HasSize)
        {
            writer.WriteUInt64(attributes.Size);
        }
        if (attributes.HasUidGid)
        {
            writer.WriteUInt32(attributes.UserId);
            writer.WriteUInt32(attributes.GroupId);
        }
        if (attributes.HasPermissions)
        {
            writer.WriteUInt32(attributes.Permissions);
        }
        if (attributes.HasTimes)
        {
            writer.WriteUInt32((uint)attributes.AccessTime);
            writer.WriteUInt32((uint)attributes.ModifyTime);
        }
    }
}
