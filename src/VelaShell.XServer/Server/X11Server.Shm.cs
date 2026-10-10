// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   MIT-SHM —— The MIT Shared Memory Extension, Version 1.1:QueryVersion 0(shared-pixmaps、uid、gid、pixmap-format)、
//   Attach 1(shmseg、shmid、read-only)、Detach 2、PutImage 3(total-width / height、src 矩形、dst、depth、format、
//   send-event、shmseg、offset;send-event 时发 Completion 事件 —— drawable、minor-event 3、major-event、shmseg、offset)、
//   GetImage 4(回复 depth、visual、size,像素写进段里)、CreatePixmap 5;错误 BadShmSeg。
//   System V 共享内存段按 shmid 附加(shmat / shmdt),段的大小与属主取自 Linux 的 /proc/sysvipc/shm。
//
//   只对「同一台机器、经 Unix 套接字连进来、与服务端在同一个 IPC 命名空间里」的客户端提供(QueryExtension / ListExtensions
//   对别的客户端看不见它;容器里的客户端给的 shmid 指的是宿主这边的段):
//   远端经 SSH 来的客户端给的 shmid 在这台机器上毫无意义。只在 Linux 上提供 —— 段的大小要可靠地取到,
//   而 shmctl 的结构体布局各平台不同。1.2 的 AttachFd / CreateSegment 要经套接字传文件描述符,不支持。
//   访问控制:连接对端的 uid(SO_PEERCRED)须是段的属主或创建者,或者段的权限对其他人开放 —— 否则一个本机客户端
//   可以借服务端之手读写别的用户的共享内存。段的 XID 别的客户端也能拿来用:不是附加它的那个客户端时,按附加时记下的
//   属主与权限再核一次(ShmPutImage 要读权限,ShmGetImage 要写权限)。
//   共享像素图(CreatePixmap)不支持:服务端的像素图是托管的缓冲;QueryVersion 如实回 shared-pixmaps = False。

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>一次 ShmPutImage 解码的像素上限(6400 万,约 256 MB)。</summary>
    private const long MaxShmImagePixels = 1L << 26;

    /// <summary>这台服务端提供 MIT-SHM:Linux 且有 /proc/sysvipc/shm。</summary>
    private static bool ShmSupported => OperatingSystem.IsLinux() && File.Exists("/proc/sysvipc/shm");

    private void Shm(XClient c, XRequestReader r)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new XProtocolError(XErrorCode.Request);
        }
        switch (r.Data)
        {
            case 0:   // QueryVersion:1.1,共享像素图不支持,服务端的有效 uid / gid(原先回 0),像素图格式 ZPixmap
                c.Reply(0, static w => w.U16(1).U16(1).U16((ushort)GetEffectiveUid()).U16((ushort)GetEffectiveGid()).U8(2).Zero(15));
                break;
            case 1:   // Attach
                ShmAttach(c, r);
                break;
            case 2:   // Detach
                {
                    XShmSegment segment = Segment(c, r.U32(), write: false);
                    RemoveResource(segment.Id);
                    segment.Detach();
                    break;
                }
            case 3:   // PutImage
                ShmPutImage(c, r);
                break;
            case 4:   // GetImage
                ShmGetImage(c, r);
                break;
            case 5:   // CreatePixmap:共享像素图不支持(QueryVersion 回了 shared-pixmaps = False)
                throw new XProtocolError(XErrorCode.Implementation);
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    /// <summary>
    /// 按 XID 找段;不是附加它的那个客户端来用时,核对这个客户端的 uid 对段有没有所需的权限(读 / 写)——
    /// 附加时只核对了附加者,别的客户端(比如持 cookie 连进来的别的用户)拿到 XID 就能借服务端之手读写它。
    /// </summary>
    internal XShmSegment Segment(XClient c, uint id, bool write)
    {
        XShmSegment segment = Use<XShmSegment>(id) ?? throw new XProtocolError((XErrorCode)ShmErrorBase, id);
        if (!ReferenceEquals(segment.Owner, c) && !MayAccess(c, segment.Access, readOnly: !write))
        {
            throw new XProtocolError(XErrorCode.Access, id);
        }
        return segment;
    }

    [SupportedOSPlatform("linux")]
    private void ShmAttach(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        int shmid = r.I32();
        bool readOnly = r.Bool();
        if (!ShmAttachAllowed(c) || FindShmSegment(shmid) is not { } info || !MayAccess(c, info, readOnly))
        {
            NoteShmAttachFailure(c);
            throw new XProtocolError(XErrorCode.Access, (uint)shmid);
        }
        nint address = ShmAttachSegment(shmid, readOnly);
        if (address == -1)
        {
            NoteShmAttachFailure(c);
            throw new XProtocolError(XErrorCode.Access, (uint)shmid);
        }
        XShmSegment segment = new(id, c, shmid, readOnly, address, info);
        try
        {
            AddResource(c, segment);
        }
        catch
        {
            segment.Detach();
            throw;
        }
    }

    /// <summary>对端 uid 是段的属主或创建者,或段对其他人开放了所需的权限。</summary>
    private static bool MayAccess(XClient c, XShmAccess info, bool readOnly)
    {
        if (c.PeerUid is not { } uid)
        {
            return false;   // 取不到对端身份(不是 Unix 套接字):不给
        }
        if (uid == info.Uid || uid == info.Cuid)
        {
            return true;
        }
        int other = info.Perms & 0x7;
        return (other & 4) != 0 && (readOnly || (other & 2) != 0);
    }

    [SupportedOSPlatform("linux")]
    private void ShmPutImage(XClient c, XRequestReader r)
    {
        uint drawable = r.U32(), gcId = r.U32();
        ushort totalWidth = r.U16(), totalHeight = r.U16();
        ushort srcX = r.U16(), srcY = r.U16(), srcWidth = r.U16(), srcHeight = r.U16();
        short dstX = r.I16(), dstY = r.I16();
        byte depth = r.U8(), format = r.U8();
        bool sendEvent = r.Bool();
        r.Skip(1);
        uint segmentId = r.U32();
        uint offset = r.U32();
        XShmSegment segment = Segment(c, segmentId, write: false);   // 服务端从段里读
        XGc gc = Gc(gcId);
        byte targetDepth = DrawableDepth(drawable);
        if (srcX + srcWidth > totalWidth || srcY + srcHeight > totalHeight)
        {
            throw new XProtocolError(XErrorCode.Value, srcX);
        }
        if ((long)totalWidth * totalHeight > MaxShmImagePixels)
        {
            throw new XProtocolError(XErrorCode.Alloc);
        }
        // 先核对格式与深度,再按它们算长度:XYPixmap 的长度随 depth 线性增长,乱写的 depth 不能先拿去算。
        ValidateImageFormat(format, depth, targetDepth, 0);
        long length = ImageDataLength(format, depth, totalWidth, totalHeight, 0);
        if (offset + length > segment.Size)
        {
            throw new XProtocolError(XErrorCode.Value, offset);
        }
        if (srcWidth > 0 && srcHeight > 0 && totalWidth > 0 && totalHeight > 0)
        {
            // 直接读段里的数据,只解 / 只贴源矩形里画得到的那一块:客户端(Qt、GTK)常常是一整幅共享图像里只更新一小块。
            // 段只在执行线程上摘下(Detach、客户端断开、收工),这里读的时候它一直映射着;
            // 客户端可能同时在改内容 —— 那只影响像素本身,不影响任何校验。
            ReadOnlySpan<byte> data;
            unsafe
            {
                data = new ReadOnlySpan<byte>((byte*)segment.Address + offset, checked((int)length));
            }
            PutImageRegion(drawable, gc, targetDepth, format, depth, 0, data, totalWidth, totalHeight,
                new XRect(srcX, srcY, srcWidth, srcHeight), dstX, dstY);
        }
        if (sendEvent)
        {
            // Completion:客户端据此知道段可以重用了。
            c.Event(ShmEventBase, 0, w => w.U32(drawable).U16(3).U8(ShmMajor).Zero(1).U32(segmentId).U32(offset));
        }
    }

    [SupportedOSPlatform("linux")]
    private void ShmGetImage(XClient c, XRequestReader r)
    {
        uint drawable = r.U32();
        short x = r.I16(), y = r.I16();
        ushort width = r.U16(), height = r.U16();
        uint planeMask = r.U32();
        byte format = r.U8();
        r.Skip(3);
        XShmSegment segment = Segment(c, r.U32(), write: true);      // 服务端往段里写
        uint offset = r.U32();
        if (segment.ReadOnly)
        {
            throw new XProtocolError(XErrorCode.Access, segment.Id);
        }
        (byte depth, uint visual, byte[] data) = CaptureImage(format, drawable, x, y, width, height, planeMask);
        if (offset + data.Length > segment.Size)
        {
            throw new XProtocolError(XErrorCode.Value, offset);
        }
        Marshal.Copy(data, 0, segment.Address + (nint)offset, data.Length);
        uint size = (uint)data.Length;
        c.Reply(depth, w => w.U32(visual).U32(size).Zero(16));
    }

    /// <summary>客户端断开 / 服务端收工:把附加的段都摘下来。</summary>
    private static void DetachShmSegments(IEnumerable<XResource> resources)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        foreach (XResource resource in resources)
        {
            if (resource is XShmSegment segment)
            {
                segment.Detach();
            }
        }
    }

    // ------------------------------------------------------------------ System V 共享内存

    // ShmAttach 每次都要读一遍 /proc/sysvipc/shm(段的大小与属主没有别的可靠来源:shmctl 的结构体布局各架构不同)。
    // 一个客户端拿一串错的 shmid 反复 Attach,原先每条都整读、整份拆分一遍,段多的机器上就是持续占着执行线程。
    // 现在逐行读、找到就停、只拆要的那几列;同一个客户端每秒失败超过 MaxShmAttachFailuresPerSecond 次之后,
    // 这一秒里的 Attach 直接回 BadAccess、不再读表(正常的程序几乎不会 Attach 失败)。

    /// <summary>一个客户端每秒最多这么多次 Attach 失败,再多的这一秒里不再查表。</summary>
    internal const int MaxShmAttachFailuresPerSecond = 16;

    /// <summary>各客户端这一秒里 Attach 失败的次数(客户端断开时摘掉)。</summary>
    private readonly Dictionary<XClient, (long Second, int Count)> _shmAttachFailures = [];

    /// <summary>读 /proc/sysvipc/shm 的次数(测试用)。</summary>
    internal int ShmTableReads { get; private set; }

    private bool ShmAttachAllowed(XClient c) =>
        !_shmAttachFailures.TryGetValue(c, out (long Second, int Count) failures)
        || failures.Second != CurrentSecond || failures.Count < MaxShmAttachFailuresPerSecond;

    private void NoteShmAttachFailure(XClient c)
    {
        long second = CurrentSecond;
        _shmAttachFailures[c] = _shmAttachFailures.TryGetValue(c, out (long Second, int Count) failures) && failures.Second == second
            ? (second, failures.Count + 1)
            : (second, 1);
    }

    private static long CurrentSecond => System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

    private void CleanupShm(XClient client) => _shmAttachFailures.Remove(client);

    /// <summary>/proc/sysvipc/shm 里这个 shmid 的大小、属主、创建者与权限;没有时为 null。</summary>
    private XShmAccess? FindShmSegment(int shmid)
    {
        ShmTableReads++;
        try
        {
            using IEnumerator<string> lines = File.ReadLines("/proc/sysvipc/shm").GetEnumerator();
            if (!lines.MoveNext())
            {
                return null;
            }
            string[] header = lines.Current.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int idCol = Array.IndexOf(header, "shmid"), sizeCol = Array.IndexOf(header, "size");
            int uidCol = Array.IndexOf(header, "uid"), cuidCol = Array.IndexOf(header, "cuid"), permsCol = Array.IndexOf(header, "perms");
            if (idCol < 0 || sizeCol < 0 || uidCol < 0 || cuidCol < 0 || permsCol < 0)
            {
                return null;
            }
            int lastCol = Math.Max(Math.Max(idCol, sizeCol), Math.Max(Math.Max(uidCol, cuidCol), permsCol));
            Span<Range> columns = stackalloc Range[lastCol + 2];
            while (lines.MoveNext())
            {
                ReadOnlySpan<char> line = lines.Current;
                if (line.Split(columns, ' ', StringSplitOptions.RemoveEmptyEntries) <= lastCol
                    || !int.TryParse(line[columns[idCol]], out int id) || id != shmid)
                {
                    continue;
                }
                return new XShmAccess(long.Parse(line[columns[sizeCol]], System.Globalization.CultureInfo.InvariantCulture),
                    uint.Parse(line[columns[uidCol]], System.Globalization.CultureInfo.InvariantCulture),
                    uint.Parse(line[columns[cuidCol]], System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToInt32(line[columns[permsCol]].ToString(), 8));
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private const int ShmReadOnlyFlag = 0x1000;   // SHM_RDONLY

    [SupportedOSPlatform("linux")]
    private static nint ShmAttachSegment(int shmid, bool readOnly) => ShmAt(shmid, 0, readOnly ? ShmReadOnlyFlag : 0);

    [SupportedOSPlatform("linux")]
    internal static void ShmDetach(nint address) => _ = ShmDt(address);

    [LibraryImport("libc", EntryPoint = "shmat", SetLastError = true)]
    private static partial nint ShmAt(int shmid, nint address, int flags);

    [LibraryImport("libc", EntryPoint = "shmdt", SetLastError = true)]
    private static partial int ShmDt(nint address);

    [LibraryImport("libc", EntryPoint = "getegid")]
    private static partial uint GetEffectiveGid();
}
