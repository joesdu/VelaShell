// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/design/architecture.md §10.2

using System.IO.Pipelines;

namespace VelaShell.Ssh.Transport;

/// <summary>
/// 一对在内存里互联的双工流：一端写出的字节，另一端读得到。
/// </summary>
/// <remarks>
/// <para>
/// <b>它是「协议测试不需要网络、不需要容器」这条路的地基</b>
/// （<see href="https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/ssh/design/architecture.md">velashell-docs/zh/ssh/design/architecture.md</see> §10.2）。
/// 有了它，分帧、状态机、通道窗口、SFTP 管线的绝大部分用例都能毫秒级跑完，
/// 因而可以进每次提交的门禁 —— 而不是被排除在门禁之外、等到某次手动跑集成测试才发现问题。
/// </para>
/// <para>
/// 它也是一个可用的生产特性：把 <see cref="ISshTransportDialer"/> 指到别处
/// （进程内的另一个组件、一条已有的隧道）时，这对流就是那条路的载体。
/// </para>
/// <para>
/// 这对流本身是零时延、无限带宽的。要测自适应通道窗口（velashell-docs/zh/ssh/spec/05-connection.md §3.3）
/// 与 SFTP 管线深度（velashell-docs/zh/ssh/spec/06-sftp.md §5.2），在一端套上 <see cref="DelayedStream"/>，
/// 按 <see cref="LinkCharacteristics"/> 加单向时延与带宽上限（仅供测试，<c>internal</c>）。
/// </para>
/// </remarks>
public static class InMemoryTransport
{
    /// <summary>创建一对互联的双工流。</summary>
    /// <param name="options">缓冲参数；<see langword="null"/> 时用默认值。</param>
    /// <returns>两端。第一端写出的字节，第二端读得到，反之亦然。</returns>
    public static InMemoryStreamPair CreatePair(
        InMemoryTransportOptions? options = null)
    {
        options ??= new InMemoryTransportOptions();

        PipeOptions pipeOptions = new(
            pauseWriterThreshold: options.PauseWriterThreshold,
            resumeWriterThreshold: options.ResumeWriterThreshold,
            useSynchronizationContext: false);

        Pipe firstToSecond = new(pipeOptions);
        Pipe secondToFirst = new(pipeOptions);

        InMemoryDuplexStream first = new(secondToFirst.Reader, firstToSecond.Writer);
        InMemoryDuplexStream second = new(firstToSecond.Reader, secondToFirst.Writer);
        return new InMemoryStreamPair(first, second);
    }

    /// <summary>
    /// 一个把 <see cref="CreatePair"/> 的一端交出去的拨号器，另一端留给测试的服务端桩。
    /// </summary>
    /// <param name="onAccepted">
    /// 每次拨号成功时以「服务端那一端」回调。测试在这里挂上报文脚本或服务端桩。
    /// </param>
    /// <param name="options">缓冲参数。</param>
    public static ISshTransportDialer CreateDialer(
        Func<InMemoryDuplexStream, SshDialTarget, CancellationToken, ValueTask> onAccepted,
        InMemoryTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(onAccepted);
        return new InMemoryDialer(onAccepted, options);
    }

    private sealed class InMemoryDialer(
        Func<InMemoryDuplexStream, SshDialTarget, CancellationToken, ValueTask> onAccepted,
        InMemoryTransportOptions? options) : ISshTransportDialer
    {
        public SshDialKind Kind => SshDialKind.InMemory;

        public async ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(target);
            (InMemoryDuplexStream client, InMemoryDuplexStream server) = CreatePair(options);
            await onAccepted(server, target, cancellationToken).ConfigureAwait(false);
            return client;
        }
    }
}

