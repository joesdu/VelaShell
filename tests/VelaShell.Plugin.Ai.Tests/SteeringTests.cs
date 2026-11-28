using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 边跑边补:一轮还没答完时补发的消息,要在模型<b>下一步之前</b>进到上下文里。
/// </summary>
/// <remarks>
/// 最要紧的那条(<see cref="FunctionInvocationLoop_HandsTheQueuedMessage_ToTheNextStep" />)
/// 把真的 <c>FunctionInvokingChatClient</c> 跑起来 —— 插话通道能不能生效,取决于那个循环
/// 每跑一步是不是都会重新穿过内层客户端。这件事只有真跑一遍才作数。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class SteeringTests
{
    private static SteeringMessage Note(string text)
        => new(text, text, new ChatMessage(ChatRole.User, text));

    private static ChatResponseUpdate Text(string text)
        => new(ChatRole.Assistant, text);

    private static ChatResponseUpdate Call(string callId, string name)
        => new(ChatRole.Assistant, [new FunctionCallContent(callId, name, null)]);

    /// <summary>一份"系统提示词 + 一问"的最小上下文(装配器出来的东西就长这样)。</summary>
    private static List<ChatMessage> Context() =>
    [
        new(ChatRole.System, "你是助手"),
        new(ChatRole.User, "把日志翻一遍")
    ];

    [TestMethod]
    public async Task QueuedMessage_ReachesTheModel_OnTheVeryNextRequest()
    {
        var queue = new SteeringQueue();
        var inner = new ScriptedChatClient([Text("好的")]);
        using var steering = new SteeringChatClient(inner, queue);

        queue.Enqueue(Note("只看最近一小时的"));
        _ = await steering.GetResponseAsync(Context());

        Assert.HasCount(1, inner.Requests);
        Assert.EndsWith("只看最近一小时的", inner.Requests[0][^1].Text, "插话该排在这次请求的最后");
    }

    /// <summary>用量明细的观察点:拿到的是并好插话、真正发出去的那一份;观察者出错不连累请求。</summary>
    [TestMethod]
    public async Task OnRequest_SeesTheComposedRequest_AndItsFailureDoesNotBreakTheRequest()
    {
        var queue = new SteeringQueue();
        var inner = new ScriptedChatClient([Text("好的")]);
        IReadOnlyList<ChatMessage>? seen = null;
        using var steering = new SteeringChatClient(inner, queue, onRequest: (messages, _) =>
        {
            seen = messages;
            throw new InvalidOperationException("统计出错");
        });

        queue.Enqueue(Note("只看最近一小时的"));
        ChatResponse response = await steering.GetResponseAsync(Context());

        Assert.AreEqual("好的", response.Text);
        Assert.IsNotNull(seen);
        Assert.AreSequenceEqual(inner.Requests[0], seen.ToList(), "观察的就是发出去的那一份");
        Assert.EndsWith("只看最近一小时的", seen[^1].Text);
    }

    /// <summary>
    /// 末条也是 user 时并进去,不能另起一条 —— Anthropic 要求角色交替,
    /// 而适配器不会替你合并(与 <c>ContextBuilder</c> 同一个理由)。
    /// </summary>
    [TestMethod]
    public async Task Interjection_MergesIntoATrailingUserMessage_InsteadOfDoublingTheRole()
    {
        var queue = new SteeringQueue();
        var inner = new ScriptedChatClient([Text("好的")]);
        using var steering = new SteeringChatClient(inner, queue);

        queue.Enqueue(Note("顺便看看磁盘"));
        _ = await steering.GetResponseAsync(Context());

        List<ChatMessage> sent = inner.Requests[0];
        Assert.HasCount(2, sent, "不该多出一条挨着的 user");
        Assert.AreEqual(ChatRole.User, sent[^1].Role);
        Assert.HasCount(2, sent[^1].Contents, "两段内容并在同一条 user 里");
        Assert.Contains("把日志翻一遍", sent[^1].Text);
        Assert.Contains("顺便看看磁盘", sent[^1].Text);
    }

    /// <summary>
    /// 插话内容排在原有内容<b>之后</b>:那条 user 往往装着工具结果,
    /// 而 Anthropic 要求 <c>tool_result</c> 块排在最前面,插到前面直接被拒。
    /// </summary>
    [TestMethod]
    public async Task Interjection_GoesAfterToolResults_NotBeforeThem()
    {
        var queue = new SteeringQueue();
        var inner = new ScriptedChatClient([Text("好的")]);
        using var steering = new SteeringChatClient(inner, queue);

        List<ChatMessage> context =
        [
            new(ChatRole.System, "你是助手"),
            new(ChatRole.User, "看看日志"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "read", null)]),
            new(ChatRole.User, [new FunctionResultContent("c1", "…日志…")])
        ];
        queue.Enqueue(Note("只要 error 行"));
        _ = await steering.GetResponseAsync(context);

        IList<AIContent> tail = inner.Requests[0][^1].Contents;
        Assert.IsInstanceOfType<FunctionResultContent>(tail[0], "工具结果必须仍排在最前");
        Assert.IsInstanceOfType<TextContent>(tail[^1]);
    }

    /// <summary>
    /// 循环把上下文整份重发时,之前送过的插话也得跟着重发 —— 那份历史是循环自己攒的,
    /// 它并不知道我们往里加过东西。
    /// </summary>
    [TestMethod]
    public async Task DeliveredInterjections_StayInContext_OnLaterSteps()
    {
        var queue = new SteeringQueue();
        var inner = new ScriptedChatClient([Text("一")], [Text("二")]);
        using var steering = new SteeringChatClient(inner, queue);

        queue.Enqueue(Note("补充条件"));
        _ = await steering.GetResponseAsync(Context());
        // 第二步:循环重新发一份历史下来(没有新插话)
        _ = await steering.GetResponseAsync(Context());

        Assert.HasCount(2, inner.Requests);
        Assert.Contains("补充条件", inner.Requests[1][^1].Text, "第二步仍要看得到那句补充");
    }

    /// <summary>
    /// 系统提示词不在消息里的时候(Codex 那条路把它挪进了 <c>instructions</c>),
    /// 之前送过的插话照样要跟着重发。
    /// </summary>
    /// <remarks>
    /// 曾经拿"消息里还有没有系统提示词"去猜循环是不是只发了增量(以为那是服务端替我们存着会话),
    /// 结果 Codex 那条路一猜就错:插过的话从第二步起就从上下文里消失了。
    /// 现在每条协议的循环都整份重发(Responses 也不续接,见 <c>StatelessResponsesChatClient</c>),不再猜。
    /// </remarks>
    [TestMethod]
    public async Task WithoutASystemMessage_DeliveredInterjectionsAreStillResent()
    {
        var queue = new SteeringQueue();
        var inner = new ScriptedChatClient([Text("一")], [Text("二")]);
        using var steering = new SteeringChatClient(inner, queue);
        List<ChatMessage> context = [new(ChatRole.User, "把日志翻一遍")];

        queue.Enqueue(Note("补充条件"));
        _ = await steering.GetResponseAsync(context);
        _ = await steering.GetResponseAsync(context);

        Assert.Contains("补充条件", inner.Requests[1][^1].Text, "第二步仍要看得到那句补充");
    }

    [TestMethod]
    public async Task Delivered_ListsWhatWentOut_AndFiresTheCallbackOnce()
    {
        var queue = new SteeringQueue();
        var inner = new ScriptedChatClient([Text("一")], [Text("二")]);
        int fired = 0;
        using var steering = new SteeringChatClient(inner, queue, () => fired++);

        queue.Enqueue(Note("第一句"));
        _ = await steering.GetResponseAsync(Context());
        _ = await steering.GetResponseAsync(Context()); // 这一步没有新插话

        Assert.HasCount(1, steering.Delivered);
        Assert.AreEqual("第一句", steering.Delivered[0].DisplayText);
        Assert.AreEqual(1, fired, "只有真的送出去新东西时才该回调");
    }

    /// <summary>
    /// <b>这条是整件事的地基</b>:插话通道垫在函数调用循环里面,循环每跑一步都要穿过它,
    /// 所以工具跑到一半时补的那句,能赶在模型决定下一步之前进上下文。
    /// </summary>
    [TestMethod]
    public async Task FunctionInvocationLoop_HandsTheQueuedMessage_ToTheNextStep()
    {
        var queue = new SteeringQueue();
        var inner = new ScriptedChatClient([Call("c1", "peek")], [Call("c2", "peek")], [Text("完成。")]);
        var steering = new SteeringChatClient(inner, queue);
        IChatClient client = steering.AsBuilder().UseFunctionInvocation().Build();

        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "…日志…", "peek")]
        };

        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(Context(), options))
        {
            foreach (FunctionResultContent result in update.Contents.OfType<FunctionResultContent>())
                queue.Enqueue(Note(result.CallId == "c1" ? "只看最近一小时的" : "按时间排序"));
        }

        Assert.HasCount(3, inner.Requests);
        Assert.DoesNotContain(m => m.Text.Contains("最近一小时"), inner.Requests[0],
            "第一步的时候用户还没开口");
        Assert.Contains("只看最近一小时的", inner.Requests[1][^1].Text,
            "第二步之前必须把那句补充送进去 —— 这正是边跑边补要的效果");
        List<string> third = [.. inner.Requests[2].SelectMany(m => m.Contents).Select(c => c switch
        {
            FunctionCallContent call => call.CallId,
            FunctionResultContent result => $"result:{result.CallId}",
            TextContent text => text.Text,
            _ => ""
        })];
        Assert.AreSequenceEqual(["c1", "result:c1", "只看最近一小时的", "c2", "result:c2", "按时间排序"],
            third.Skip(2).ToList());
        Assert.HasCount(2, steering.Delivered);
        client.Dispose();
    }

    [TestMethod]
    public async Task RetryPrompt_FollowsTheDeliveredNote_InOneUserMessage()
    {
        var queue = new SteeringQueue();
        var inner = new ScriptedChatClient();
        using var steering = new SteeringChatClient(inner, queue);
        List<ChatMessage> context = Context();
        context.Add(new(ChatRole.Assistant, [new FunctionCallContent("c1", "peek", null)]));
        context.Add(new(ChatRole.Tool, [new FunctionResultContent("c1", "out")]));
        queue.Enqueue(Note("补充条件"));
        _ = await steering.GetResponseAsync(context);

        context.Add(new(ChatRole.User, RecoveringChatClient.ContinueAfterTools));
        _ = await steering.GetResponseAsync(context);

        List<ChatMessage> retry = inner.Requests[1];
        Assert.HasCount(context.Count, retry);
        Assert.AreEqual(ChatRole.Tool, retry[^2].Role);
        Assert.AreSequenceEqual(["补充条件", RecoveringChatClient.ContinueAfterTools],
            retry[^1].Contents.OfType<TextContent>().Select(c => c.Text));
        Assert.HasCount(1, context[^1].Contents, "补试传入的原始消息不应被改写");
    }

    [TestMethod]
    public async Task Compaction_KeepsNotesFromFoldedSteps_BeforeTheRemainingSteps()
    {
        var queue = new SteeringQueue();
        var inner = new ScriptedChatClient();
        var steering = new SteeringChatClient(inner, queue);
        using var compactor = new RunContextCompactor(steering, 1000, 0,
            (_, _, _) => Task.FromResult<string?>("progress"))
        { PrefixCount = 2 };
        List<ChatMessage> context = Context();
        AddStep("c1", "first");
        queue.Enqueue(Note("补充条件"));
        _ = await compactor.GetResponseAsync(context);

        for (int i = 2; i <= 6; i++) AddStep($"c{i}", new string('x', 1600));
        _ = await compactor.GetResponseAsync(context);

        List<ChatMessage> sent = inner.Requests[1];
        Assert.AreEqual("progress", compactor.Summary);
        Assert.DoesNotContain(c => c.CallId == "c1", sent.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.AreEqual(ChatRole.User, sent[1].Role);
        Assert.EndsWith("补充条件", sent[1].Text);
        Assert.AreEqual(ChatRole.Assistant, sent[2].Role);
        Assert.DoesNotContain(m => m.Text.Contains("补充条件"), sent.Skip(2));

        void AddStep(string id, string text)
        {
            context.Add(new(ChatRole.Assistant, [new TextContent(text), new FunctionCallContent(id, "peek", null)]));
            context.Add(new(ChatRole.Tool, [new FunctionResultContent(id, "out")]));
        }
    }

    [TestMethod]
    public void DrainMerged_FoldsEverythingWaiting_IntoOneTurn()
    {
        var queue = new SteeringQueue();
        queue.Enqueue(Note("第一句"));
        queue.Enqueue(Note("第二句"));

        SteeringMessage? merged = queue.DrainMerged();

        Assert.IsNotNull(merged);
        Assert.AreEqual("第一句\n\n第二句", merged.DisplayText);
        Assert.AreEqual("第一句\n\n第二句", merged.RawText);
        Assert.HasCount(2, merged.Message.Contents, "两条的内容都要带上(图片附件也在里头)");
        Assert.AreEqual(0, queue.Count, "并完就该空了");
    }

    [TestMethod]
    public void DrainMerged_OnAnEmptyQueue_GivesNothing()
        => Assert.IsNull(new SteeringQueue().DrainMerged());

    [TestMethod]
    public void Remove_TakesBackOneWaitingMessage()
    {
        var queue = new SteeringQueue();
        SteeringMessage first = Note("撤回我");
        queue.Enqueue(first);
        queue.Enqueue(Note("留着"));

        Assert.IsTrue(queue.Remove(first));
        Assert.HasCount(1, queue.Snapshot());
        Assert.AreEqual("留着", queue.Snapshot()[0].DisplayText);
        Assert.IsFalse(queue.Remove(first), "撤过一次就不在队里了");
    }

    /// <summary>按脚本逐次作答的假客户端,并把每次收到的消息序列原样留下。</summary>
    private sealed class ScriptedChatClient(params IReadOnlyList<ChatResponseUpdate>[] script) : IChatClient
    {
        private readonly Queue<IReadOnlyList<ChatResponseUpdate>> _script = new(script);

        /// <summary>每一次请求收到的消息(第 0 次 = 第一步)。</summary>
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Next(messages).ToChatResponse());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (ChatResponseUpdate update in Next(messages))
            {
                yield return update;
            }
            await Task.CompletedTask;
        }

        private IReadOnlyList<ChatResponseUpdate> Next(IEnumerable<ChatMessage> messages)
        {
            Requests.Add([.. messages]);
            return _script.Count > 0 ? _script.Dequeue() : [];
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
