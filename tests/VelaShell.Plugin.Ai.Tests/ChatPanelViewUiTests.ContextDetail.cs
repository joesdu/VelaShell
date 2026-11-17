using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    /// <summary>
    /// 输入框右下角的用量:悬停给按来源分段的面板,点击打开逐项的上下文明细。
    /// 分类来自那次真实请求(经插话通道观察到的那一份),合计按供应商报告的输入缩放。
    /// </summary>
    [TestMethod]
    public void Usage_HoverShowsCategories_AndClickOpensTheContextDetail()
    {
        OnUi(async () =>
        {
            await using var stub = new SseStub("""
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"done"},"finish_reason":null}]}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":1000,"completion_tokens":50,"total_tokens":1050}}

            data: [DONE]


            """);

            using var context = new TestPluginContext();
            AiProvider provider = StubProvider("stub", stub.BaseUrl, "m", maxInputTokens: 10000);
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = provider.Models[0].Id,
                SuggestFollowUps = false
            });

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                Grid area = Find<Grid>(panel, "UsageArea");
                Popup popup = Find<Popup>(panel, "ContextPopup");

                TextBlock usage = Find<TextBlock>(panel, "UsageText");
                panel.SendExternal("hi");
                Assert.IsTrue(await WaitForAsync(() => !string.IsNullOrEmpty(usage.Text) && !usage.Text.Contains('~')),
                    "等这一轮结束(估算换成报告值)");
                Assert.IsTrue(await WaitForAsync(() => ToolTip.GetTip(area) is UsageDetailView hover
                    && Shown(hover).Contains("Messages")),
                    "一轮结束后悬停面板按来源分行;实际:" + (ToolTip.GetTip(area) is UsageDetailView h
                        ? string.Join(" | ", Shown(h)) : ToolTip.GetTip(area)?.GetType().Name ?? "null"));

                var detail = (UsageDetailView)ToolTip.GetTip(area)!;
                string[] shown = Shown(detail);
                Assert.Contains("System prompt", shown, "系统提示词单独一行");
                Assert.Contains("1k", shown, "合计是供应商报告的 1000,不是本地估算");
                Assert.Contains(t => t.Contains("Click for the full breakdown", StringComparison.Ordinal), shown,
                    "面板底部提示可以点开明细");

                // 点击:打开明细,悬停面板让位
                Click(panel, "UsageButton");
                await PumpAsync(5);
                Assert.IsTrue(popup.IsOpen);
                Assert.IsNull(ToolTip.GetTip(area), "明细开着时不再弹悬停面板");
                var full = (ContextDetailView)Find<ContentControl>(panel, "ContextDetailHost").Content!;
                string text = full.ToString();
                Assert.Contains("Context details: 1,000 / 10,000", text);
                Assert.Contains("System prompt", text);
                Assert.Contains("Messages", text);
                Assert.Contains("Your messages", text, "消息一类展开到用户 / 助手 / 工具结果");
                Assert.IsNotEmpty(full.GetLogicalDescendants().OfType<Expander>().ToList(), "各类可以展开");

                // Esc 关掉,悬停面板挂回去
                Find<Border>(panel, "ContextPopupBody").RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Escape,
                    Source = Find<Border>(panel, "ContextPopupBody")
                });
                await PumpAsync(5);
                Assert.IsFalse(popup.IsOpen);
                Assert.IsInstanceOfType<UsageDetailView>(ToolTip.GetTip(area));
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    private static string[] Shown(Control view)
        => [.. view.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text ?? "")];
}
