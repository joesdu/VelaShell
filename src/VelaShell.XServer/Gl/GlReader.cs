// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   GLX Extensions for OpenGL Protocol Specification, Version 1.3 —— §1.4「Common Types」(FLOAT32 / FLOAT64 是 IEEE 单 / 双精度,
//   与其余多字节量一样按客户端字节序)、§2.3.3「GL Rendering Commands」(参数按 C 函数的次序紧排,FLOAT64 不另对齐)。

using System.Buffers.Binary;

namespace VelaShell.XServer.Gl;

/// <summary>按客户端字节序读一段 GL 渲染命令的参数。越界读返回 0,由调用方按长度先行校验。</summary>
internal ref struct GlReader
{
    private readonly ReadOnlySpan<byte> _data;

    public GlReader(ReadOnlySpan<byte> data, bool bigEndian)
    {
        _data = data;
        BigEndian = bigEndian;
        Position = 0;
    }

    public readonly int Remaining => _data.Length - Position;

    public int Position { get; private set; }

    public readonly bool BigEndian { get; }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (Position + count > _data.Length)
        {
            Position = _data.Length;
            return default;
        }
        ReadOnlySpan<byte> span = _data.Slice(Position, count);
        Position += count;
        return span;
    }

    public void Skip(int count) => Position = Math.Min(_data.Length, Position + count);

    public byte U8()
    {
        ReadOnlySpan<byte> s = Take(1);
        return s.IsEmpty ? (byte)0 : s[0];
    }

    public sbyte I8() => (sbyte)U8();

    public ushort U16()
    {
        ReadOnlySpan<byte> s = Take(2);
        return s.IsEmpty ? (ushort)0 : BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
    }

    public short I16() => (short)U16();

    public uint U32()
    {
        ReadOnlySpan<byte> s = Take(4);
        return s.IsEmpty ? 0 : BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
    }

    public int I32() => (int)U32();

    public float F32() => BitConverter.UInt32BitsToSingle(U32());

    public double F64()
    {
        ReadOnlySpan<byte> s = Take(8);
        return s.IsEmpty ? 0 : BitConverter.UInt64BitsToDouble(
            BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s));
    }

    /// <summary>剩下的字节(不复制)。</summary>
    public ReadOnlySpan<byte> Rest() => Take(Remaining);

    public ReadOnlySpan<byte> Bytes(int count) => Take(Math.Min(count, Remaining));
}
