#!/usr/bin/env dotnet
// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// ============================================================================
// 压缩：在 System.IO.Compression.UseStrictValidation 打开时也要对
// ============================================================================
//
// 为什么这件事需要一个**单独的进程**来验：
//
// `UseStrictValidation` 是一个 AppContext 开关，只能在进程启动时设一次
// （`AppContext.SetSwitch`，或 runtimeconfig）。环境变量设不了它，
// 而 `ZlibCompressor` 里读它的那个静态字段一个进程只读一次 ——
// 所以它没法在常规单元测试里和别的用例同进程并行地开关。
//
// 它为什么要紧：SSH 的 zlib 流是**一直 flush、永不结束**的。
// 开关打开之后，「读到没有更多数据」会被 System.IO.Compression 判成
// 流被截断并抛 InvalidDataException —— 也就是说**每一个报文解完都会撞上它**。
// 处理不对的话，开了这个开关的应用一连上就全线报错。
//
// 用法（CI 里也跑这一条）：
//   dotnet run scripts/ssh/compression/verify-strict-validation.cs
//
// 退出码 0 = 通过。

#:project ../../../src/VelaShell.Ssh/VelaShell.Ssh.csproj
#:property LangVersion=preview
#:property TreatWarningsAsErrors=false
#:property EnforceCodeStyleInBuild=false
#:property AnalysisLevel=none
// 宿主根 Directory.Build.props 全仓打开了 GenerateDocumentationFile;脚本不是 API。
#:property GenerateDocumentationFile=false

using System.Buffers;
using System.Text;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Diagnostics;

// ⚠️ 必须在**碰到 ZlibCompressor 之前**设 —— 那个静态字段只读一次。
AppContext.SetSwitch("System.IO.Compression.UseStrictValidation", true);

return Verify();

static int Verify()
{
    bool on = AppContext.TryGetSwitch("System.IO.Compression.UseStrictValidation", out bool value) && value;
    if (!on)
    {
        Console.Error.WriteLine("❌ 开关没设上 —— 这一轮什么都没验到。");
        return 1;
    }

    Console.WriteLine("UseStrictValidation = True");

    using ZlibCompressor sender = new();
    using ZlibCompressor receiver = new();

    // 连发几个报文：既验往返，也验字典确实跨报文保留了
    // （第一个报文之后线上字节应当明显变小）。
    int firstWire = 0;
    for (int i = 1; i <= 5; i++)
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            $"报文 {i}：" + string.Concat(Enumerable.Repeat("同一段反复出现的内容。", 20)));

        ArrayBufferWriter<byte> wire = new();
        sender.Compress(payload, wire);

        ArrayBufferWriter<byte> back = new();
        receiver.Decompress(new ReadOnlySequence<byte>(wire.WrittenMemory), back, 1 << 20);

        if (!back.WrittenSpan.SequenceEqual(payload))
        {
            Console.Error.WriteLine($"❌ 报文 {i} 没能原样还原。");
            return 1;
        }

        if (i == 1)
        {
            firstWire = wire.WrittenCount;
        }
        else if (wire.WrittenCount >= firstWire)
        {
            Console.Error.WriteLine(
                $"❌ 报文 {i} 的线上字节（{wire.WrittenCount}）没有比第一个（{firstWire}）小 —— " +
                "字典没有跨报文保留住。");
            return 1;
        }

        Console.WriteLine($"  报文 {i}: {payload.Length} → 线上 {wire.WrittenCount} → 还原 {back.WrittenCount} ✔");
    }

    // **开关打开也不能把非法数据放过去。**
    // 豁免只针对「这一次读没产出任何数据」的情况；解出来什么都没有的载荷仍然是错误。
    try
    {
        using ZlibCompressor lonely = new();
        ArrayBufferWriter<byte> junk = new();
        lonely.Decompress(new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), junk, 1 << 20);

        Console.Error.WriteLine("❌ 非法 zlib 数据没有报错 —— 豁免开得太宽了。");
        return 1;
    }
    catch (SshProtocolException)
    {
        Console.WriteLine("  非法数据仍然被拦下 ✔");
    }

    Console.WriteLine("✅ 严格校验开关下的压缩行为正确。");
    return 0;
}
