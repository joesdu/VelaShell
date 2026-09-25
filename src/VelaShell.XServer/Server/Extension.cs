// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

/// <summary>一个扩展:名字、分到的主操作码与事件 / 错误编号、请求处理,以及清理状态的钩子。</summary>
internal sealed class Extension(string name, byte majorOpcode, Action<XClient, XRequestReader> handle)
{
    public string Name { get; } = name;

    public byte MajorOpcode { get; } = majorOpcode;

    /// <summary>这个扩展的第一个事件码;没有自己的事件时为 0。</summary>
    public byte FirstEvent { get; init; }

    /// <summary>占用几个事件码(从 <see cref="FirstEvent" /> 起)。</summary>
    public int EventCount { get; init; }

    /// <summary>这个扩展的第一个错误码;没有自己的错误时为 0。</summary>
    public byte FirstError { get; init; }

    /// <summary>占用几个错误码(从 <see cref="FirstError" /> 起)。</summary>
    public int ErrorCount { get; init; }

    /// <summary>只对部分客户端可见(比如 MIT-SHM 只给同一台机器上的);null = 对谁都可见。</summary>
    public Func<XClient, bool>? VisibleTo { get; init; }

    /// <summary>客户端断开时清它在这个扩展里的状态(它的资源此时已从资源表里释放)。</summary>
    public Action<XClient>? ClientClosed { get; init; }

    /// <summary>窗口销毁时清这个扩展里与它有关的状态。</summary>
    public Action<XWindow>? WindowDestroyed { get; init; }

    public bool IsVisibleTo(XClient client) => VisibleTo?.Invoke(client) ?? true;

    public void Handle(XClient client, XRequestReader request) => handle(client, request);
}
