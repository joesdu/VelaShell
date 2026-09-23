// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 8 节「Connection Setup」(resource-id-base / mask)、
//   附录 B「Syntactic Conventions」(回复:32 字节起、长度以 4 字节计;事件:恰好 32 字节;
//   错误:32 字节,含序号、出错的值、次 / 主操作码)

using System.Threading.Channels;
using VelaShell.XServer.Protocol;


namespace VelaShell.XServer.Server;

/// <summary>一个已连上的客户端。</summary>
/// <remarks>
/// 除 <see cref="Output" /> 之外的状态只在执行线程上读写。发给客户端的字节进 <see cref="Output" />,
/// 由连接的写出任务写到套接字 —— 执行线程从不在套接字上阻塞。
/// </remarks>
internal sealed class XClient
{
    /// <summary>每个客户端可用的资源 ID 位(21 位,约 200 万个)。</summary>
    public const uint ResourceMask = 0x001FFFFF;

    public XClient(int index, bool bigEndian)
    {
        Index = index;
        ResourceBase = (uint)index << 21;
        BigEndian = bigEndian;
    }

    public int Index { get; }

    public uint ResourceBase { get; }

    public bool BigEndian { get; }

    /// <summary>最近处理完的请求序号(低 16 位)。事件与错误都带它。</summary>
    public ushort Sequence { get; set; }

    public bool BigRequestsEnabled { get; set; }

    public bool Closed { get; set; }

    /// <summary>SetCloseDownMode:0 Destroy(默认),1 RetainPermanent,2 RetainTemporary。</summary>
    public byte CloseDownMode { get; set; }

    public HashSet<uint> SaveSet { get; } = [];

    /// <summary>最近几条请求的「主.次」操作码(只在开了诊断日志时记),出错时一并打印,便于看出错前客户端在干什么。</summary>
    public Queue<string> RecentRequests { get; } = new();

    public Channel<byte[]> Output { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>这个 ID 是不是在本客户端的资源范围内。</summary>
    public bool OwnsId(uint id) => (id & ~ResourceMask) == ResourceBase;

    public XWriter Writer(int capacity = 32) => new(BigEndian, capacity);

    public void Send(byte[] bytes)
    {
        if (!Closed)
        {
            Output.Writer.TryWrite(bytes);
        }
    }

    /// <summary>发一条回复:头(1、data、序号、长度)+ 由 <paramref name="body" /> 写的内容,补齐到至少 32 字节。</summary>
    public void Reply(byte data, Action<XWriter> body)
    {
        XWriter w = Writer(64);
        w.U8(1).U8(data).U16(Sequence).U32(0);
        body(w);
        if (w.Length < 32)
        {
            w.Zero(32 - w.Length);
        }
        w.Pad4();
        w.PatchU32(4, (uint)((w.Length - 32) / 4));
        Send(w.ToArray());
    }

    /// <summary>发一个事件:恰好 32 字节,由 <paramref name="body" /> 写序号之后的 28 字节。</summary>
    public void Event(byte code, byte detail, Action<XWriter> body, bool sent = false)
    {
        XWriter w = Writer();
        w.U8((byte)(code | (sent ? XEventCode.SentFlag : 0))).U8(detail).U16(Sequence);
        body(w);
        if (w.Length < 32)
        {
            w.Zero(32 - w.Length);
        }
        byte[] bytes = w.ToArray();
        Send(bytes.Length == 32 ? bytes : bytes[..32]);
    }

    /// <summary>
    /// 发一个 GenericEvent(Generic Event Extension):32 字节头 —— 35、扩展主操作码、序号、额外长度、evtype ——
    /// 之后可以跟任意多的 4 字节单位。<paramref name="body" /> 从第 10 字节(evtype 之后)写起。
    /// </summary>
    public void GenericEvent(byte extension, ushort evtype, Action<XWriter> body)
    {
        XWriter w = Writer(64);
        w.U8(XEventCode.GenericEvent).U8(extension).U16(Sequence).U32(0).U16(evtype);
        body(w);
        if (w.Length < 32)
        {
            w.Zero(32 - w.Length);
        }
        w.Pad4();
        w.PatchU32(4, (uint)((w.Length - 32) / 4));
        Send(w.ToArray());
    }

    /// <summary>这个客户端经 Generic Event Extension 声明过的版本;没声明过的客户端不该收到 GenericEvent。</summary>
    public bool GenericEventsEnabled { get; set; }

    /// <summary>发一条错误。</summary>
    public void Error(XErrorCode code, uint badValue, ushort minorOpcode, byte majorOpcode)
    {
        XWriter w = Writer();
        w.U8(0).U8((byte)code).U16(Sequence).U32(badValue).U16(minorOpcode).U8(majorOpcode).Zero(21);
        Send(w.ToArray());
    }

    public override string ToString() => $"client#{Index}";
}
