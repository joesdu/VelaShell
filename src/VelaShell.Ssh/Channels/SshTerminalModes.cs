// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §8    终端模式编码
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §5.3

using System.Buffers;
using System.Collections.Immutable;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Channels;

/// <summary>一组终端模式（<c>pty-req</c> 里的 encoded terminal modes）。</summary>
/// <remarks>
/// <para>
/// 不给就发一个只含 <see cref="SshTerminalModeOpcode.EndOfOptions"/> 的空表 ——
/// 那表示「用服务端的默认值」，是最常见也最安全的选择。
/// </para>
/// <para>
/// <b>不可变。</b><see cref="With(byte, uint)"/> 返回一个新实例。它挂在
/// <see cref="SshShellOptions.Modes"/> 上，而那个 record 的 <c>Default</c> 与 <c>with</c> 副本会共享同一个实例 ——
/// 可变的话，任何一处改动都会改掉所有调用方的默认值。
/// </para>
/// </remarks>
public sealed class SshTerminalModes
{
    private readonly ImmutableArray<(byte Opcode, uint Argument)> _entries;

    private SshTerminalModes(ImmutableArray<(byte Opcode, uint Argument)> entries) => _entries = entries;

    /// <summary>空的模式表：一切按服务端默认。</summary>
    public static SshTerminalModes Empty { get; } = new([]);

    /// <summary>加一个具名模式，返回新的模式表。</summary>
    public SshTerminalModes With(SshTerminalModeOpcode opcode, uint argument) =>
        With((byte)opcode, argument);

    /// <summary>加一个裸操作码，返回新的模式表 —— 具名成员不可能穷举，未知的值原样传过去。</summary>
    public SshTerminalModes With(byte opcode, uint argument)
    {
        if (opcode == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(opcode), "0 是模式表的结束标记，不能当成一个模式来设。");
        }

        // 160–255 是保留区：RFC 4254 §8 说解析方遇到未知的应当停止解析，
        // 所以往里塞东西会把后面所有模式一起弄丢。
        if (opcode >= 160)
        {
            throw new ArgumentOutOfRangeException(
                nameof(opcode),
                $"操作码 {opcode} 落在保留区（160–255）。对端遇到它会停止解析，" +
                "排在后面的模式会被一起丢掉。");
        }

        return new SshTerminalModes(_entries.Add((opcode, argument)));
    }

    /// <summary>编码成 <c>pty-req</c> 里那个 string 的内容。</summary>
    internal byte[] Encode()
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        foreach ((byte opcode, uint argument) in _entries)
        {
            writer.WriteByte(opcode);
            writer.WriteUInt32(argument);
        }
        writer.WriteByte((byte)SshTerminalModeOpcode.EndOfOptions);
        return buffer.WrittenSpan.ToArray();
    }
}
