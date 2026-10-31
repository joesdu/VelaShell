// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/05-connection.md §4.3

using System.Buffers;
using System.IO.Pipelines;

namespace VelaShell.Ssh.Channels;

/// <summary>一条已经结束的空 <see cref="PipeReader"/>。</summary>
/// <remarks>
/// stderr 被显式丢弃时用它。
/// <b>关键在于它是「立刻结束」而不是「永远没有数据」</b> ——
/// 后者会让读它的调用方挂死，而挂死没有堆栈也没有日志。
/// </remarks>
internal sealed class EmptyPipeReader : PipeReader
{
    private static readonly ReadResult CompletedResult =
        new(ReadOnlySequence<byte>.Empty, isCanceled: false, isCompleted: true);

    /// <inheritdoc />
    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CompletedResult);
    }

    /// <inheritdoc />
    public override bool TryRead(out ReadResult result)
    {
        result = CompletedResult;
        return true;
    }

    /// <inheritdoc />
    public override void AdvanceTo(SequencePosition consumed)
    {
    }

    /// <inheritdoc />
    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
    }

    /// <inheritdoc />
    public override void CancelPendingRead()
    {
    }

    /// <inheritdoc />
    public override void Complete(Exception? exception = null)
    {
    }
}
