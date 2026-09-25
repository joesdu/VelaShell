// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The MIT Shared Memory Extension, Version 1.1 —— Attach / Detach(段按 shmid 附加进服务端进程)

using System.Runtime.Versioning;
using VelaShell.XServer.Server;

namespace VelaShell.XServer.Resources;

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
