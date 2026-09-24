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
//   只对「同一台机器、经 Unix 套接字连进来」的客户端提供(QueryExtension / ListExtensions 对别的客户端看不见它):
//   远端经 SSH 来的客户端给的 shmid 在这台机器上毫无意义。只在 Linux 上提供 —— 段的大小要可靠地取到,
//   而 shmctl 的结构体布局各平台不同。1.2 的 AttachFd / CreateSegment 要经套接字传文件描述符,不支持。
//   访问控制:连接对端的 uid(SO_PEERCRED)须是段的属主或创建者,或者段的权限对其他人开放 —— 否则一个本机客户端
//   可以借服务端之手读写别的用户的共享内存。
//   共享像素图(CreatePixmap)不支持:服务端的像素图是托管的缓冲;QueryVersion 如实回 shared-pixmaps = False。

using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;

namespace VelaShell.XServer.Server;

/// <summary>一个附加上的 System V 共享内存段。</summary>
internal sealed class XShmSegment(uint id, XClient owner, int shmid, bool readOnly, nint address, long size) : XResource(id, owner)
{
    public int ShmId { get; } = shmid;

    public bool ReadOnly { get; } = readOnly;

    public nint Address { get; private set; } = address;

    public long Size { get; } = size;

    /// <summary>从服务端进程里摘下(shmdt)。重复调用无害。</summary>
    [SupportedOSPlatform("linux")]
    public void Detach()
    {
        if (Address != 0)
        {
            X11Server.ShmDetach(Address);
            Address = 0;
        }
    }
}

public sealed partial class X11Server
{
    private const byte ShmMajor = 147;
    private const byte ShmEventBase = 91;   // Completion
    private const byte ShmErrorBase = 149;  // BadShmSeg

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
            case 0:   // QueryVersion:1.1,共享像素图不支持,像素图格式 ZPixmap
                c.Reply(0, w => w.U16(1).U16(1).U16(0).U16(0).U8(2).Zero(15));
                break;
            case 1:   // Attach
                ShmAttach(c, r);
                break;
            case 2:   // Detach
                {
                    XShmSegment segment = Segment(r.U32());
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

    private XShmSegment Segment(uint id) =>
        Lookup<XShmSegment>(id) ?? throw new XProtocolError((XErrorCode)ShmErrorBase, id);

    [SupportedOSPlatform("linux")]
    private void ShmAttach(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        int shmid = r.I32();
        bool readOnly = r.Bool();
        if (FindShmSegment(shmid) is not { } info || !MayAccess(c, info, readOnly))
        {
            throw new XProtocolError(XErrorCode.Access, (uint)shmid);
        }
        nint address = ShmAttachSegment(shmid, readOnly);
        if (address == -1)
        {
            throw new XProtocolError(XErrorCode.Access, (uint)shmid);
        }
        XShmSegment segment = new(id, c, shmid, readOnly, address, info.Size);
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
    private static bool MayAccess(XClient c, (long Size, uint Uid, uint Cuid, int Perms) info, bool readOnly)
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
        XShmSegment segment = Segment(segmentId);
        XGc gc = Gc(gcId);
        byte targetDepth = DrawableDepth(drawable);
        if (srcX + srcWidth > totalWidth || srcY + srcHeight > totalHeight)
        {
            throw new XProtocolError(XErrorCode.Value, srcX);
        }
        if ((long)totalWidth * totalHeight > MaxShmImagePixels)
        {
            throw new XProtocolError(XErrorCode.Alloc);   // 位图格式一个字节八个像素:段再大也不按它的大小去分配像素
        }
        long length = ImageDataLength(format, depth, totalWidth, totalHeight, 0);
        if (offset + length > segment.Size)
        {
            throw new XProtocolError(XErrorCode.Value, offset);
        }
        if (srcWidth > 0 && srcHeight > 0 && totalWidth > 0 && totalHeight > 0)
        {
            byte[] data = ArrayPool<byte>.Shared.Rent((int)Math.Max(1, length));
            uint[] whole = ArrayPool<uint>.Shared.Rent(totalWidth * totalHeight);
            uint[] part = ArrayPool<uint>.Shared.Rent(srcWidth * srcHeight);
            try
            {
                Marshal.Copy(segment.Address + (nint)offset, data, 0, (int)length);
                DecodeImage(format, depth, targetDepth, 0, data.AsSpan(0, (int)length), totalWidth, totalHeight, gc, whole);
                for (int y = 0; y < srcHeight; y++)
                {
                    whole.AsSpan(((srcY + y) * totalWidth) + srcX, srcWidth).CopyTo(part.AsSpan(y * srcWidth));
                }
                Draw(drawable, gcId, raster => raster.Blit(part, srcWidth, srcHeight, dstX, dstY, preMasked: false));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data);
                ArrayPool<uint>.Shared.Return(whole);
                ArrayPool<uint>.Shared.Return(part);
            }
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
        XShmSegment segment = Segment(r.U32());
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

    /// <summary>/proc/sysvipc/shm 里这个 shmid 的大小、属主、创建者与权限;没有时为 null。</summary>
    private static (long Size, uint Uid, uint Cuid, int Perms)? FindShmSegment(int shmid)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines("/proc/sysvipc/shm");
        }
        catch (IOException)
        {
            return null;
        }
        if (lines.Length == 0)
        {
            return null;
        }
        string[] header = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int idCol = Array.IndexOf(header, "shmid"), sizeCol = Array.IndexOf(header, "size");
        int uidCol = Array.IndexOf(header, "uid"), cuidCol = Array.IndexOf(header, "cuid"), permsCol = Array.IndexOf(header, "perms");
        if (idCol < 0 || sizeCol < 0 || uidCol < 0 || cuidCol < 0 || permsCol < 0)
        {
            return null;
        }
        foreach (string line in lines.Skip(1))
        {
            string[] cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length <= Math.Max(Math.Max(idCol, sizeCol), Math.Max(Math.Max(uidCol, cuidCol), permsCol))
                || !int.TryParse(cols[idCol], out int id) || id != shmid)
            {
                continue;
            }
            return (long.Parse(cols[sizeCol], System.Globalization.CultureInfo.InvariantCulture),
                uint.Parse(cols[uidCol], System.Globalization.CultureInfo.InvariantCulture),
                uint.Parse(cols[cuidCol], System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToInt32(cols[permsCol], 8));
        }
        return null;
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
}
