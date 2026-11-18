using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>一轮工具的装配:按登记顺序收集、按模式取舍、停用清单统一过滤、调用方的同步上下文不丢(ai-plan §12.2 的 1c)。</summary>
[TestClass]
public sealed class TurnToolAssemblyTests
{
    private static AIFunction Tool(string name) => AIFunctionFactory.Create(() => "ok", name);

    [TestMethod]
    public async Task BuildAsync_CollectsInRegistrationOrder_OnlyFromSourcesForThisMode()
    {
        IList<AITool> tools = await new TurnToolAssembly(ChatMode.Plan, new HashSet<string>())
            .Add(m => m != ChatMode.Chat, () => [Tool("builtin_a"), Tool("builtin_b")])
            .Add(_ => true, () => [Tool("images")])
            .Add(m => m == ChatMode.Agent, () => [Tool("agent_only")])
            .Add(m => m != ChatMode.Chat, () => [Tool("note")])
            .BuildAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "builtin_a", "builtin_b", "images", "note" }, tools.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public async Task BuildAsync_DisabledListAppliesToEverySource()
    {
        int created = 0;
        IList<AITool> tools = await new TurnToolAssembly(ChatMode.Agent, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NOTE_FOR_USER", "write_file" })
            .Add(_ => true, () => { created++; return [Tool("read_file"), Tool("write_file")]; })
            .Add(_ => true, () => [Tool("note_for_user")])
            .BuildAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "read_file" }, tools.Select(t => t.Name).ToArray());
        Assert.AreEqual(1, created);
    }

    [TestMethod]
    public async Task BuildAsync_ChatModeGetsNothingUnlessASourceAsksForIt()
    {
        IList<AITool> tools = await new TurnToolAssembly(ChatMode.Chat, new HashSet<string>())
            .Add(m => m != ChatMode.Chat, () => throw new AssertFailedException("对话模式不该去造内置工具"))
            .BuildAsync(CancellationToken.None);
        Assert.IsEmpty(tools);
    }

    /// <summary>来源的回调(桌面的状态行)要回到调用方的上下文:装配内部不能 ConfigureAwait(false)。</summary>
    [TestMethod]
    public void BuildAsync_KeepsTheCallersSynchronizationContextAcrossAsyncSources()
    {
        var context = new RecordingContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext? seenAfterAwait = null;
        Task<IList<AITool>> build;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            build = new TurnToolAssembly(ChatMode.Agent, new HashSet<string>())
                .Add(new YieldingSource(() => seenAfterAwait = SynchronizationContext.Current))
                .BuildAsync(CancellationToken.None);
            context.RunUntil(build);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        build.GetAwaiter().GetResult();
        Assert.AreSame(context, seenAfterAwait, "来源 await 之后仍在调用方的上下文上");
    }

    private sealed class YieldingSource(Action afterYield) : ITurnToolSource
    {
        public bool AppliesTo(ChatMode mode) => true;

        public async ValueTask ContributeAsync(IList<AITool> tools, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            afterYield();
        }
    }

    /// <summary>单线程的同步上下文:投递进来的续体都在调用 <see cref="RunUntil" /> 的线程上依次执行。</summary>
    private sealed class RecordingContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public void RunUntil(Task task)
        {
            task.ContinueWith(_ => _queue.CompleteAdding(), TaskScheduler.Default);
            foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable()) callback(state);
        }
    }
}
