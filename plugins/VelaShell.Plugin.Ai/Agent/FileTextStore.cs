using System.Text;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Ai.Agent;

/// <summary>读一个文件全文的结果。</summary>
/// <param name="Text">全文(UTF-8 解码、去掉 BOM);失败或文件不存在时为 null。</param>
/// <param name="Exists">文件是否存在。</param>
/// <param name="HasBom">原文件是否带 UTF-8 BOM(写回时照原样保留)。</param>
/// <param name="Error">失败原因(给人看);成功为 null。</param>
internal sealed record FileTextReadResult(string? Text, bool Exists, bool HasBom, string? Error);

/// <summary>
/// 按 <see cref="FileChange.Location" />(<c>local:&lt;workspaceId&gt;</c> / <c>remote:&lt;sessionId&gt;</c>)读写文本文件,
/// 给差异文档的「保存 / 载入完整文件」用。这是用户自己的操作,不走 Agent 审批。
/// </summary>
/// <remarks>
/// <para>
/// 本地:每次都用 <see cref="LocalPathScope" /> 重新解析路径(工作区可能已经改了、被移除了,或者路径里的目录被换成了链接),
/// 写入方式与 <c>AgentToolbox</c> 的本地写一致 —— UTF-8,原文件带 BOM 就保留 BOM(见 <see cref="WriteLocalTextAsync" />,两边共用)。
/// </para>
/// <para>
/// 远端:会话必须还连着(否则给出「会话已断开」),走 <c>IRemoteFsApi</c> 的整份读写;原文件带 BOM 同样保留。
/// </para>
/// </remarks>
internal sealed class FileTextStore(IPluginContext context, Func<IReadOnlyList<LocalWorkspace>> workspaces, int maxBytes)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>读失败、写失败时给人看的几句固定话术的键(由调用方按 <c>Loc</c> 翻译)。</summary>
    internal const string ErrorSessionGone = "DiffSaveSessionGone";
    internal const string ErrorPathRejected = "DiffSavePathRejected";
    internal const string ErrorNotText = "DiffSaveNotText";
    internal const string ErrorReviewConflict = "DiffReviewConflict";
    internal const string ErrorReviewUnavailable = "DiffReviewUnavailable";

    /// <summary>读当前全文。</summary>
    internal async Task<FileTextReadResult> ReadAsync(string location, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            if (TryLocal(location, path, write: false, out LocalPath local, out bool isLocal))
            {
                if (!File.Exists(local.FullPath)) return new FileTextReadResult(null, false, false, null);
                if (new FileInfo(local.FullPath).Length > maxBytes) return new FileTextReadResult(null, true, false, ErrorNotText);
                byte[] bytes = await File.ReadAllBytesAsync(local.FullPath, cancellationToken).ConfigureAwait(false);
                return Decode(bytes);
            }
            if (isLocal) return new FileTextReadResult(null, false, false, ErrorPathRejected);
            if (RemoteSession(location) is not { } sessionId) return new FileTextReadResult(null, false, false, ErrorPathRejected);
            if (!await IsConnectedAsync(sessionId, cancellationToken).ConfigureAwait(false))
            {
                return new FileTextReadResult(null, false, false, ErrorSessionGone);
            }
            if (await context.RemoteFs.StatAsync(sessionId, path, cancellationToken).ConfigureAwait(false) is not { } entry)
            {
                return new FileTextReadResult(null, false, false, null);
            }
            if (entry.IsDirectory || entry.Size > maxBytes) return new FileTextReadResult(null, true, false, ErrorNotText);
            byte[] remote = await context.RemoteFs.ReadAllBytesAsync(sessionId, path, maxBytes, cancellationToken).ConfigureAwait(false);
            return Decode(remote);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FileTextReadResult(null, false, false, ex.Message);
        }
    }

    /// <summary>写回全文;成功返回 null,失败返回原因(固定话术的键或异常消息)。</summary>
    /// <param name="location">位置。</param>
    /// <param name="path">路径。</param>
    /// <param name="text">新全文。</param>
    /// <param name="withBom">写成带 BOM 的 UTF-8(照原文件)。</param>
    /// <param name="cancellationToken">取消。</param>
    internal async Task<string?> WriteAsync(string location, string path, string text, bool withBom, CancellationToken cancellationToken = default)
    {
        try
        {
            if (TryLocal(location, path, write: true, out LocalPath local, out bool isLocal))
            {
                await WriteLocalTextAsync(local.FullPath, text, withBom, cancellationToken).ConfigureAwait(false);
                return null;
            }
            if (isLocal) return ErrorPathRejected;
            if (RemoteSession(location) is not { } sessionId) return ErrorPathRejected;
            if (!await IsConnectedAsync(sessionId, cancellationToken).ConfigureAwait(false)) return ErrorSessionGone;
            byte[] body = Utf8NoBom.GetBytes(text);
            if (withBom) body = [.. Encoding.UTF8.GetPreamble(), .. body];
            await context.RemoteFs.WriteAllBytesAsync(sessionId, path, body, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>当前状态仍匹配变更后的快照时，还原操作前的文件内容和路径。</summary>
    internal async Task<string?> RestoreAsync(FileChange change)
    {
        bool complete = change.Kind switch
        {
            FileChangeKind.Created => change.After is not null,
            FileChangeKind.Deleted => change.Before is not null,
            FileChangeKind.Modified => change.Before is not null && change.After is not null,
            FileChangeKind.Renamed => change.Before is not null && change.After is not null && !string.IsNullOrWhiteSpace(change.OldPath),
            _ => false
        };
        if (!complete || change.Before?.Contains('\0') == true || change.After?.Contains('\0') == true) return ErrorReviewUnavailable;
        try
        {
            string target = change.OldPath ?? change.Path;
            if (TryLocal(change.Location, change.Path, write: true, out LocalPath current, out bool isLocal))
            {
                if (!TryLocal(change.Location, target, write: true, out LocalPath original, out _)) return ErrorPathRejected;
                return await RestoreLocalAsync(change, current.FullPath, original.FullPath).ConfigureAwait(false);
            }
            if (isLocal || RemoteSession(change.Location) is not { } session) return ErrorPathRejected;
            if (!await IsConnectedAsync(session, default).ConfigureAwait(false)) return ErrorSessionGone;
            return await RestoreRemoteAsync(change, session, target).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task<string?> RestoreLocalAsync(FileChange change, string path, string target)
    {
        if (change.OldPath is not null && Path.Exists(target)) return ErrorReviewConflict;
        if (change.Kind == FileChangeKind.Deleted)
        {
            if (Path.Exists(path)) return ErrorReviewConflict;
            byte[] bytes = Encode(change.Before!, change.BeforeHasBom ?? false);
            using FileStream created = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await created.WriteAsync(bytes).ConfigureAwait(false);
            return null;
        }
        if (!File.Exists(path) || Directory.Exists(path)) return ErrorReviewConflict;
        bool remove = change.Kind == FileChangeKind.Created;
        using FileStream stream = new(path, FileMode.Open, remove ? FileAccess.Read : FileAccess.ReadWrite,
            remove || change.OldPath is not null ? FileShare.Delete : FileShare.None);
        if (stream.Length > maxBytes) return ErrorReviewConflict;
        byte[] current = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(current).ConfigureAwait(false);
        FileTextReadResult read = Decode(current);
        if (read.Text != change.After) return ErrorReviewConflict;
        if (remove)
        {
            File.Delete(path);
            return null;
        }
        bool bom = change.BeforeHasBom ?? read.HasBom;
        byte[]? replacement = change.Before != change.After || bom != read.HasBom ? Encode(change.Before!, bom) : null;
        if (change.OldPath is not null) File.Move(path, target);
        if (replacement is not null)
        {
            stream.Position = 0;
            await stream.WriteAsync(replacement).ConfigureAwait(false);
            stream.SetLength(replacement.Length);
        }
        return null;
    }

    private async Task<string?> RestoreRemoteAsync(FileChange change, string session, string target)
    {
        var fs = context.RemoteFs;
        string path = change.Path;
        var current = await fs.StatAsync(session, path).ConfigureAwait(false);
        if (change.Kind == FileChangeKind.Deleted)
        {
            if (current is not null || (change.OldPath is not null && await fs.StatAsync(session, target).ConfigureAwait(false) is not null))
                return ErrorReviewConflict;
            await fs.WriteAllBytesAsync(session, target, Encode(change.Before!, change.BeforeHasBom ?? false)).ConfigureAwait(false);
            return null;
        }
        if (current is null || current.IsDirectory || current.IsSymbolicLink || current.Size > maxBytes) return ErrorReviewConflict;
        byte[] bytes = await fs.ReadAllBytesAsync(session, path, maxBytes).ConfigureAwait(false);
        FileTextReadResult read = Decode(bytes);
        if (read.Text != change.After) return ErrorReviewConflict;
        var latest = await fs.StatAsync(session, path).ConfigureAwait(false);
        if (latest is null || latest.IsSymbolicLink || latest.IsDirectory || latest.Size != bytes.Length) return ErrorReviewConflict;
        if (change.Kind == FileChangeKind.Created)
        {
            await fs.DeleteAsync(session, path).ConfigureAwait(false);
            return null;
        }
        bool bom = change.BeforeHasBom ?? read.HasBom;
        byte[]? replacement = change.Before != change.After || bom != read.HasBom ? Encode(change.Before!, bom) : null;
        if (change.OldPath is not null)
        {
            if (await fs.StatAsync(session, target).ConfigureAwait(false) is not null) return ErrorReviewConflict;
            await fs.RenameAsync(session, path, target).ConfigureAwait(false);
        }
        if (replacement is not null) await fs.WriteAllBytesAsync(session, target, replacement).ConfigureAwait(false);
        return null;
    }

    private static byte[] Encode(string text, bool withBom)
    {
        byte[] body = Utf8Strict.GetBytes(text);
        return withBom ? [.. Encoding.UTF8.GetPreamble(), .. body] : body;
    }

    /// <summary>
    /// 把 <paramref name="text" /> 的换行统一成 <paramref name="reference" /> 里占多数的那种(CRLF 或 LF)。
    /// 参照为空或没有换行时原样返回。
    /// </summary>
    /// <param name="text">要写出的全文。</param>
    /// <param name="reference">原文件内容。</param>
    internal static string MatchLineEndings(string text, string? reference)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(reference)) return text;
        int crlf = 0, lf = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            if (reference[i] != '\n') continue;
            if (i > 0 && reference[i - 1] == '\r') crlf++;
            else lf++;
        }
        if (crlf == 0 && lf == 0) return text;
        // 只认 \r\n 与 \n:ReplaceLineEndings 还会改写换页符、U+2028 等,那是改内容
        string lfOnly = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return crlf > lf ? lfOnly.Replace("\n", "\r\n", StringComparison.Ordinal) : lfOnly;
    }

    /// <summary>
    /// 本地写文本:UTF-8,<paramref name="withBom" /> 时带 BOM。<c>AgentToolbox</c> 的本地写与差异文档的保存共用这一处,
    /// 两边写出来的字节一致。
    /// </summary>
    internal static Task WriteLocalTextAsync(string fullPath, string content, bool withBom, CancellationToken cancellationToken)
        => File.WriteAllTextAsync(fullPath, content, withBom ? Encoding.UTF8 : Utf8NoBom, cancellationToken);

    /// <summary>本地文件开头是不是 UTF-8 BOM。</summary>
    internal static bool LocalFileHasUtf8Bom(string fullPath)
    {
        try
        {
            using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[3];
            return stream.Read(head) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static FileTextReadResult Decode(byte[] bytes)
    {
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        try
        {
            int offset = bom ? 3 : 0;
            return new FileTextReadResult(Utf8Strict.GetString(bytes, offset, bytes.Length - offset), true, bom, null);
        }
        catch (DecoderFallbackException)
        {
            return new FileTextReadResult(null, true, bom, ErrorNotText);
        }
    }

    private bool TryLocal(string location, string path, bool write, out LocalPath local, out bool isLocal)
    {
        local = default;
        isLocal = location.StartsWith("local:", StringComparison.Ordinal);
        if (!isLocal) return false;
        string workspaceId = location["local:".Length..];
        return new LocalPathScope(workspaces()).TryResolve(workspaceId, path, write, out local, out _);
    }

    private static string? RemoteSession(string location)
        => location.StartsWith("remote:", StringComparison.Ordinal) && location.Length > "remote:".Length
            ? location["remote:".Length..]
            : null;

    private async Task<bool> IsConnectedAsync(string sessionId, CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionInfo> sessions = await context.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
        return sessions.Any(s => s.SessionId == sessionId && s.State == SessionState.Connected);
    }
}
