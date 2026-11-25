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
internal sealed class TestSftpNode
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
    public uint ModifyTime { get; set; } = 1_700_000_000;

    /// <summary>最后访问时间。</summary>
    public uint AccessTime { get; set; } = 1_700_000_000;

    /// <summary>带上类型位之后的完整权限字段。</summary>
    public uint FullPermissions =>
        Permissions | (LinkTarget is not null
            ? SftpProtocol.FileTypeSymbolicLink
            : IsDirectory ? SftpProtocol.FileTypeDirectory : SftpProtocol.FileTypeRegular);
}

/// <summary>测试 SFTP 服务端的开关。</summary>
internal sealed record TestSftpOptions
{
    /// <summary>宣告的版本号。</summary>
    public uint Version { get; init; } = 3;

    /// <summary>按 draft-02 的顺序解析 <c>SSH_FXP_SYMLINK</c>（先 linkpath、后 targetpath）—— 与 OpenSSH 相反的那种服务端。</summary>
    public bool SymLinkInDraftOrder { get; init; }

    /// <summary>
    /// 本测试桩不认识的扩展怎么答（扩展名、请求载荷 → 应答载荷）：返回 <see langword="null"/> 回 <c>STATUS OK</c>，
    /// 抛 <see cref="InvalidOperationException"/> 回 <c>FAILURE</c>。扩展名还得出现在 <see cref="Extensions"/> 里。
    /// </summary>
    public Func<string, byte[], byte[]?>? VendorExtension { get; init; }

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

    /// <summary><c>users-groups-by-id@openssh.com</c> 认得的 uid → 用户名。</summary>
    public IReadOnlyDictionary<uint, string> UserNames { get; init; } = new Dictionary<uint, string> { [0] = "root", [1000] = "joe" };

    /// <summary><c>users-groups-by-id@openssh.com</c> 认得的 gid → 组名。</summary>
    public IReadOnlyDictionary<uint, string> GroupNames { get; init; } = new Dictionary<uint, string> { [0] = "root", [100] = "users" };

    /// <summary><c>users-groups-by-id@openssh.com</c> 的应答少回一个名字（坏服务端）。</summary>
    public bool DropOneIdName { get; init; }

    /// <summary><c>WRITE</c> 把文件的修改时间改成「现在」（真实文件系统就是这样）。</summary>
    public bool WriteTouchesModifyTime { get; init; }

    /// <summary>
    /// 各用户的家目录（<c>home-directory</c> 与 <c>expand-path@openssh.com</c> 的 <c>~用户名</c> 查它；登录用户的是 <see cref="WorkingDirectory"/>）。
    /// </summary>
    public IReadOnlyDictionary<string, string> HomeDirectories { get; init; } = new Dictionary<string, string> { ["alice"] = "/srv/alice" };

    /// <summary><c>statvfs@openssh.com</c> 回的 11 个值（要在 <see cref="Extensions"/> 里列上它才回）。</summary>
    public ulong[] StatVfs { get; init; } = [4096, 1024, 1_000_000, 400_000, 300_000, 65_536, 60_000, 59_000, 0xABCD, 0x1, 255];

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

    /// <summary>这个偏移上的 <c>READ</c> 只回一半（只这一次）—— 预读队伍建起来之后中间冒出一个短读。</summary>
    public long? ShortReadAtOffset { get; init; }

    /// <summary>每批 <c>READDIR</c> 最多回这么多项。</summary>
    public int ReadDirBatchSize { get; init; } = 2;

    /// <summary>收到 <c>INIT</c> 不回 <c>VERSION</c> —— 模拟登录 shell 的启动文件卡住、sftp-server 一直没起来。</summary>
    public bool NeverAnswerInit { get; init; }

    /// <summary>在任何 SFTP 报文之前先往 stdout 写这段文字 —— 模拟启动文件（.bashrc 之类）往 stdout 输出了东西。</summary>
    public string? StdoutBanner { get; init; }

    /// <summary><c>READDIR</c> 永远回空的一批（count = 0），既不给项也不回 EOF。</summary>
    public bool EmptyReadDirBatches { get; init; }

    /// <summary><c>READDIR</c> 的属性里不带权限位（<c>STAT</c> / <c>LSTAT</c> 照常带）—— 模拟列目录图省事的服务端。</summary>
    public bool OmitPermissionsInReadDir { get; init; }

    /// <summary>每个目录的列表末尾再塞进这些名字（不对应任何节点）—— 模拟一个回 <c>../x</c>、<c>a/b</c>、空名字的服务端。</summary>
    public IReadOnlyList<string> ExtraDirectoryEntryNames { get; init; } = [];

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

    /// <summary>每个 <c>WRITE</c> 都以这个状态拒绝（模拟磁盘满、配额、权限），数据不写。</summary>
    public SftpStatusCode? RejectWritesWith { get; init; }

    /// <summary>
    /// 这个偏移上的 <c>READ</c> 先不答，等收到一个偏移更靠后的 <c>READ</c> 才放行。
    /// </summary>
    /// <remarks>
    /// 用来证明顺序读确实在预读：一次只发一个 <c>READ</c> 的客户端会停在这里等，
    /// 预读的客户端则会先发出后面的请求、把它放出来（<see cref="TestSftpServer.PipelinedReadObserved"/>）。
    /// </remarks>
    public long? HoldReadReplyAtOffset { get; init; }

    /// <summary><c>OPEN</c> / <c>OPENDIR</c> 的应答等这么久才发（句柄在收到请求时就已经打开）。</summary>
    /// <remarks>模拟慢盘、网络文件系统：客户端在等句柄时取消了，句柄晚到。</remarks>
    public TimeSpan DelayOpenReplies { get; init; }

    /// <summary><c>STAT</c> / <c>LSTAT</c> 的应答等这么久才发。</summary>
    /// <remarks>让并发请求在测试服务端形成一批，稳定地覆盖应答乱序。</remarks>
    public TimeSpan DelayStatReplies { get; init; }

    /// <summary>对 <c>OPEN</c> 回一个 DATA —— 模拟一个应答类型对不上请求的服务端。</summary>
    public bool WrongOpenReply { get; init; }

    /// <summary>对 <c>WRITE</c> 回一个 HANDLE（数据照样写下）—— 模拟一个应答类型对不上请求的服务端。</summary>
    public bool WrongWriteReply { get; init; }

    /// <summary><c>CLOSE</c> 的应答等这么久才发（句柄在收到请求时就已经关掉）。</summary>
    public TimeSpan DelayCloseReplies { get; init; }

    /// <summary><c>WRITE</c> 的应答等这么久才发（数据在收到请求时就已经写下）—— 让一次写在 <c>WriteAsync</c> 返回之后还在路上。</summary>
    public TimeSpan DelayWriteReplies { get; init; }

    /// <summary>
    /// 扣住每个 <c>WRITE</c> 的应答（数据在收到请求时就已经写下），直到测试调
    /// <see cref="TestSftpServer.ReleaseHeldWrites"/> 按到达顺序放行。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="DelayWriteReplies"/> 不同：扣住期间照常读后面的请求，「确认什么时候到」完全由测试决定，
    /// 不靠计时 —— 用来断言进度只跟着确认走、不跟着发出去的字节走。
    /// </remarks>
    public bool HoldWriteReplies { get; init; }

    /// <summary>设了就对每个 <c>CLOSE</c> 回这个失败码（句柄照样关掉）—— 模拟到关闭时才报出来的写入错误。</summary>
    public SftpStatusCode? FailCloseWith { get; init; }

    /// <summary><c>FSTAT</c> 回一个被截断的 ATTRS：宣告带 size，却没有那 8 个字节。</summary>
    public bool MalformedFStat { get; init; }

    /// <summary>替代 FSTAT 的属性集，用于只返回部分字段的服务端。</summary>
    public SftpFileAttributes? FStatAttributes { get; init; }

    /// <summary>服务端拒绝 FSTAT，但仍允许 READ。</summary>
    public bool RejectFStat { get; init; }

    /// <summary><c>READLINK</c> 回一个被截断的 NAME：宣告 1 项，却只有半个文件名。</summary>
    public bool MalformedReadLink { get; init; }

    /// <summary><c>STAT</c> / <c>LSTAT</c> 原样回这个类型与 request-id 之后的这些字节（畸形应答的用例用）。</summary>
    public (SftpMessageType Type, byte[] Body)? StatReplyOverride { get; init; }
}

/// <summary>在内存里说 SFTP v3 的测试服务端。</summary>
internal sealed class TestSftpServer
{
    private readonly TestSftpOptions _options;
    // string 的默认相等比较器就是序数比较。
    private readonly Dictionary<string, TestSftpNode> _nodes = [];
    private readonly Dictionary<string, HandleState> _handles = [];
    private readonly List<byte[]> _deferred = [];

    private int _nextHandle = 1;
    private int _receivedRequests;
    private int _sentReplies;

    /// <summary>被扣住的那条 READ 应答（见 <see cref="TestSftpOptions.HoldReadReplyAtOffset"/>）。</summary>
    private (long Offset, byte[] Reply)? _heldRead;
    private bool _holdUsed;

    /// <summary>这一批处理里放行的、之前扣住的应答。</summary>
    private readonly List<byte[]> _released = [];

    /// <summary>扣住的 READ 被一个更靠后的 READ 放了出来 —— 客户端确实在预读。</summary>
    public bool PipelinedReadObserved { get; private set; }

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

    /// <summary>收到过几个 <c>READDIR</c>（可以在服务端还在跑的时候读，不必枚举 <see cref="ReceivedTypes"/>）。</summary>
    public int ReadDirRequests => Volatile.Read(ref _readDirRequests);

    private int _readDirRequests;

    /// <summary>同时打开过的句柄峰值。</summary>
    public int PeakOpenHandles { get; private set; }

    /// <summary>当前还开着的句柄数 —— <b>泄漏检查看这个</b>。</summary>
    public int OpenHandleCount => _handles.Count;

    /// <summary>收到过几个 <c>copy-data</c> 请求（验分段）。</summary>
    public int CopyDataRequests { get; private set; }

    /// <summary>收到的 <c>WRITE</c> 次数。</summary>
    public int WriteCount { get; private set; }

    /// <summary>收到过的最长一个 <c>WRITE</c> 的数据长度。</summary>
    public int LargestWrite { get; private set; }

    /// <summary>收到过的最长一个 <c>READ</c> 请求的长度。</summary>
    public long LargestReadRequest { get; private set; }

    public int EofReadRequests { get; private set; }

    /// <summary>同一个文件里被 <c>READ</c> 回了不止一遍的字节数（预读作废后重读的浪费）。</summary>
    public long RereadBytes { get; private set; }

    /// <summary>每个文件已经回过的字节区间（算 <see cref="RereadBytes"/> 用）。</summary>
    private readonly Dictionary<string, List<(long Start, long End)>> _servedReads = [];

    private bool _shortReadAtOffsetUsed;

    /// <summary>最近一次 <c>OPEN</c> 里带来的 ATTRS。</summary>
    public SftpFileAttributes? LastOpenAttributes { get; private set; }

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

    /// <summary>让这个 sftp-server 退出（模拟崩溃，或服务端按 <c>ChannelTimeout</c> 关掉闲置通道）：输出收尾，通道随之 EOF + CLOSE。</summary>
    public void Exit()
    {
        Volatile.Write(ref _exited, true);
        Volatile.Read(ref _input)?.CancelPendingRead();
    }

    private bool _exited;
    private PipeReader? _input;

    /// <summary>扣住的 <c>WRITE</c> 应答，按到达顺序（见 <see cref="TestSftpOptions.HoldWriteReplies"/>）。</summary>
    private readonly Queue<byte[]> _heldWrites = new();

    /// <summary>测试已经放行、等服务端循环发出去的 <c>WRITE</c> 应答。</summary>
    private readonly List<byte[]> _releasedWrites = [];

    /// <summary>当前扣着几条 <c>WRITE</c> 应答。</summary>
    public int HeldWriteReplies
    {
        get
        {
            lock (_heldWrites)
            {
                return _heldWrites.Count;
            }
        }
    }

    /// <summary>按到达顺序放行至多 <paramref name="count"/> 条扣住的 <c>WRITE</c> 应答（省略 = 全部）。</summary>
    /// <remarks>
    /// 应答仍由服务端循环发出（输出只有它一个写者）：这里只是挪到待发队列，再叫醒正在等请求的循环 ——
    /// 与 <see cref="Exit"/> 同一个办法。
    /// </remarks>
    /// <returns>实际放行了几条。</returns>
    public int ReleaseHeldWrites(int count = int.MaxValue)
    {
        int released = 0;
        lock (_heldWrites)
        {
            while (released < count && _heldWrites.TryDequeue(out byte[]? reply))
            {
                _releasedWrites.Add(reply);
                released++;
            }
        }
        if (released > 0)
        {
            Volatile.Read(ref _input)?.CancelPendingRead();
        }
        return released;
    }

    /// <summary>跑服务端循环。</summary>
    public async Task RunAsync(PipeReader input, PipeWriter output, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _input, input);
        try
        {
            if (_options.StdoutBanner is { } banner)
            {
                await output.WriteAsync(System.Text.Encoding.ASCII.GetBytes(banner), cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult read = await input.ReadAsync(cancellationToken);
                if (Volatile.Read(ref _exited))
                {
                    break;
                }

                ReadOnlySequence<byte> buffer = read.Buffer;
                SequencePosition consumed = buffer.Start;

                List<byte[]> replies = [];
                bool delayReplies = false;
                bool delayStatReplies = false;
                bool delayWriteReplies = false;
                bool delayCloseReplies = false;
                while (SftpWire.TryReadFrame(ref buffer, out SftpFrame frame))
                {
                    delayCloseReplies |= frame.Type is SftpMessageType.Close;
                    delayReplies |= frame.Type is SftpMessageType.Open or SftpMessageType.OpenDir;
                    delayStatReplies |= frame.Type is SftpMessageType.Stat or SftpMessageType.LStat;
                    delayWriteReplies |= frame.Type is SftpMessageType.Write;
                    byte[]? reply = Handle(frame);
                    replies.AddRange(_released);
                    _released.Clear();
                    if (reply is not null && _options.HoldWriteReplies && frame.Type is SftpMessageType.Write)
                    {
                        lock (_heldWrites)
                        {
                            _heldWrites.Enqueue(reply);
                        }
                    }
                    else if (reply is not null)
                    {
                        replies.Add(reply);
                    }
                    consumed = buffer.Start;
                }

                input.AdvanceTo(consumed, buffer.End);

                if (delayReplies && _options.DelayOpenReplies > TimeSpan.Zero)
                {
                    await Task.Delay(_options.DelayOpenReplies, cancellationToken);
                }
                if (delayStatReplies && _options.DelayStatReplies > TimeSpan.Zero)
                {
                    await Task.Delay(_options.DelayStatReplies, cancellationToken);
                }
                if (delayWriteReplies && _options.DelayWriteReplies > TimeSpan.Zero)
                {
                    await Task.Delay(_options.DelayWriteReplies, cancellationToken);
                }
                if (delayCloseReplies && _options.DelayCloseReplies > TimeSpan.Zero)
                {
                    await Task.Delay(_options.DelayCloseReplies, cancellationToken);
                }

                lock (_heldWrites)
                {
                    // 测试放行的确认排在这一批前面：它们对应的 WRITE 比这一批里的任何请求都早到。
                    replies.InsertRange(0, _releasedWrites);
                    _releasedWrites.Clear();
                }

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
        if (frame.Type == SftpMessageType.ReadDir)
        {
            Interlocked.Increment(ref _readDirRequests);
        }
        _receivedRequests++;

        if (frame.Type == SftpMessageType.Init)
        {
            return _options.NeverAnswerInit ? null : BuildVersion();
        }

        SshDataReader reader = new(frame.Payload);
        uint id = reader.ReadUInt32();
        ReadOnlySequence<byte> rest = frame.Payload.Slice(4);

        return frame.Type switch
        {
            SftpMessageType.RealPath => HandleRealPath(id, rest),
            SftpMessageType.Stat or SftpMessageType.LStat when _options.StatReplyOverride is { } replaced =>
                Frame(replaced.Type, [.. BigEndian(id), .. replaced.Body]),
            SftpMessageType.Stat => HandleStat(id, rest, follow: true),
            SftpMessageType.LStat => HandleStat(id, rest, follow: false),
            SftpMessageType.Open when _options.WrongOpenReply => BuildData(id, [1, 2, 3, 4]),
            SftpMessageType.Write when _options.WrongWriteReply => WrongWriteReply(id, rest),
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
        string path = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);
        string resolved = path == "." ? _options.WorkingDirectory : path;
        return BuildName(id, [new SftpNameEntry(resolved, resolved, AttributesOf(_nodes.GetValueOrDefault(resolved)))]);
    }

    private byte[] HandleStat(uint id, ReadOnlySequence<byte> rest, bool follow)
    {
        SshDataReader reader = new(rest);
        string path = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);

        TestSftpNode? node = Resolve(path, follow);
        return node is null
            ? BuildStatus(id, SftpStatusCode.NoSuchFile, $"没有 {path}")
            : BuildAttrs(id, AttributesOf(node));
    }

    private byte[] HandleOpen(uint id, ReadOnlySequence<byte> rest)
    {
        SshDataReader reader = new(rest);
        string path = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);
        var mode = (SftpOpenModes)reader.ReadUInt32();
        SftpFileAttributes attributes = ReadAttributes(ref reader);
        LastOpenAttributes = attributes;

        bool exists = _nodes.TryGetValue(path, out TestSftpNode? node);

        if (exists && (mode & SftpOpenModes.Exclusive) != 0)
        {
            return BuildStatus(id, SftpStatusCode.Failure, "文件已存在");
        }

        if (!exists)
        {
            if ((mode & SftpOpenModes.Create) == 0)
            {
                return BuildStatus(id, SftpStatusCode.NoSuchFile, $"没有 {path}");
            }
            node = new TestSftpNode
            {
                Permissions = attributes.HasPermissions ? attributes.PermissionBits : 0b110_100_100,
            };
            _nodes[path] = node;
        }
        else if ((mode & SftpOpenModes.Truncate) != 0)
        {
            node!.Content.Clear();
        }

        return BuildHandle(id, NewHandle(new HandleState(path, isDirectory: false)));
    }

    private byte[] HandleOpenDir(uint id, ReadOnlySequence<byte> rest)
    {
        SshDataReader reader = new(rest);
        string path = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);

        if (!_nodes.TryGetValue(path, out TestSftpNode? node) || !node.IsDirectory)
        {
            return BuildStatus(id, SftpStatusCode.NoSuchFile, $"没有目录 {path}");
        }

        HandleState state = new(path, isDirectory: true)
        {
            Entries = [.. ChildrenOf(path), .. _options.ExtraDirectoryEntryNames],
        };
        return BuildHandle(id, NewHandle(state));
    }

    private byte[] HandleReadDir(uint id, ReadOnlySequence<byte> rest)
    {
        if (!TryGetHandle(rest, out HandleState? state))
        {
            return BuildStatus(id, SftpStatusCode.Failure, "无效的句柄");
        }

        if (_options.EmptyReadDirBatches)
        {
            return BuildName(id, []);
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
            SftpFileAttributes listed = AttributesOf(node);
            if (_options.OmitPermissionsInReadDir)
            {
                listed = listed with { Flags = listed.Flags & ~SftpAttributeFields.Permissions, Permissions = 0 };
            }
            batch.Add(new SftpNameEntry(name, $"-rw-r--r-- 1 joe joe 0 Jan 1 00:00 {name}", listed));
        }

        state.DirectoryCursor += take;
        return BuildName(id, batch);
    }

    private byte[]? HandleRead(uint id, ReadOnlySequence<byte> rest)
    {
        byte[]? reply = BuildReadReply(id, rest, out long offset);

        // 扣住的那一条：收到偏移更靠后的 READ 才放行（见 TestSftpOptions.HoldReadReplyAtOffset）。
        if (_heldRead is { } held && offset > held.Offset)
        {
            _released.Add(held.Reply);
            _heldRead = null;
            PipelinedReadObserved = true;
        }

        if (!_holdUsed && _options.HoldReadReplyAtOffset == offset)
        {
            _holdUsed = true;
            _heldRead = (offset, reply);
            return null;
        }

        return reply;
    }

    private byte[] BuildReadReply(uint id, ReadOnlySequence<byte> rest, out long requestedOffset)
    {
        requestedOffset = -1;
        if (!TryGetHandle(rest, out HandleState? state))
        {
            return BuildStatus(id, SftpStatusCode.Failure, "无效的句柄");
        }

        SshDataReader reader = new(rest);
        _ = reader.ReadStringAsArray(SftpProtocol.MaxHandleLength);
        ulong offset = reader.ReadUInt64();
        uint length = reader.ReadUInt32();
        requestedOffset = (long)offset;
        LargestReadRequest = Math.Max(LargestReadRequest, length);

        TestSftpNode node = _nodes[state.Path];
        if (offset >= (ulong)node.Content.Count)
        {
            EofReadRequests++;
            return BuildStatus(id, SftpStatusCode.EndOfFile, "读完了");
        }

        int available = node.Content.Count - (int)offset;
        int take = (int)Math.Min(length, (uint)available);

        // 短读：**协议允许返回的数据少于请求的长度**。
        if (_options.ShortReadLimit > 0)
        {
            take = Math.Min(take, _options.ShortReadLimit);
        }
        if (!_shortReadAtOffsetUsed && _options.ShortReadAtOffset == (long)offset)
        {
            _shortReadAtOffsetUsed = true;
            take = Math.Max(1, take / 2);
        }

        List<(long Start, long End)> served = _servedReads.TryGetValue(state.Path, out List<(long Start, long End)>? list)
            ? list
            : _servedReads[state.Path] = [];
        long start = (long)offset, end = start + take;
        RereadBytes += served.Sum(range => Math.Max(0, Math.Min(end, range.End) - Math.Max(start, range.Start)));
        served.Add((start, end));

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

        if (_options.RejectWritesWith is { } rejection)
        {
            return BuildStatus(id, rejection, "No space left on device");
        }

        if (!TryGetHandle(rest, out HandleState? state))
        {
            return BuildStatus(id, SftpStatusCode.Failure, "无效的句柄");
        }

        SshDataReader reader = new(rest);
        _ = reader.ReadStringAsArray(SftpProtocol.MaxHandleLength);
        ulong offset = reader.ReadUInt64();
        byte[] data = reader.ReadStringAsArray(SftpProtocol.MaxMessageLength);
        LargestWrite = Math.Max(LargestWrite, data.Length);

        TestSftpNode node = _nodes[state.Path];
        if (_options.WriteTouchesModifyTime)
        {
            node.ModifyTime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
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
        return _options.FailCloseWith is { } failure
            ? BuildStatus(id, failure, "关闭时回写失败：配额已满")
            : BuildStatus(id, SftpStatusCode.Ok, "");
    }

    private byte[] HandleFStat(uint id, ReadOnlySequence<byte> rest)
    {
        if (!TryGetHandle(rest, out HandleState? state))
        {
            return BuildStatus(id, SftpStatusCode.Failure, "无效的句柄");
        }

        if (_options.MalformedFStat)
        {
            ArrayBufferWriter<byte> payload = new();
            SshDataWriter writer = new(payload);
            writer.WriteUInt32(id);
            writer.WriteUInt32((uint)SftpAttributeFields.Size);   // 宣告带 size，后面却什么都没有
            return Frame(SftpMessageType.Attrs, payload.WrittenSpan);
        }

        return _options.RejectFStat
            ? BuildStatus(id, SftpStatusCode.PermissionDenied, "不允许 FSTAT")
            : BuildAttrs(id, _options.FStatAttributes ?? AttributesOf(_nodes.GetValueOrDefault(state.Path)));
    }

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
        string path = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);
        SftpFileAttributes attributes = ReadAttributes(ref reader);

        // SETSTAT 跟随符号链接（改到的是目标）；不跟随的是 lsetstat@openssh.com。
        if (Resolve(path, followLinks: true) is not { } node)
        {
            return BuildStatus(id, SftpStatusCode.NoSuchFile, $"没有 {path}");
        }

        ApplyAttributes(node, attributes);
        return BuildStatus(id, SftpStatusCode.Ok, "");
    }

    private byte[] HandleMkDir(uint id, ReadOnlySequence<byte> rest)
    {
        SshDataReader reader = new(rest);
        string path = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);

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
        string path = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);

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
        string oldPath = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);
        string newPath = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);

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
        string path = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);

        if (!_nodes.TryGetValue(path, out TestSftpNode? node) || node.LinkTarget is null)
        {
            return BuildStatus(id, SftpStatusCode.NoSuchFile, $"{path} 不是符号链接");
        }

        if (_options.MalformedReadLink)
        {
            ArrayBufferWriter<byte> payload = new();
            SshDataWriter writer = new(payload);
            writer.WriteUInt32(id);
            writer.WriteUInt32(1);
            writer.WriteUInt32(100);                     // 文件名声称 100 字节
            writer.WriteRaw("only-a-bit"u8);             // 实际只有这么点
            return Frame(SftpMessageType.Name, payload.WrittenSpan);
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
        string targetPath = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);
        string linkPath = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);
        if (_options.SymLinkInDraftOrder)
        {
            (targetPath, linkPath) = (linkPath, targetPath);
        }

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
            string oldPath = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);
            string newPath = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);

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
            string targetPath = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);
            string linkPath = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);

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

        if (name == SftpExtensionNames.UsersGroupsById)
        {
            byte[] uids = reader.ReadStringAsArray(SftpProtocol.MaxMessageLength);
            byte[] gids = reader.ReadStringAsArray(SftpProtocol.MaxMessageLength);

            ArrayBufferWriter<byte> users = new();
            SshDataWriter usersWriter = new(users);
            for (int i = 0; i + 4 <= uids.Length - (_options.DropOneIdName ? 4 : 0); i += 4)
            {
                usersWriter.WriteUtf8String(_options.UserNames.GetValueOrDefault(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(uids.AsSpan(i))) ?? "");
            }
            ArrayBufferWriter<byte> groups = new();
            SshDataWriter groupsWriter = new(groups);
            for (int i = 0; i + 4 <= gids.Length; i += 4)
            {
                groupsWriter.WriteUtf8String(_options.GroupNames.GetValueOrDefault(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(gids.AsSpan(i))) ?? "");
            }

            ArrayBufferWriter<byte> payload = new();
            SshDataWriter writer = new(payload);
            writer.WriteUInt32(id);
            writer.WriteString(users.WrittenSpan);
            writer.WriteString(groups.WrittenSpan);
            return Frame(SftpMessageType.ExtendedReply, payload.WrittenSpan);
        }

        if (name == SftpExtensionNames.CopyData)
        {
            CopyDataRequests++;
            if (!TryGetHandle(rest.Slice(reader.Consumed), out HandleState? from))
            {
                return BuildStatus(id, SftpStatusCode.Failure, "无效的读句柄");
            }
            _ = reader.ReadStringAsArray(SftpProtocol.MaxHandleLength);
            ulong readOffset = reader.ReadUInt64();
            ulong readLength = reader.ReadUInt64();
            if (!TryGetHandle(rest.Slice(reader.Consumed), out HandleState? to))
            {
                return BuildStatus(id, SftpStatusCode.Failure, "无效的写句柄");
            }
            _ = reader.ReadStringAsArray(SftpProtocol.MaxHandleLength);
            ulong writeOffset = reader.ReadUInt64();

            List<byte> source = _nodes[from.Path].Content;
            List<byte> target = _nodes[to.Path].Content;
            int start = (int)Math.Min(readOffset, (ulong)source.Count);
            int count = readLength == 0 ? source.Count - start : (int)Math.Min(readLength, (ulong)(source.Count - start));
            byte[] chunk = [.. source.GetRange(start, count)];
            while (target.Count < (int)writeOffset + count)
            {
                target.Add(0);
            }
            for (int i = 0; i < count; i++)
            {
                target[(int)writeOffset + i] = chunk[i];
            }
            return BuildStatus(id, SftpStatusCode.Ok, "");
        }

        if (name == SftpExtensionNames.LSetStat)
        {
            string path = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);
            SftpFileAttributes attributes = ReadAttributes(ref reader);
            if (!_nodes.TryGetValue(path, out TestSftpNode? link))
            {
                return BuildStatus(id, SftpStatusCode.NoSuchFile, $"没有 {path}");
            }
            ApplyAttributes(link, attributes);   // 链接自身，不跟随
            return BuildStatus(id, SftpStatusCode.Ok, "");
        }

        if (name == SftpExtensionNames.HomeDirectory)
        {
            string user = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);
            return _options.HomeDirectories.TryGetValue(user, out string? home)
                ? BuildName(id, [new SftpNameEntry(home, home, AttributesOf(null))])
                : BuildStatus(id, SftpStatusCode.Failure, $"没有用户 {user}");
        }

        if (name == SftpExtensionNames.ExpandPath)
        {
            string path = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);
            int slash = path.IndexOf('/', StringComparison.Ordinal);
            string user = slash < 0 ? path[1..] : path[1..slash];
            string? home = user.Length == 0 ? _options.WorkingDirectory : _options.HomeDirectories.GetValueOrDefault(user);
            if (!path.StartsWith('~') || home is null)
            {
                return BuildStatus(id, SftpStatusCode.NoSuchFile, $"展开不了 {path}");
            }
            string expanded = slash < 0 ? home : home.TrimEnd('/') + path[slash..];
            return BuildName(id, [new SftpNameEntry(expanded, expanded, AttributesOf(_nodes.GetValueOrDefault(expanded)))]);
        }

        if (name == SftpExtensionNames.StatVfs)
        {
            string path = SftpNameCodec.Utf8.Read(ref reader, SftpProtocol.MaxPathLength);
            if (!_nodes.ContainsKey(path))
            {
                return BuildStatus(id, SftpStatusCode.NoSuchFile, $"没有 {path}");
            }

            ArrayBufferWriter<byte> payload = new();
            SshDataWriter writer = new(payload);
            writer.WriteUInt32(id);
            foreach (ulong value in _options.StatVfs)
            {
                writer.WriteUInt64(value);
            }
            return Frame(SftpMessageType.ExtendedReply, payload.WrittenSpan);
        }

        if (_options.VendorExtension is { } vendor)
        {
            byte[] request = reader.ReadRemaining().ToArray();
            byte[]? reply;
            try
            {
                reply = vendor(name, request);
            }
            catch (InvalidOperationException ex)
            {
                return BuildStatus(id, SftpStatusCode.Failure, ex.Message);
            }
            if (reply is null)
            {
                return BuildStatus(id, SftpStatusCode.Ok, "");
            }
            ArrayBufferWriter<byte> payload = new();
            SshDataWriter writer = new(payload);
            writer.WriteUInt32(id);
            writer.WriteRaw(reply);
            return Frame(SftpMessageType.ExtendedReply, payload.WrittenSpan);
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
        uint accessTime = 0;
        uint modifyTime = 0;

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
            accessTime = reader.ReadUInt32();
            modifyTime = reader.ReadUInt32();
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

    private static byte[] BigEndian(uint value)
    {
        byte[] bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
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

    private byte[] WrongWriteReply(uint id, ReadOnlySequence<byte> rest)
    {
        _ = HandleWrite(id, rest);   // 数据照样写下，只是应答的类型不对
        return BuildHandle(id, [9, 9, 9, 9]);
    }

    private static byte[] BuildData(uint id, byte[] data)
    {
        ArrayBufferWriter<byte> payload = new();
        SshDataWriter writer = new(payload);
        writer.WriteUInt32(id);
        writer.WriteString(data);
        return Frame(SftpMessageType.Data, payload.WrittenSpan);
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
            SftpNameCodec.Utf8.Write(ref writer, entry.Name);
            SftpNameCodec.Utf8.Write(ref writer, entry.LongName);
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
            writer.WriteUInt32(attributes.AccessTime);
            writer.WriteUInt32(attributes.ModifyTime);
        }
    }
}
