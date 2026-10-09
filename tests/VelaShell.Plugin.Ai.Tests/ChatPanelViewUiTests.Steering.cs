using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Auth;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 边跑边补的面板接线:一轮还没答完时按回车,消息排队而不是被"正忙"吞掉,
/// 并且真的会发出去(见 ChatPanelView.Steering.cs)。
/// </summary>
/// <remarks>
/// 这里走的是"一轮结束时队列还没空"的那条路 —— 纯对话模式一轮只发一次请求,
/// 排在流式途中的那句赶不上,于是它作为<b>下一轮</b>整体发出去。
/// 真正的中途插入(函数调用循环每跑一步都送一次)在 <c>SteeringTests</c> 里拿真的循环验。
/// </remarks>
public sealed partial class ChatPanelViewUiTests
{
    /// <summary>
    /// 一段流式回应。本文件的用例都用 <c>hold: true</c> 把它扣在服务端,
    /// 看完"处理中"该看的再 <c>Release()</c> —— 插话的窗口由测试自己开合,不靠延时去赌。
    /// </summary>
    private const string SlowReply = """
    data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"在看了。"},"finish_reason":"stop"}]}

    data: [DONE]


    """;

    private static void PressEnter(TextEditor input) =>
        input.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            Source = input
        });

    private static async Task<(Window Window, ChatPanelView Panel)> ShowWithStubAsync(
        TestPluginContext context, SseStub stub)
    {
        AiProvider provider = StubProvider("stub", stub.BaseUrl, "m");
        await new AiSettingsStore(context).SaveAsync(new AiSettings
        {
            Providers = [provider],
            ActiveModelId = provider.Models[0].Id,
            // 这几个用例只看插话,别让"后续提问"再多发一次请求把断言搅浑
            SuggestFollowUps = false
        });
        return await ShowAsync(context);
    }

    /// <summary>
    /// 处理中按回车 = 排队,不是被吞掉:输入框当场清空、上方多出一枚可撤回的芯片,
    /// 发送键仍在(换成「排队」)而不是被停止键顶掉。等这一轮答完,排队的那句自己发出去。
    /// </summary>
    [TestMethod]
    public void EnterWhileBusy_QueuesTheMessage_AndSendsItWhenTheTurnEnds()
    {
        OnUi(async () =>
        {
            using var stub = new SseStub(SlowReply, hold: true);
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowWithStubAsync(context, stub);
            try
            {
                panel.SendExternal("看看日志");
                Button stop = Find<Button>(panel, "StopButton");
                Assert.IsTrue(await WaitForAsync(() => stop.IsVisible, maxRounds: 60), "这一轮该跑起来了");
                Assert.IsTrue(Find<Button>(panel, "SendButton").IsVisible,
                    "忙的时候发送键也得留着 —— 它此刻是「排队」");
                // 停止键亮起只说明这一轮开跑了。SteeringChatClient 每次发请求前都会把队列取空 ——
                // 回车若赶在第一次请求发出之前,那句话会被并进第一次请求,而不是排着等下一轮。
                Assert.IsTrue(await WaitForAsync(() => stub.Requests.Count >= 1, maxRounds: 120),
                    "这一轮的请求该已经发出去了");
                // 忙态早于首请求:这时入队可能合法并入首请求,必须等请求已发出再测试下一轮。
                await stub.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));

                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.Text = "只看最近一小时的";
                PressEnter(input);

                // 入队要跨一个 await(展开引用可能走一趟 SFTP),芯片是那之后才画的。
                // 固定泵几十毫秒等于赌调度 —— 本机赌得赢,CI 上一忙就赌输(见 plan.md §71-六)。
                WrapPanel queued = Find<WrapPanel>(panel, "QueuedBar");
                Assert.IsTrue(await WaitForAsync(() => queued.Children.Count == 1, maxRounds: 120),
                    "排队的那句该出现在输入框上方");
                Assert.IsTrue(queued.IsVisible, "排队的那句要在输入框上方看得见");
                Assert.IsEmpty(input.Text, "排完队输入框就该空了,否则用户会再敲一次回车");
                Assert.HasCount(1, stub.Requests, "不许打断正在跑的这一次请求");

                // 排队的都看完了,这才让这一轮答完
                stub.Release();
                Assert.IsTrue(await WaitForAsync(() => stub.Requests.Count >= 2, maxRounds: 200),
                    "这一轮答完,排队的那句该自己发出去");
                Assert.Contains("只看最近一小时的", stub.Requests[1],
                    "第二次请求里必须带着那句补充");
                Assert.IsFalse(queued.IsVisible, "送出去之后芯片就该收掉");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 这一轮被按停时,排着的那句原样回到输入框 —— 不替用户自动再发一次:
    /// 按停止本身就是"我要改主意",刚失败的那次多半也还会失败。
    /// </summary>
    [TestMethod]
    public void StoppingTheTurn_PutsTheQueuedMessageBackInTheBox()
    {
        OnUi(async () =>
        {
            // 挂住不回:这一轮要被按停,回应压根不需要来
            using var stub = new SseStub(SlowReply, hold: true);
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowWithStubAsync(context, stub);
            try
            {
                panel.SendExternal("看看日志");
                Button stop = Find<Button>(panel, "StopButton");
                Assert.IsTrue(await WaitForAsync(() => stop.IsVisible, maxRounds: 60), "这一轮该跑起来了");
                // 请求是 stub 那一头记的:界面亮起停止键只说明这一轮开跑了,不代表请求已经落到 stub。
                // 先等它真的记上,下面「只发了一次」才是在断已发生的事实(macOS runner 上就是这么挂的)。
                Assert.IsTrue(await WaitForAsync(() => stub.Requests.Count >= 1, maxRounds: 120),
                    "这一轮的请求该已经发出去了");

                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.Text = "算了,先看磁盘";
                PressEnter(input);
                WrapPanel queuedBar = Find<WrapPanel>(panel, "QueuedBar");
                Assert.IsTrue(await WaitForAsync(() => queuedBar.IsVisible, maxRounds: 120),
                    "得先排上队,才谈得上按停之后退回输入框");

                stop.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

                Assert.IsTrue(await WaitForAsync(() => input.Text.Contains("算了,先看磁盘"), maxRounds: 120),
                    "停掉之后那句该回到输入框");
                Assert.IsFalse(Find<WrapPanel>(panel, "QueuedBar").IsVisible, "回到输入框了就不该还排着");
                Assert.HasCount(1, stub.Requests, "停掉之后不许再自动发一次");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>点一枚排队芯片就是撤回:这一轮答完也不该再把它发出去。</summary>
    [TestMethod]
    public void ClickingAQueuedChip_TakesTheMessageBack()
    {
        OnUi(async () =>
        {
            using var stub = new SseStub(SlowReply, hold: true);
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowWithStubAsync(context, stub);
            try
            {
                panel.SendExternal("看看日志");
                Assert.IsTrue(await WaitForAsync(() => Find<Button>(panel, "StopButton").IsVisible, maxRounds: 60));
                // 必须等第一次请求真的落到 stub 再按回车:SteeringChatClient 每次发请求前都会把队列取空,
                // 回车若赶在请求发出之前,那句话就并进了第一次请求,芯片根本不会出现(macOS runner 上就是这么挂的)。
                Assert.IsTrue(await WaitForAsync(() => stub.Requests.Count >= 1, maxRounds: 120),
                    "这一轮的请求该已经发出去了");
                await stub.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));

                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.Text = "说错了,撤回";
                PressEnter(input);

                WrapPanel queued = Find<WrapPanel>(panel, "QueuedBar");
                Assert.IsTrue(await WaitForAsync(() => queued.Children.Count == 1, maxRounds: 120),
                    "这一句该排在那儿等着被撤回");
                var chip = (Border)queued.Children[0];
                chip.RaiseEvent(new PointerPressedEventArgs(chip, new Pointer(0, PointerType.Mouse, true),
                    chip, default, 0, new PointerPointProperties(), KeyModifiers.None));
                await PumpAsync(5);

                Assert.IsFalse(queued.IsVisible, "撤回之后这一行就该收掉");
                // 撤回完成,这才让这一轮答完 —— 队里已经没人,不该再发第二次
                stub.Release();
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible, maxRounds: 200),
                    "这一轮该正常答完");
                await PumpAsync(20);
                Assert.HasCount(1, stub.Requests, "撤回了就不该再发出去");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EmptyReplay_PreservesDeliveredSteeringCardAndRequestContext(bool failover)
    {
        OnUi(async () =>
        {
            const string whitespace = "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\" \\t\\n \"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            using var first = new SseStub(failover ? whitespace : SlowReply,
                holdFirstStream: true, firstStreamContent: " \t\n ");
            using var backup = new SseStub(SlowReply, firstStreamContent: "OK");
            using var refresh = new HeldCredentialRefresh();
            using var http = new HttpClient(refresh);
            using var context = new TestPluginContext();
            AiProvider original = StubProvider("original", first.BaseUrl, "m");
            original.Auth = AuthMethod.Subscription;
            original.OAuth = new OAuthConfig
            {
                Credential = OAuthCredential.AccessToken, TokenUrl = "https://auth.example/token", ClientId = "test"
            };
            AiProvider next = StubProvider("backup", backup.BaseUrl, "m");
            var store = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
            await store.SaveTokensAsync(original.Id, new OAuthTokens
            {
                AccessToken = "expired", RefreshToken = "refresh", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            await store.SaveAsync(new AiSettings
            {
                Providers = failover ? [original, next] : [original], ActiveModelId = original.Models[0].Id,
                FailoverChain = failover ? [new FailoverEntry { ModelId = next.Models[0].Id }] : [],
                SuggestFollowUps = false
            });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            try
            {
                await PumpAsync();
                panel.SendExternal("FIRST_PROMPT");
                await refresh.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.Text = "STEERING_MARKER";
                PressEnter(input);
                Assert.IsTrue(await WaitForAsync(() => Find<WrapPanel>(panel, "QueuedBar").IsVisible));
                refresh.Release();
                await first.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                List<Border> Cards() => [.. messages.GetVisualDescendants().OfType<Border>()
                    .Where(card => card.Classes.Contains("steeringCard"))];
                Assert.IsTrue(await WaitForAsync(() => Cards().Count == 1), "插话在首个HTTP前已送达并显示");
                Assert.Contains("STEERING_MARKER", first.Requests[0]);
                first.Release();
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count > 0
                    && !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(2, first.Requests, "空白首站只重试一次");
                Assert.IsTrue(first.Requests.All(body => body.Contains("STEERING_MARKER", StringComparison.Ordinal)),
                    "重试不得丢失已送达的插话上下文");
                Assert.HasCount(1, Cards(), "安全重放不得删除已送达插话卡");
                Assert.AreEqual("STEERING_MARKER", Cards()[0].GetVisualDescendants()
                    .OfType<SelectableTextBlock>().Single().Text);
                Assert.AreEqual("在看了。", AnswerRenderers(messages).Single().MarkdownBuilder.ToString());
                if (failover)
                {
                    Assert.HasCount(2, backup.Requests);
                    Assert.Contains("STEERING_MARKER", backup.Requests[1], "备用正式请求也须保留送达上下文");
                    Assert.AreEqual(next.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                }
                else Assert.IsEmpty(backup.Requests);
            }
            finally { refresh.Release(); first.Release(); panel.Detach(); window.Close(); }
        });
    }
}
