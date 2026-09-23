#!/usr/bin/env dotnet
// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// ============================================================================
// 性能基准
// ============================================================================
//
// 它是一个**单文件应用**而不是一个项目 —— 与 src/VelaShell.Ssh/AGENTS.md §3.2 一致：
// 脚本用 PowerShell 或 C# 单文件，不新建项目。
//
// 用法：
//   dotnet run scripts/ssh/benchmarks/benchmarks.cs                  # 跑全部
//   dotnet run scripts/ssh/benchmarks/benchmarks.cs -- --filter 密码  # 只跑名字里带「密码」的
//
// 它量的是 README 里声称的那几件事。**声称了就要能量出来** ——
// 量不出来的性能主张和没有主张是一回事。

#:project ../../../src/VelaShell.Ssh/VelaShell.Ssh.csproj
// 版本单独钉在这里:脚本不在 tests/ 的中央包管理之下;tests/ 那边的 0.16 预览版改了 InProcessNoEmitToolchain 的 API。
#:package BenchmarkDotNet@0.14.0
#:property LangVersion=preview
#:property TreatWarningsAsErrors=false
#:property EnforceCodeStyleInBuild=false
#:property AnalysisLevel=none
// 宿主根 Directory.Build.props 全仓打开了 GenerateDocumentationFile;脚本不是 API。
#:property GenerateDocumentationFile=false
#:property Configuration=Release
#:property IsAotCompatible=false

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.Sftp;
using VelaShell.Ssh.Transport;

// ⚠️ **必须用进程内工具链。**
//
// BenchmarkDotNet 默认会为每组基准**重新生成一个项目**再编译 ——
// 而单文件应用根本没有 .csproj 给它找，于是报
// 「Unable to find benchmarks … name of output exe is different than the name of the .csproj」。
//
// 用 NoEmit 而不是 Emit：Emit 那一版在这里构建包装代码时直接抛异常
// （只报一句「Build Error: Exception!」，查不出所以然）。
// NoEmit 走反射调用，每次多几十纳秒的固定开销 ——
// 对「改动前后在同一台机器上对比」无所谓，因为两边都摊到这份开销。
//
// 进程内工具链的另一个代价是各组基准共用一个进程（没有隔离），
// 对「改动前后在同一台机器上对比」这个用途完全够用 ——
// 而那正是这些数字的唯一用途（见 Directory.Packages.props 的说明）。
IConfig config = DefaultConfig.Instance
    .AddJob(Job.Default.WithToolchain(InProcessNoEmitToolchain.Instance).WithId("in-process"));

BenchmarkSwitcher
    .FromTypes([typeof(CipherSuiteBenchmarks), typeof(FramingBenchmarks),
                typeof(CompressionBenchmarks), typeof(WireBenchmarks)])
    .Run(args, config);

/// <summary>密码套件的吞吐 —— 这是整条链路上最热的一段。</summary>
[MemoryDiagnoser]
public class CipherSuiteBenchmarks
{
    private byte[] _payload = [];
    private ISshCipherSuite _aesGcm = null!;
    private ISshCipherSuite _chacha = null!;
    private ISshCipherSuite _aesCtr = null!;
    private ArrayBufferWriter<byte> _output = null!;

    /// <summary>载荷大小。32 KiB 是 SSH 通道 max packet 的典型值。</summary>
    [Params(1024, 32 * 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[PayloadSize];
        Random.Shared.NextBytes(_payload);
        _output = new ArrayBufferWriter<byte>(PayloadSize + 256);

        _aesGcm = new AesGcmCipherSuite(new byte[32], new byte[12]);
        _chacha = new ChaCha20Poly1305CipherSuite(new byte[64]);
        _aesCtr = new AesCtrHmacCipherSuite(
            new byte[32], new byte[16], SshMacAlgorithm.HmacSha256, new byte[32],
            encryptThenMac: true);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _aesGcm.Dispose();
        _chacha.Dispose();
        _aesCtr.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int AesGcm()
    {
        _output.ResetWrittenCount();
        _aesGcm.Seal(_payload, 0, _output);
        return _output.WrittenCount;
    }

    [Benchmark]
    public int ChaCha20Poly1305()
    {
        _output.ResetWrittenCount();
        _chacha.Seal(_payload, 0, _output);
        return _output.WrittenCount;
    }

    [Benchmark]
    public int AesCtrHmacEtm()
    {
        _output.ResetWrittenCount();
        _aesCtr.Seal(_payload, 0, _output);
        return _output.WrittenCount;
    }
}

/// <summary>分帧与发送合并 —— README 声称「每轮 syscall 64 → 1~2」。</summary>
[MemoryDiagnoser]
public class FramingBenchmarks
{
    private byte[] _payload = [];

    /// <summary>一轮里连写多少帧。</summary>
    [Params(1, 64)]
    public int FramesPerRound { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[32 * 1024];
        Random.Shared.NextBytes(_payload);
    }

    /// <summary>连写若干帧再刷一次 —— 这是库的实际做法。</summary>
    [Benchmark]
    public async Task<long> WriteManyThenFlushOnce()
    {
        (InMemoryDuplexStream client, InMemoryDuplexStream server) =
            InMemoryTransport.CreatePair(new InMemoryTransportOptions
            {
                PauseWriterThreshold = 64 * 1024 * 1024,
                ResumeWriterThreshold = 32 * 1024 * 1024,
            });

        await using SshPacketTransport transport = new(client);

        for (int i = 0; i < FramesPerRound; i++)
        {
            transport.WritePacket(_payload);
        }
        await transport.FlushAsync();

        long sent = transport.BytesSent;
        await server.DisposeAsync();
        return sent;
    }

    /// <summary>每帧都刷一次 —— 对照组，能看出合并到底省了多少。</summary>
    [Benchmark]
    public async Task<long> FlushEveryFrame()
    {
        (InMemoryDuplexStream client, InMemoryDuplexStream server) =
            InMemoryTransport.CreatePair(new InMemoryTransportOptions
            {
                PauseWriterThreshold = 64 * 1024 * 1024,
                ResumeWriterThreshold = 32 * 1024 * 1024,
            });

        await using SshPacketTransport transport = new(client);

        for (int i = 0; i < FramesPerRound; i++)
        {
            transport.WritePacket(_payload);
            await transport.FlushAsync();
        }

        long sent = transport.BytesSent;
        await server.DisposeAsync();
        return sent;
    }
}

/// <summary>压缩 —— 量的是「值不值得开」。</summary>
[MemoryDiagnoser]
public class CompressionBenchmarks
{
    private byte[] _logLines = [];
    private byte[] _randomData = [];
    private ZlibCompressor _compressor = null!;
    private ArrayBufferWriter<byte> _output = null!;

    [GlobalSetup]
    public void Setup()
    {
        _logLines = Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("2026-09-21 12:00:00 INFO  请求处理完成 耗时=12ms\n", 400)));

        _randomData = new byte[_logLines.Length];
        Random.Shared.NextBytes(_randomData);

        _compressor = new ZlibCompressor();
        _output = new ArrayBufferWriter<byte>(_logLines.Length * 2);
    }

    [GlobalCleanup]
    public void Cleanup() => _compressor.Dispose();

    /// <summary>可压缩的内容（日志、终端输出）。</summary>
    [Benchmark]
    public int CompressibleText()
    {
        _output.ResetWrittenCount();
        _compressor.Compress(_logLines, _output);
        return _output.WrittenCount;
    }

    /// <summary>压不动的内容（已压缩的文件、加密数据）—— 开压缩反而是净亏。</summary>
    [Benchmark]
    public int IncompressibleData()
    {
        _output.ResetWrittenCount();
        _compressor.Compress(_randomData, _output);
        return _output.WrittenCount;
    }
}

/// <summary>SFTP 的 wire 层 —— 每个报文都要走一遍，值得看一眼。</summary>
/// <remarks>
/// 只量公开面。<c>SshDataWriter</c> / <c>SshDataReader</c> 是 internal，
/// 而**为了做基准去把内部类型放开是本末倒置** —— 公开面的形状
/// 不该由基准脚本决定。
/// </remarks>
[MemoryDiagnoser]
public class WireBenchmarks
{
    private byte[] _sftpFrame = [];
    private byte[] _handle = [];
    private byte[] _block = [];

    [GlobalSetup]
    public void Setup()
    {
        _handle = new byte[8];
        _block = new byte[32 * 1024];

        ArrayBufferWriter<byte> sftp = new();
        SftpWire.WriteRead(sftp, 1, _handle, 0, 32768);
        _sftpFrame = sftp.WrittenSpan.ToArray();
    }

    [Benchmark]
    public bool ReadSftpFrame()
    {
        ReadOnlySequence<byte> buffer = new(_sftpFrame);
        return SftpWire.TryReadFrame(ref buffer, out _);
    }

    [Benchmark]
    public int WriteSftpRead()
    {
        ArrayBufferWriter<byte> buffer = new(64);
        SftpWire.WriteRead(buffer, 1, _handle, 0, 32768);
        return buffer.WrittenCount;
    }

    [Benchmark]
    public int WriteSftpWrite()
    {
        ArrayBufferWriter<byte> buffer = new(32 * 1024 + 64);
        SftpWire.WriteWrite(buffer, 1, _handle, 0, _block);
        return buffer.WrittenCount;
    }
}
