using System.Diagnostics;
using System.Globalization;
using System.Text;
using VelaShell.Ssh.Forwarding;

namespace VelaShell.Infrastructure.XServer;

/// <summary>
/// 把内置 X 服务端的授权 cookie 写进(停下时撤出)用户的 <c>.Xauthority</c>:本机别的 X 程序经
/// <c>DISPLAY=localhost:N</c> / <c>:N</c> 连它时,Xlib 自己从这里取 cookie 带上 —— 与 <c>xauth add :N . cookie</c> 效果相同。
/// </summary>
/// <remarks>
/// <para>
/// 文件格式的编解码用 SSH 库的 <see cref="XAuthority" />(转发找 cookie 用的也是它;原先这里另有一份解析,规则与库里的不同)。
/// 本机显示的记录族是 <see cref="XAuthority.FamilyLocal" />、地址是本机主机名 —— Xlib 连本机(包括环回 TCP)时就按主机名找。
/// 这里只管「怎样安全地改写这个文件」。
/// </para>
/// <para>
/// <b>这个文件里装的是能打开用户各个显示的钥匙</b>,改的时候格外小心:整份解析不了就不动它(截断的、别的格式的);
/// 按 xauth 的约定先建 <c>文件名-c</c>、再建 <c>文件名-l</c> 当锁(别的 xauth 正在改就等一会儿,等不到就放弃);新内容写进同目录的临时文件、
/// 权限 0600,再整个换过去 —— 写到一半崩了,原文件也还是完整的。任何一步失败只记日志,不影响服务端运行。
/// </para>
/// </remarks>
internal static class XAuthorityFile
{
    /// <summary>
    /// 锁文件比这更老就当作上次没收拾干净的残留。原先 10 秒:家目录在慢的 NFS 上时,别的程序正拿着的锁可能被当成残留删掉,
    /// 两边同时改写、丢掉对方的记录。拿不到锁的代价只是这一次 cookie 没登记(记一行日志),宁可等不到。
    /// </summary>
    private static readonly TimeSpan StaleLock = TimeSpan.FromSeconds(60);

    /// <summary>这一条是不是我们给 <paramref name="hostName" />:<paramref name="display" /> 登记的那种(本机族、MIT-MAGIC-COOKIE-1)。</summary>
    private static bool IsFor(XAuthorityEntry entry, string hostName, int display) =>
        entry.Family == XAuthority.FamilyLocal && entry.Name == XAuthority.MitMagicCookie1
        && entry.DisplayNumber == display.ToString(CultureInfo.InvariantCulture)
        && string.Equals(Encoding.ASCII.GetString(entry.Address.Span), hostName, StringComparison.OrdinalIgnoreCase);

    /// <summary>登记这个显示的 cookie(同一主机名、同一显示号的旧记录一并替换,与 <c>xauth add</c> 一致)。</summary>
    public static bool Add(string path, string hostName, int display, byte[] cookie) =>
        Update(path, entries =>
        {
            entries.RemoveAll(e => IsFor(e, hostName, display));
            entries.Add(new XAuthorityEntry(XAuthority.FamilyLocal, Encoding.ASCII.GetBytes(hostName),
                display.ToString(CultureInfo.InvariantCulture), XAuthority.MitMagicCookie1, cookie.ToArray()));
            return true;
        });

    /// <summary>撤掉自己登记的那一条(cookie 也对得上才撤:同一显示号后来被别的服务端登记了就不碰)。</summary>
    public static bool Remove(string path, string hostName, int display, byte[] cookie) =>
        Update(path, entries => entries.RemoveAll(e => IsFor(e, hostName, display) && e.Data.Span.SequenceEqual(cookie)) > 0);

    /// <summary>
    /// 在锁里读、改、整个换掉。<paramref name="change" /> 返回 false 表示没改动,不写文件。
    /// 文件有任何一条认不全(截断的、别的格式的)就不动它 —— 改写回去会把用户别的钥匙弄丢(<see cref="XAuthority.TryDecode" />)。
    /// </summary>
    private static bool Update(string path, Func<List<XAuthorityEntry>, bool> change)
    {
        string lockPath = path + "-c", linkPath = path + "-l";
        if (!TryLock(lockPath, linkPath))
        {
            Trace.WriteLine($"[XServer] {path} is locked by another program; the cookie was not updated");
            return false;
        }
        try
        {
            byte[]? content = ReadSnapshot(path);
            if (content is null || !XAuthority.TryDecode(content, out IReadOnlyList<XAuthorityEntry>? decoded))
            {
                Trace.WriteLine($"[XServer] {path} is not a valid Xauthority file; leaving it alone");
                return false;
            }
            List<XAuthorityEntry> entries = [.. decoded];
            if (!change(entries))
            {
                return true;
            }
            byte[] encoded = XAuthority.Encode(entries);
            if (encoded.Length > XAuthority.MaxFileBytes)
            {
                Trace.WriteLine($"[XServer] updating {path} would exceed {XAuthority.MaxFileBytes} bytes; leaving it alone");
                return false;
            }
            string temporary = path + "-n";
            File.Delete(temporary);   // 上次没收拾干净的残留:权限只在新建时生效,得先删掉
            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;   // 0600:别的用户读不到钥匙
            }
            using (FileStream stream = new(temporary, options))
            {
                stream.Write(encoded);
            }
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[XServer] cannot update {path}: {ex.Message}");
            return false;
        }
        finally
        {
            foreach (string held in (string[])[linkPath, lockPath])   // 先 -l 后 -c(与上锁的次序相反)
            {
                try
                {
                    File.Delete(held);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Trace.WriteLine($"[XServer] cannot remove {held}: {ex.Message}");
                }
            }
        }
    }

    private static byte[]? ReadSnapshot(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            if (!stream.CanSeek)
            {
                return null;
            }
            long length = stream.Length;
            if (length > XAuthority.MaxFileBytes)
            {
                return null;
            }
            byte[] content = new byte[(int)length];
            stream.ReadExactly(content);
            // 读的过程中变长了也不改写,否则会丢掉后加的记录。
            return stream.ReadByte() == -1 ? content : null;
        }
        catch (FileNotFoundException)
        {
            return [];
        }
    }

    /// <summary>
    /// 按 xauth 的约定上锁(Xau 的 XauLockAuth:先建 <c>文件名-c</c>,再由它得到 <c>文件名-l</c>,两个都成了才算拿到锁)。
    /// 原先只建 <c>-c</c>:xauth 那一侧以 <c>-l</c> 是否建得成为准的话,两边会同时以为自己拿着锁,与 xauth 只是部分互斥。
    /// 这里两个都以「不存在才建得成」的方式建;<c>-l</c> 建不成就放掉自己的 <c>-c</c>,等一会儿再试。别人拿着就等,
    /// 比 <see cref="StaleLock" /> 还老的当作残留清掉(<c>-c</c> 老了连同 <c>-l</c> 一起清,与 XauLockAuth 的 dead 参数同义)。
    /// </summary>
    private static bool TryLock(string lockPath, string linkPath)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                CreateExclusive(lockPath);
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                if (IsStale(lockPath))
                {
                    TryDelete(linkPath);
                    TryDelete(lockPath);
                    continue;
                }
                Thread.Sleep(50);
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[XServer] cannot lock {lockPath}: {ex.Message}");
                return false;
            }
            try
            {
                CreateExclusive(linkPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // -l 在别人手里(或者是上次没收拾干净的):放掉自己的 -c,不挡着别人收尾。
                bool stale = File.Exists(linkPath) && IsStale(linkPath);
                if (stale)
                {
                    TryDelete(linkPath);
                }
                TryDelete(lockPath);
                if (!stale)
                {
                    Thread.Sleep(50);
                }
            }
        }
        return false;

        static void CreateExclusive(string path)
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
            }
        }

        static bool IsStale(string path) => DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > StaleLock;

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
