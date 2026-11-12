using System.Reflection;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 故障转移 / 换 Key 之后,聊天记录里那张切换提示卡:说清换到了谁(哪一站、哪一把 Key)、为什么换,
/// 而且<b>绝不进模型上下文</b> —— 下一轮发给模型的请求、内存里的 History、存进库里的回复正文都不含它。
/// </summary>
public sealed partial class ChatPanelViewUiTests
{
    private const BindingFlags SwitchFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>
    /// 第一站限流(429,纯对话先原地补试一次)→ 换到备用站答上并粘住:回复里一张卡写明「改用 beta·m」、
    /// 「alpha·m:限流(HTTP 429)」与「之后也发到这里」;下一轮发给备用站的请求里一个字都没有。
    /// </summary>
    [TestMethod]
    public void FailoverNotice_ShowsTargetAndReason_AndNeverReachesTheModel()
    {
        OnUi(async () =>
        {
            using var broken = new KeyAuthStub((_, _) => (429, "{\"error\":{\"message\":\"slow down\"}}"));
            using var backup = new KeyAuthStub((_, _) => (200, LifecycleAnswer));
            using var context = new TestPluginContext();
            var loc = new Loc(context.Host.Locale);
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "m");
            AiProvider second = StubProvider("beta", backup.BaseUrl, "m");
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [first, second],
                ActiveModelId = first.Models[0].Id,
                SuggestFollowUps = false,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }]
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                panel.SendExternal("first question");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1 && !Find<Viewbox>(panel, "StopIcon").IsVisible));

                Assert.HasCount(1, SwitchCards(messages), "切了一次,一张卡");
                string reason = loc.F("SwitchReasonWithStatus", loc["SwitchReasonRateLimit"], 429);
                Assert.AreSequenceEqual(
                    [loc.F("SwitchNoticeTo", "beta·m"), loc.F("SwitchNoticeHop", "alpha·m", reason), loc["SwitchNoticeSticky"]],
                    [.. SwitchLines(messages)]);
                Assert.AreEqual(second.Models[0].Id, (await new AiSettingsStore(context).LoadAsync()).ActiveModelId, "粘住了,卡上那句「之后也发到这里」才成立");

                int before = backup.Requests.Count;
                panel.SendExternal("second question");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 2 && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.HasCount(before + 1, backup.Requests, "粘住的备用站直接正式发送,不再探活");
                string body = backup.Requests[^1].Body;
                Assert.Contains("first question", body, "下一轮的请求确实带着上一轮的历史");
                AssertNoNotice(body, loc);
                foreach (ChatMessage message in PanelHistory(panel)) AssertNoNotice(message.Text, loc);
                Assert.HasCount(1, SwitchCards(messages), "第二轮没切,不加卡");

                // 存进库里的回复正文也不含它:提示记在这条回复的附加信息里
                var history = new ChatHistoryStore(context);
                await history.InitAsync();
                ChatSessionSummary saved = (await history.ListSessionsAsync()).Single();
                IReadOnlyList<ChatEntry> entries = await history.LoadAsync(saved.Id);
                ChatEntry answer = entries.First(entry => entry.Role == "assistant");
                AssertNoNotice(answer.Text, loc);
                ChatProviderSwitch hop = answer.Meta!.Switch!.Hops.Single();
                Assert.AreEqual(("alpha·m", "beta·m", TransientFailure.SwitchRateLimit, 429), (hop.From, hop.To, hop.Reason, hop.Status));
                Assert.IsTrue(answer.Meta.Switch.Sticky);
                Assert.IsNull(entries.Last(entry => entry.Role == "assistant").Meta?.Switch, "第二轮没切,不记");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    /// <summary>
    /// 同一模型换 Key:卡上两把 Key 都按设置页 Key 行的写法点名(「Key 2 · ●●●●2222」),不出现 Key 本身。
    /// 没开轮流时这把 Key 写成当前槽,卡上说「之后也发到这里」;开着轮流时下一轮本来就换下一把,不这么说。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void KeySwitchNotice_NamesBothKeysByOrdinalAndTail(bool balanced)
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((auth, _) => auth == "Bearer key-a-1111"
                ? (401, "{\"error\":{\"message\":\"invalid key\"}}") : (200, ProbeAccountingAnswer));
            using var context = new TestPluginContext();
            var loc = new Loc(context.Host.Locale);
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("pool", endpoint.BaseUrl, "m");
            provider.BalanceApiKeys = balanced;
            var settings = new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false };
            await store.SetApiKeyAsync(provider.Id, "key-a-1111");
            await store.SaveAsync(settings);
            await store.AddProviderApiKeyAsync(settings, provider, "key-b-2222");
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                panel.SendExternal("rotate the key");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1 && !Find<Viewbox>(panel, "StopIcon").IsVisible));

                string station = "pool·m";
                string KeyOf(int ordinal, string tail) => $"{station} · {loc.F("SetupApiKeyOrdinal", ordinal)} · ●●●●{tail}";
                List<string> expected =
                [
                    loc.F("SwitchNoticeTo", KeyOf(2, "2222")),
                    loc.F("SwitchNoticeHop", KeyOf(1, "1111"), loc.F("SwitchReasonWithStatus", loc["SwitchReasonAuth"], 401))
                ];
                if (!balanced) expected.Add(loc["SwitchNoticeSticky"]);
                Assert.AreSequenceEqual([.. expected], [.. SwitchLines(messages)]);
                Assert.DoesNotContain(line => line.Contains("key-a-1111", StringComparison.Ordinal)
                    || line.Contains("key-b-2222", StringComparison.Ordinal), SwitchLines(messages), "只露末四位");
                Assert.AreEqual("Bearer key-b-2222", endpoint.Requests[^1].Auth);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    /// <summary>
    /// 一轮里连着换五站,也只有一张卡:第一行是最后一跳(现在用的是谁),下面列头一跳、中间合成一行计数、再列最后两跳。
    /// </summary>
    [TestMethod]
    public void ManySwitchesInOneTurn_MergeIntoOneCard()
    {
        OnUi(async () =>
        {
            // 同一家、同一把 Key 的六个型号排成链:探活都通,正式请求只有 m-f 答得上,其余回 500
            using var endpoint = new KeyAuthStub((_, body) =>
                body.Contains("Reply with exactly:", StringComparison.Ordinal) || body.Contains("\"model\":\"m-f\"", StringComparison.Ordinal)
                    ? (200, LifecycleAnswer)
                    : (500, "{\"error\":{\"message\":\"upstream\"}}"));
            using var context = new TestPluginContext();
            var loc = new Loc(context.Host.Locale);
            AiProvider provider = StubProvider("chain", endpoint.BaseUrl, "m-a");
            foreach (string model in new[] { "m-b", "m-c", "m-d", "m-e", "m-f" }) provider.Models.Add(new AiModelConfig { Model = model });
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = provider.Models[0].Id,
                SuggestFollowUps = false,
                FailoverChain = [.. provider.Models.Select(model => new FailoverEntry { ModelId = model.Id })]
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                panel.SendExternal("walk the chain");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1 && !Find<Viewbox>(panel, "StopIcon").IsVisible));

                Assert.HasCount(1, SwitchCards(messages), "五跳合成一张卡");
                string server = loc.F("SwitchReasonWithStatus", loc["SwitchReasonServer"], 500);
                Assert.AreSequenceEqual(
                [
                    loc.F("SwitchNoticeTo", "chain·m-f"),
                    loc.F("SwitchNoticeHop", "chain·m-a", server),
                    loc.F("SwitchNoticeMore", 2),
                    loc.F("SwitchNoticeHop", "chain·m-d", server),
                    loc.F("SwitchNoticeHop", "chain·m-e", server),
                    loc["SwitchNoticeSticky"]
                ], [.. SwitchLines(messages)]);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    /// <summary>
    /// 重开会话:卡片照原样恢复(语言跟着当前界面),模型上下文仍只由正文装配 —— 续聊的请求里没有它;
    /// 导出 Markdown 时写成一段引用,与思考、工具调用同属排查记录。
    /// </summary>
    [TestMethod]
    public void SwitchNotice_IsRestoredOnReloadAndExported_ButStaysOutOfTheContext()
    {
        OnUi(async () =>
        {
            using var broken = new KeyAuthStub((_, _) => (401, "{\"error\":{\"message\":\"invalid key\"}}"));
            using var backup = new KeyAuthStub((_, _) => (200, LifecycleAnswer));
            using var context = new TestPluginContext();
            var loc = new Loc(context.Host.Locale);
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "m");
            AiProvider second = StubProvider("beta", backup.BaseUrl, "m");
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [first, second],
                ActiveModelId = first.Models[0].Id,
                SuggestFollowUps = false,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }]
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                panel.SendExternal("before reload");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1 && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                string[] live = [.. SwitchLines(messages)];
                Assert.IsNotEmpty(live);

                var history = new ChatHistoryStore(context);
                await history.InitAsync();
                ChatSessionSummary saved = (await history.ListSessionsAsync()).Single();
                await (Task)typeof(ChatPanelView).GetMethod("LoadConversationAsync", SwitchFlags)!.Invoke(panel, [saved])!;
                messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.HasCount(1, SwitchCards(messages), "重开会话后卡片还在");
                Assert.AreSequenceEqual(live, [.. SwitchLines(messages)], "恢复出来的与当时那张一字不差");
                foreach (ChatMessage message in PanelHistory(panel)) AssertNoNotice(message.Text, loc);

                panel.SendExternal("after reload");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 2 && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                string body = backup.Requests[^1].Body;
                Assert.Contains("before reload", body);
                AssertNoNotice(body, loc);

                IReadOnlyList<ChatEntry> entries = await history.LoadAsync(saved.Id);
                string markdown = (string)typeof(ChatPanelView).GetMethod("BuildMarkdown", SwitchFlags)!.Invoke(panel, [saved, entries])!;
                Assert.Contains("> **" + loc["SwitchNoticeTitle"] + "**", markdown);
                foreach (string line in live) Assert.Contains("> " + line, markdown);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    /// <summary>
    /// 整链都挂(切过去的那一站也失败):卡片留着说明这一轮试过谁、为什么,但不说「之后也发到这里」——
    /// 没答完,选择退回原站,切换没有粘住。
    /// </summary>
    [TestMethod]
    public void WholeChainFails_KeepsTheNoticeWithoutTheStickyLine()
    {
        OnUi(async () =>
        {
            using var broken = new KeyAuthStub((_, _) => (401, "{\"error\":{\"message\":\"invalid key\"}}"));
            // 备用站探活通过、正式请求 503:错误卡是它的,选择退回原站
            using var backup = new KeyAuthStub((_, body) => body.Contains("Reply with exactly:", StringComparison.Ordinal)
                ? (200, LifecycleAnswer) : (503, "{\"error\":{\"message\":\"overloaded\"}}"));
            using var context = new TestPluginContext();
            var loc = new Loc(context.Host.Locale);
            AiProvider first = StubProvider("alpha", broken.BaseUrl, "m");
            AiProvider second = StubProvider("beta", backup.BaseUrl, "m");
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [first, second],
                ActiveModelId = first.Models[0].Id,
                SuggestFollowUps = false,
                FailoverChain = [new FailoverEntry { ModelId = second.Models[0].Id }]
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                panel.SendExternal("nobody answers");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count == 1 && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.Contains(b => b.Classes.Contains("errorCard"), messages.GetVisualDescendants().OfType<Border>());
                Assert.AreSequenceEqual(
                [
                    loc.F("SwitchNoticeTo", "beta·m"),
                    loc.F("SwitchNoticeHop", "alpha·m", loc.F("SwitchReasonWithStatus", loc["SwitchReasonAuth"], 401))
                ], [.. SwitchLines(messages)]);
                Assert.AreEqual(first.Models[0].Id, (await new AiSettingsStore(context).LoadAsync()).ActiveModelId, "没答完,退回原站");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    private static List<Border> SwitchCards(StackPanel messages)
        => [.. messages.GetVisualDescendants().OfType<Border>().Where(border => border.Name == "SwitchNoticeCard")];

    private static List<string> SwitchLines(StackPanel messages)
        => [.. messages.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Name == "SwitchNoticeText").Select(text => text.Text ?? "")];

    private static List<ChatMessage> PanelHistory(ChatPanelView panel)
        => (List<ChatMessage>)typeof(ChatPanelView).GetProperty("History", SwitchFlags)!.GetValue(panel)!;

    /// <summary>提示卡上的字一个都不许出现在发给模型的东西里(只比 ASCII 片段:请求体里的非 ASCII 字符会被 JSON 转义)。</summary>
    private static void AssertNoNotice(string text, Loc loc)
    {
        foreach (string fragment in new[]
                 {
                     loc["SwitchNoticeTitle"], "Now using", loc["SwitchReasonRateLimit"], loc["SwitchReasonAuth"],
                     "(HTTP 4", "(HTTP 5", "billed here"
                 })
        {
            Assert.DoesNotContain(fragment, text, $"切换提示漏进了模型上下文:{fragment}");
        }
    }
}
