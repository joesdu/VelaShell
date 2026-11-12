using System.Globalization;
using VelaShell.Core.Models;
using VelaShell.Core.Notifications;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;

namespace VelaShell.Services;

/// <summary>重传一份外部编辑草稿时要用到的远端:由已经连着(或刚连上)那台服务器的文件面板提供。</summary>
public interface IRemoteEditDraftTarget
{
    /// <summary>远端路径此刻的样子;不存在时为 null。后端给不出时抛 <see cref="NotSupportedException" />。</summary>
    /// <param name="remotePath">远端完整路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<RemoteFileInfo?> StatAsync(string remotePath, CancellationToken cancellationToken);

    /// <summary>远端目录在不在。</summary>
    /// <param name="remotePath">远端目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> DirectoryExistsAsync(string remotePath, CancellationToken cancellationToken);

    /// <summary>把本地文件传到远端路径(与外部编辑的自动回传同一条路:进传输浮窗)。</summary>
    /// <param name="localPath">本地文件。</param>
    /// <param name="remotePath">远端完整路径。</param>
    Task UploadAsync(string localPath, string remotePath);
}

/// <summary>按一个文件面板的远程文件服务与会话提供 <see cref="IRemoteEditDraftTarget" />;上传交给面板自己那一条(进传输浮窗)。</summary>
/// <param name="service">远程文件服务。</param>
/// <param name="sessionId">面板所在的远程会话。</param>
/// <param name="upload">面板的上传(本地路径、远端路径)。</param>
public sealed class RemoteFileServiceDraftTarget(ISftpService service, Guid sessionId, Func<string, string, Task> upload) : IRemoteEditDraftTarget
{
    /// <inheritdoc />
    public async Task<RemoteFileInfo?> StatAsync(string remotePath, CancellationToken cancellationToken)
    {
        // 先判存在:不存在时 GetFileInfoAsync 抛的异常各个后端不一样,拿异常做分支容易把「连接断了」当成「文件不在」
        if (!await service.ExistsAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(true))
        {
            return null;
        }
        return await service.GetFileInfoAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public Task<bool> DirectoryExistsAsync(string remotePath, CancellationToken cancellationToken) =>
        service.ExistsAsync(sessionId, remotePath, cancellationToken);

    /// <inheritdoc />
    public Task UploadAsync(string localPath, string remotePath) => upload(localPath, remotePath);
}

/// <summary>从消息中心重传一份外部编辑草稿的结果。</summary>
public enum RemoteEditDraftReuploadResult
{
    /// <summary>传上去了,草稿删了、消息撤了。</summary>
    Uploaded,

    /// <summary>传上去了,但传的过程中本机副本又被改过:草稿与消息都留着。</summary>
    UploadedButChanged,

    /// <summary>这份草稿已经不在了(处理完删掉了、已经不是草稿):消息撤了。</summary>
    DraftGone,

    /// <summary>草稿没记着连接配置或远端路径(快速连接、旧版本留下的):没法直接重传。</summary>
    NoProfile,

    /// <summary>记着的连接配置已经删除了。</summary>
    ProfileMissing,

    /// <summary>没连上(连接失败、登录框取消):原因由连接那一路报过了。</summary>
    NotConnected,

    /// <summary>远端文件所在的目录已经不在了。</summary>
    RemoteDirectoryMissing,

    /// <summary>远端那个路径现在是一个目录。</summary>
    RemoteIsDirectory,

    /// <summary>远端在这之后被改过(或不在了、说不准),用户没让覆盖;也包括没有能问的人。</summary>
    Declined,

    /// <summary>查远端或上传失败。</summary>
    Failed,

    /// <summary>这一份正在重传(连点了两下)。</summary>
    Busy
}

/// <summary>重传结果与要说给用户听的话(null = 不必再说,比如连接那一路已经报过了)。</summary>
/// <param name="Result">结果。</param>
/// <param name="Message">提示文案。</param>
public sealed record RemoteEditDraftReuploadOutcome(RemoteEditDraftReuploadResult Result, string? Message);

/// <summary>
/// 从消息中心重传一份外部编辑草稿(<see cref="NotificationSources.ReuploadRemoteEditDraftCommandId" />):
/// 按草稿标记里记着的连接配置找已经连着的面板(没有就照常打开一个文件标签,缺凭据照样弹登录框),
/// 先看远端在这之后有没有被改过(改过、不在了、说不准都先问),再传到记着的远端路径;成功后删草稿、撤消息,失败时两样都留着并说明原因。
/// </summary>
/// <remarks>
/// <para>
/// 「有没有被改过」比的是标记里记着的远端样子(本地副本最近一次与远端一致时远端文件的长度与修改时间,
/// 见 <see cref="RemoteEditMarker.RemoteLength" />)与远端此刻的样子。对得上才直接传;对不上、标记里没记(旧版本的标记、当时查不到)、
/// 后端查不了,一律先问 —— 宁可多问一次,不静默盖掉别人的改动。没有能问的人(<c>confirm</c> 为 null)时按不覆盖处理。
/// </para>
/// <para>
/// 跑在 UI 线程上(打开文件标签、弹框、传输浮窗都要在那里),全程不写 <c>ConfigureAwait(false)</c>;扫草稿目录这一下放到线程池。
/// </para>
/// </remarks>
public sealed class RemoteEditDraftReuploader
{
    private readonly Func<SessionProfile, Task<IRemoteEditDraftTarget?>> _connect;
    private readonly Func<string, string, string, Task<bool>>? _confirm;
    private readonly Func<Guid, Task<SessionProfile?>> _findProfile;
    private readonly HashSet<string> _inFlight = [with(StringComparer.Ordinal)];
    private readonly Func<IReadOnlyCollection<string>> _activeDirectories;
    private readonly INotificationCenter? _notifications;
    private readonly string _root;

    /// <summary>构造。</summary>
    /// <param name="root">外部编辑副本的根目录(<see cref="RemoteEditSessionManager.TempRoot" />;测试里换成临时目录)。</param>
    /// <param name="findProfile">按 Id 取连接配置;已删除时为 null。</param>
    /// <param name="connect">找已经连着这条配置的面板,没有就照常打开一个文件标签;没连上时为 null。</param>
    /// <param name="confirm">问用户(标题、正文、确认键文案),返回是否继续;null = 没有能问的人,一律不覆盖。</param>
    /// <param name="notifications">消息中心:成功或草稿已不在时撤掉那一条。</param>
    /// <param name="activeDirectories">此刻正开着的编辑会话的子目录;null 时取 <see cref="RemoteEditSessionManager.ActiveSessions" />。</param>
    public RemoteEditDraftReuploader(
        string root,
        Func<Guid, Task<SessionProfile?>> findProfile,
        Func<SessionProfile, Task<IRemoteEditDraftTarget?>> connect,
        Func<string, string, string, Task<bool>>? confirm,
        INotificationCenter? notifications,
        Func<IReadOnlyCollection<string>>? activeDirectories = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _findProfile = findProfile ?? throw new ArgumentNullException(nameof(findProfile));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _confirm = confirm;
        _notifications = notifications;
        _activeDirectories = activeDirectories
                             ?? (static () => [.. RemoteEditSessionManager.ActiveSessions.Select(static s => Path.GetDirectoryName(s.LocalPath)!)]);
    }

    /// <summary>重传 <paramref name="key" /> 那一份草稿(草稿子目录的名字,即消息动作的参数)。</summary>
    /// <param name="key">草稿子目录的名字。</param>
    /// <param name="cancellationToken">取消令牌(取消查远端;上传本身在传输浮窗里取消)。</param>
    public async Task<RemoteEditDraftReuploadOutcome> ReuploadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_inFlight.Add(key))
        {
            return new(RemoteEditDraftReuploadResult.Busy, null);
        }
        try
        {
            return await ReuploadCoreAsync(key, cancellationToken);
        }
        finally
        {
            _inFlight.Remove(key);
        }
    }

    private async Task<RemoteEditDraftReuploadOutcome> ReuploadCoreAsync(string key, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> active = _activeDirectories();
        (RemoteEditDraft? draft, RemoteEditMarker? marker) = await Task.Run(() =>
        {
            RemoteEditDraft? found = RemoteEditDrafts.FindDraft(_root, key, active);
            return (found, found is null ? null : RemoteEditDrafts.Read(found.Directory));
        }, cancellationToken);
        if (draft is null)
        {
            await WithdrawAsync(key);
            return new(RemoteEditDraftReuploadResult.DraftGone, Strings.Get("RemoteEditDraft_Gone"));
        }
        if (!draft.CanReupload || marker is null || draft.ProfileId is not { } profileId)
        {
            return new(RemoteEditDraftReuploadResult.NoProfile, Strings.Format("RemoteEditDraft_NoProfile", draft.FileName, draft.LocalPath));
        }
        string remotePath = draft.RemotePath!;
        string where = draft.Server is { Length: > 0 } server ? $"{server}:{remotePath}" : remotePath;
        if (await _findProfile(profileId) is not { } profile)
        {
            return new(RemoteEditDraftReuploadResult.ProfileMissing,
                Strings.Format("RemoteEditDraft_ProfileMissing", draft.FileName, draft.LocalPath));
        }
        if (await _connect(profile) is not { } target)
        {
            // 失败原因(认证失败、指纹被拒、登录框取消)已经由打开连接的那一路报过了
            return new(RemoteEditDraftReuploadResult.NotConnected, null);
        }

        RemoteFileInfo? remote;
        bool remoteKnown = true;
        try
        {
            remote = await target.StatAsync(remotePath, cancellationToken);
        }
        catch (NotSupportedException)
        {
            // 后端给不出远端的样子:说不准有没有被改过,下面照「说不准」先问
            remote = null;
            remoteKnown = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed(draft, where, ex);
        }

        if (remoteKnown && remote is null)
        {
            string parent = RemotePath.Parent(remotePath);
            bool parentExists;
            try
            {
                parentExists = await target.DirectoryExistsAsync(parent, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failed(draft, where, ex);
            }
            if (!parentExists)
            {
                return new(RemoteEditDraftReuploadResult.RemoteDirectoryMissing,
                    Strings.Format("RemoteEditDraft_RemoteDirMissing", parent, draft.FileName));
            }
            if (!await ConfirmAsync(Strings.Get("RemoteEditDraft_RemoteMissingTitle"),
                    Strings.Format("RemoteEditDraft_RemoteMissingBody", where), Strings.Get("RemoteEditDraft_Recreate")))
            {
                return new(RemoteEditDraftReuploadResult.Declined, null);
            }
        }
        else if (remote is { IsDirectory: true })
        {
            return new(RemoteEditDraftReuploadResult.RemoteIsDirectory, Strings.Format("RemoteEditDraft_RemoteIsDirectory", where));
        }
        else if (!MatchesBaseline(marker, remote))
        {
            // 远端在上次与这份草稿一致之后被改过(或者说不准):不静默覆盖
            string body = remote is null
                ? Strings.Format("RemoteEditDraft_ConflictNoInfoBody", where)
                : Strings.Format(
                    marker.RemoteLength is null || marker.RemoteModifiedUtc is null ? "RemoteEditDraft_ConflictUnknownBody" : "RemoteEditDraft_ConflictBody",
                    where, RemoteFileInfoViewModel.FormatSize(remote.Size),
                    remote.LastModified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            if (!await ConfirmAsync(Strings.Get("RemoteEditDraft_ConflictTitle"), body, Strings.Get("Sftp_ConflictOverwrite")))
            {
                return new(RemoteEditDraftReuploadResult.Declined, null);
            }
        }

        // 问的这会儿草稿可能已经被删掉了(在文件夹里处理掉了):没有可传的
        (long Length, DateTime LastWriteUtc)? before = SnapshotOf(draft.LocalPath);
        if (before is null)
        {
            await WithdrawAsync(key);
            return new(RemoteEditDraftReuploadResult.DraftGone, Strings.Get("RemoteEditDraft_Gone"));
        }
        try
        {
            await target.UploadAsync(draft.LocalPath, remotePath);
        }
        catch (OperationCanceledException)
        {
            // 在传输浮窗里点了取消:草稿与消息照旧留着
            return new(RemoteEditDraftReuploadResult.Declined, null);
        }
        catch (Exception ex)
        {
            return Failed(draft, where, ex);
        }
        RemoteEditLog.Write("reupload", $"ok {remotePath} <- {draft.LocalPath}");

        if (SnapshotOf(draft.LocalPath) != before)
        {
            // 传的过程中又存了一次:远端拿到的是之前那一份,这份副本还不能删
            return new(RemoteEditDraftReuploadResult.UploadedButChanged,
                Strings.Format("RemoteEditDraft_ChangedDuringUpload", draft.FileName, where, draft.LocalPath));
        }
        Settle(draft, marker, before.Value);
        await WithdrawAsync(key);
        return new(RemoteEditDraftReuploadResult.Uploaded, Strings.Format("RemoteEditDraft_Uploaded", draft.FileName, where));
    }

    /// <summary>远端此刻的样子是不是标记里记着的那一份(长度与修改时间都对得上)。标记里没记、后端查不了的都算对不上。</summary>
    internal static bool MatchesBaseline(RemoteEditMarker marker, RemoteFileInfo? remote) =>
        remote is { IsDirectory: false }
        && marker.RemoteLength is { } length && marker.RemoteModifiedUtc is { } modified
        && remote.Size == length && remote.LastModified.ToUniversalTime() == modified.ToUniversalTime();

    private async Task<bool> ConfirmAsync(string title, string body, string confirmText) =>
        _confirm is not null && await _confirm(title, body, confirmText);

    private static RemoteEditDraftReuploadOutcome Failed(RemoteEditDraft draft, string where, Exception ex)
    {
        RemoteEditLog.Write("reupload", $"failed {where}: {ex.Message}");
        return new(RemoteEditDraftReuploadResult.Failed, Strings.Format("RemoteEditDraft_Failed", draft.FileName, where, ex.Message));
    }

    /// <summary>
    /// 传上去了:先把标记写成干净(副本就是传上去的那一份),再删子目录 —— 删不掉(编辑器还开着它)也只是一份干净的副本,
    /// 启动时照 7 天的规则清,不会再被当成草稿列出来。
    /// </summary>
    private static void Settle(RemoteEditDraft draft, RemoteEditMarker marker, (long Length, DateTime LastWriteUtc) uploaded)
    {
        RemoteEditDrafts.Write(draft.Directory, marker with
        {
            State = RemoteEditDrafts.Clean,
            FileName = Path.GetFileName(draft.LocalPath),
            Length = uploaded.Length,
            LastWriteUtc = uploaded.LastWriteUtc
        });
        RemoteEditSessionManager.TryDeleteDirectory(draft.Directory);
    }

    private async Task WithdrawAsync(string key)
    {
        if (_notifications is null)
        {
            return;
        }
        try
        {
            await _notifications.RemoveAsync(NotificationSources.RemoteEditDraftIdPrefix + key);
        }
        catch (Exception ex)
        {
            RemoteEditLog.Write("reupload", $"withdrawing the message failed: {ex.Message}");
        }
    }

    private static (long Length, DateTime LastWriteUtc)? SnapshotOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
