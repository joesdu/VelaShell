using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Auth;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.TimeSeries;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    private const string LifecycleAnswer = """
        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"回答"},"finish_reason":"stop"}]}

        data: [DONE]


        """;

    [TestMethod]
    public void BackgroundFailoverCompaction_PreservesForegroundStatusAndRestoresOwnStatus()
    {
        OnUi(async () =>
        {
            await using var history = new SseStub(LifecycleAnswer);
            await using var broken = new SseStub("data: [DONE]\n\n", holdFirstStream: true, firstStreamContent: "");
            await using var backup = new SseStub(LifecycleAnswer, jsonContent: "background-summary", holdFirstNonStream: true);
            using var context = new TestPluginContext();
            context.FakeSessions.AddConnected(host: "10.0.0.1", username: "root");
            context.FakeSessions.AddConnected(host: "10.0.0.2", username: "root");
            AiProvider original = StubProvider("history", history.BaseUrl, "m");
            AiProvider failed = StubProvider("broken", broken.BaseUrl, "m");
            AiProvider small = StubProvider("small", backup.BaseUrl, "m", maxInputTokens: 40);
            small.Models[0].MaxTokens = 1;
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [original, failed, small],
                ActiveModelId = original.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = small.Models[0].Id }],
                CompactContext = true,
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                ComboBox sessions = Find<ComboBox>(panel, "SessionCombo");
                TextBlock status = Find<TextBlock>(panel, "StatusText");
                StackPanel messagesA = Find<StackPanel>(panel, "MessagesPanel");
                for (int turn = 1; turn <= 4; turn++)
                {
                    panel.SendExternal($"历史第{turn}条问题");
                    int completed = turn;
                    Assert.IsTrue(await WaitForAsync(() => Footers(messagesA).Count >= completed));
                }

                sessions.SelectedIndex = 1;
                await PumpAsync();
                StackPanel messagesB = Find<StackPanel>(panel, "MessagesPanel");
                panel.SendExternal("B 的问题");
                Assert.IsTrue(await WaitForAsync(() => Footers(messagesB).Count == 1
                    && !Find<Viewbox>(panel, "StopIcon").IsVisible));
                string statusB = status.Text ?? "";
                Assert.IsNotEmpty(statusB, "B 的真实回答完成后应保留未请求推理的提示状态");

                sessions.SelectedIndex = 0;
                await PumpAsync();
                Find<ListBox>(panel, "ProviderList").SelectedIndex = 1;
                panel.SendExternal("第五条触发故障转移和后台压缩");
                await broken.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(10));
                // 首个请求在真实HTTP端挂住后再切走，确保发送已绑定A会话。
                sessions.SelectedIndex = 1;
                broken.Release();
                Assert.IsTrue(await WaitForAsync(() => backup.Requests.Any(body =>
                    body.Contains("You are compacting", StringComparison.Ordinal))));
                Assert.AreEqual(statusB, status.Text, "后台 A 开始压缩不能覆盖前台 B 的状态");

                sessions.SelectedIndex = 0;
                await PumpAsync();
                var loc = (Loc)typeof(ChatPanelView).GetField("_loc",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
                Assert.AreEqual(loc["Compacting"], status.Text, "切回 A 应恢复 A 自己的压缩状态");
                sessions.SelectedIndex = 1;
                await PumpAsync();
                Assert.AreEqual(statusB, status.Text);

                backup.Release();
                Assert.IsTrue(await WaitForAsync(() => Footers(messagesA).Count == 5));
                await PumpAsync();
                Assert.AreEqual(statusB, status.Text, "后台 A 压缩 finally 和回答收尾不能清空 B 的状态");
                Assert.HasCount(3, backup.Requests, "备用站必须完成真实探活、非流式摘要和正式回答");
                Assert.Contains("You are compacting", backup.Requests[1]);
                Assert.DoesNotContain("\"stream\":true", backup.Requests[1]);
                Assert.Contains("background-summary", backup.Requests[2], "正式回答必须消费已生成的摘要");
                sessions.SelectedIndex = 0;
                await PumpAsync();
                Assert.AreSame(messagesA, Find<StackPanel>(panel, "MessagesPanel"));
                Assert.IsEmpty(status.Text ?? "", "A 回答结束后应清除自己的临时压缩状态");
                Assert.AreEqual("回答", AnswerRenderers(messagesA)[^1].MarkdownBuilder.ToString());
            }
            finally { broken.Release(); backup.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void Catalog_LateAutomaticModelsPreserveCooling_ConfigurationSaveClearsIt()
    {
        OnUi(async () =>
        {
            using var endpoint = new HeldLifecycleModelsEndpoint();
            using var context = new TestPluginContext();
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = endpoint.BaseUrl + "/v1";
            provider.Models[0].Model = "configured-model";
            provider.Models[0].SupportsReasoning = false; // 清单同为false，本场景仅刷新列表、不改请求能力。
            string modelId = provider.Models[0].Id;
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = modelId,
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? settingsHost = null;
            try
            {
                var health = (ProviderHealth)typeof(ChatPanelView)
                    .GetField("_health", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
                health.Record(modelId, false);
                Assert.IsTrue(health.IsCooling(modelId));
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var settings = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                settingsHost = Host(settings);
                settings.GetControl<Button>("AddButton")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync(5);
                ProviderSetupView catalog = settings.GetLogicalDescendants().OfType<ProviderSetupView>().Single();
                catalog.FocusEntry(provider.CatalogId, provider.Id);
                await PumpAsync(5);
                T CatalogControl<T>(string name) where T : Control => catalog.GetLogicalDescendants()
                    .OfType<T>().Single(control => control.Name == name);
                int saves = 0;
                var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                catalog.ProviderChanged += _ => saves++;
                catalog.ModelsChanged += () => pulled.TrySetResult();
                CatalogControl<TextBox>("SetupKeyBox").Text = "sk-first";
                CatalogControl<Button>("SetupPrimaryButton")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await endpoint.Requested.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual("GET /v1/models HTTP/1.1", endpoint.RequestLine);
                Assert.AreEqual(1, saves, "先完成真实配置保存,再进入自动型号拉取");
                Assert.IsFalse(health.IsCooling(modelId), "ProviderChanged 必须先清除旧配置冷却");
                health.Record(modelId, false);
                int evidenceVersion = health.Version;
                Assert.IsTrue(health.IsCooling(modelId), "HTTP 在途期间记录新的失败证据");

                endpoint.Release();
                await pulled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsTrue(await WaitForAsync(() => CatalogControl<Button>("SetupPrimaryButton").IsEnabled));
                ComboBox picker = CatalogControl<ComboBox>("SetupModelPicker");
                Assert.Contains("configured-model", [.. picker.ItemsSource!.Cast<string>()],
                    "真实目录窗口必须展示晚到的型号清单");
                AiProvider persisted = (await store.LoadAsync()).Providers.Single();
                Assert.AreEqual(provider.Id, persisted.Id);
                Assert.AreEqual(modelId, persisted.Models[0].Id);
                Assert.AreEqual("configured-model", persisted.Models[0].Model);
                Assert.Contains("configured-model", persisted.AvailableModels, "成功 HTTP 型号结果必须落库");
                Assert.AreEqual(evidenceVersion, health.Version, "ModelsChanged 不得推进健康证据代数");
                Assert.IsTrue(health.IsCooling(modelId), "晚到的型号目录不能抹掉共享失败冷却");

                CatalogControl<TextBox>("SetupNameBox").Text = "saved configuration";
                CatalogControl<TextBox>("SetupKeyBox").Text = "sk-second";
                var secondPull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                catalog.ModelsChanged += () => secondPull.TrySetResult();
                CatalogControl<Button>("SetupPrimaryButton")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.IsTrue(await WaitForAsync(() => saves == 2));
                Assert.IsFalse(health.IsCooling(modelId), "下一次真实配置保存仍须清除共享冷却");
                Assert.IsGreaterThan(evidenceVersion, health.Version);
                Assert.AreEqual("saved configuration", (await store.LoadAsync()).Providers.Single().Name);
                Assert.AreEqual("sk-second", await store.GetApiKeyAsync(provider.Id));
                await secondPull.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual(2, endpoint.Requests, "第二次真实型号请求已完成并应用结果");
                Assert.IsTrue(await WaitForAsync(() => CatalogControl<Button>("SetupPrimaryButton").IsEnabled),
                    "后续配置保存的自动型号拉取也须完成,不能留下在途请求");
            }
            finally
            {
                endpoint.Release();
                settingsHost?.Close();
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void Catalog_ChangedRequestModelInvalidatesOnlyItsOldHealthEvidence()
    {
        OnUi(async () =>
        {
            using var endpoint = new HeldLifecycleModelsEndpoint();
            using var context = new TestPluginContext();
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = endpoint.BaseUrl + "/v1";
            provider.Models[0].Model = "old-model";
            AiModelConfig automatic = provider.Models[0];
            ModelsDevCatalog.Apply(automatic, new ModelSpec(automatic.Model, automatic.Name, automatic.MaxInputTokens,
                automatic.MaxTokens, 0, 0, 0, null), newModel: true);
            var untouched = new AiModelConfig { Model = "untouched-model" };
            provider.Models.Add(untouched);
            string editedId = provider.Models[0].Id;
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = editedId });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? settingsHost = null;
            try
            {
                var health = (ProviderHealth)typeof(ChatPanelView)
                    .GetField("_health", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var settings = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                settingsHost = Host(settings);
                RaiseClick(settings.GetControl<Button>("AddButton"));
                await PumpAsync(5);
                ProviderSetupView catalog = settings.GetLogicalDescendants().OfType<ProviderSetupView>().Single();
                catalog.FocusEntry(provider.CatalogId, provider.Id);
                await PumpAsync(5);
                T CatalogControl<T>(string name) where T : Control => catalog.GetLogicalDescendants()
                    .OfType<T>().Single(control => control.Name == name);
                var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                catalog.ModelsChanged += () => pulled.TrySetResult();
                CatalogControl<TextBox>("SetupKeyBox").Text = "sk-local";
                RaiseClick(CatalogControl<Button>("SetupPrimaryButton"));
                await endpoint.Requested.WaitAsync(TimeSpan.FromSeconds(10));
                int version = health.Version;
                long oldRequest = health.BeginObservation();
                health.Record(editedId, false, version, oldRequest);
                health.Record(untouched.Id, false, version, health.BeginObservation());
                endpoint.Release();
                await pulled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                AiProvider saved = (await store.LoadAsync()).Providers.Single();
                Assert.AreEqual(editedId, saved.Models[0].Id);
                Assert.AreEqual("configured-model", saved.Models[0].Model, "真实目录确实更换了同ID的请求型号");
                Assert.IsFalse(health.IsCooling(editedId), "旧型号失败不能阻止新型号被自动选择");
                Assert.IsTrue(health.IsCooling(untouched.Id), "目录没有改动的模型必须保留冷却");
                Assert.AreEqual(version, health.Version, "目录不清全局健康证据代数");
                health.Record(editedId, false, version, oldRequest);
                Assert.IsFalse(health.IsCooling(editedId), "晚到的旧型号失败不能写回新型号");
                health.Record(editedId, false, version, health.BeginObservation());
                Assert.IsTrue(health.IsCooling(editedId), "新请求的失败仍须冷却");
                Assert.IsTrue(await WaitForAsync(() => CatalogControl<Button>("SetupPrimaryButton").IsEnabled));
            }
            finally
            {
                endpoint.Release(); settingsHost?.Close(); panel.Detach(); window.Close();
            }
        });
    }

    [TestMethod]
    public void Catalog_InFlightSaveKeepsNewEvidenceWhenCatalogueDoesNotChangeRequestFields()
    {
        OnUi(async () =>
        {
            using var endpoint = new HeldLifecycleModelsEndpoint();
            using var context = new TestPluginContext();
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.ModelsExpanded = true;
            provider.BaseUrl = endpoint.BaseUrl + "/v1";
            provider.Models[0].Model = "configured-model";
            provider.Models[0].SupportsReasoning = false;
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? settingsHost = null;
            try
            {
                var health = (ProviderHealth)typeof(ChatPanelView)
                    .GetField("_health", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var settings = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                settingsHost = Host(settings);
                RaiseClick(settings.GetControl<Button>("AddButton"));
                await PumpAsync(5);
                ProviderSetupView catalog = settings.GetLogicalDescendants().OfType<ProviderSetupView>().Single();
                catalog.FocusEntry(provider.CatalogId, provider.Id);
                await PumpAsync(5);
                T CatalogControl<T>(string name) where T : Control => catalog.GetLogicalDescendants()
                    .OfType<T>().Single(control => control.Name == name);
                var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                catalog.ModelsChanged += () => pulled.TrySetResult();
                CatalogControl<TextBox>("SetupKeyBox").Text = "sk-local";
                RaiseClick(CatalogControl<Button>("SetupPrimaryButton"));
                await endpoint.Requested.WaitAsync(TimeSpan.FromSeconds(10));

                settings.GetControl<ListBox>("ProvidersList").SelectedIndex = 1;
                Assert.IsTrue(await WaitForAsync(() => settings.GetControl<TextBox>("ModelBox").Text == "configured-model"));
                settings.GetControl<TextBox>("MaxTokensBox").Text = "1000";
                RaiseClick(settings.GetControl<Button>("SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => settings.GetControl<TextBlock>("StatusText").Text == new Loc("en")["Saved"]));
                int version = health.Version;
                health.Record(provider.Models[0].Id, false, version, health.BeginObservation());

                endpoint.Release();
                await pulled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual(1000, (await store.LoadAsync()).Providers[0].Models[0].MaxTokens);
                Assert.IsTrue(health.IsCooling(provider.Models[0].Id),
                    "目录没有改请求字段时，不能把请求在途期间保存后记录的新失败当旧证据抹掉");
                Assert.AreEqual(version, health.Version);
                Assert.IsTrue(await WaitForAsync(() => CatalogControl<Button>("SetupPrimaryButton").IsEnabled));
            }
            finally
            {
                endpoint.Release(); settingsHost?.Close(); panel.Detach(); window.Close();
            }
        });
    }

    private sealed class HeldLifecycleModelsEndpoint : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public string BaseUrl { get; }
        public string? RequestLine { get; private set; }
        public Task Requested => _requested.Task;

        public HeldLifecycleModelsEndpoint()
        {
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync();
                    using NetworkStream stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    RequestLine = await reader.ReadLineAsync();
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                    Interlocked.Increment(ref _requests);
                    _requested.TrySetResult();
                    await _released.Task;
                    byte[] payload = Encoding.UTF8.GetBytes("""{"data":[{"id":"configured-model"}]}""");
                    byte[] header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header);
                    await stream.WriteAsync(payload);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // 收尾释放尚未到达的连接或仍被门控的响应。
            }
        }

        public void Release() => _released.TrySetResult();
        public void Dispose()
        {
            _released.TrySetCanceled();
            _listener.Stop();
        }
    }

    private sealed class FailedCredentialRefresh(int status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException(status == 0 ? "refresh offline" : "refresh rejected", null,
                status == 0 ? null : (HttpStatusCode)status);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Suggestions_FailoverCandidate_BindsParametersAndUsageToOriginalConversation(bool switchSession)
    {
        OnUi(async () =>
        {
            await using var broken = new ErrorStub();
            await using var candidate = new SseStub(LifecycleAnswer, jsonContent: "CANDIDATE_SUGGESTION", holdFirstNonStream: true);
            await using var other = new SseStub(LifecycleAnswer, jsonContent: "OTHER_SESSION_SUGGESTION");
            using var context = new TestPluginContext();
            context.FakeSessions.AddConnected(host: "10.0.0.1", username: "root");
            context.FakeSessions.AddConnected(host: "10.0.0.2", username: "root");
            AiProvider first = StubProvider("broken", broken.BaseUrl, "first-model");
            AiProvider backup = StubProvider("candidate", candidate.BaseUrl, "candidate-model");
            backup.Models[0].Reasoning = ReasoningLevel.High;
            backup.Models[0].SupportsReasoning = true;
            backup.UnsupportedParameters = "max_output_tokens";
            AiProvider next = StubProvider("other", other.BaseUrl, "other-model");
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [first, backup, next],
                ActiveModelId = first.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = backup.Models[0].Id }]
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("ORIGINAL_SESSION_PROMPT");
                Assert.IsTrue(await WaitForAsync(() => candidate.Requests.Any(body => body.Contains("Suggest 3", StringComparison.Ordinal))));
                Task originalSuggestion = SuggestionTask(panel)!;
                Assert.IsNotNull(originalSuggestion);
                Assert.HasCount(3, candidate.Requests, "候选只有探活、正文和建议三次请求");
                Assert.Contains("\"reasoning_effort\":\"high\"", candidate.Requests[0], "正向对照:候选探活确实使用自己的 High 配置");
                Assert.Contains("\"reasoning_effort\":\"high\"", candidate.Requests[1]);
                using var body = System.Text.Json.JsonDocument.Parse(candidate.Requests[2]);
                Assert.AreEqual("candidate-model", body.RootElement.GetProperty("model").GetString());
                Assert.IsFalse(body.RootElement.TryGetProperty("max_tokens", out _));
                Assert.IsFalse(body.RootElement.TryGetProperty("max_completion_tokens", out _));
                Assert.IsFalse(body.RootElement.TryGetProperty("tools", out _));
                Assert.DoesNotContain("\"reasoning_effort\":\"high\"", candidate.Requests[2], "建议不能继承昂贵的候选 High 思考");
                Assert.Contains("ORIGINAL_SESSION_PROMPT", candidate.Requests[2]);
                Assert.Contains("回答", candidate.Requests[2]);
                if (switchSession)
                {
                    Find<ComboBox>(panel, "SessionCombo").SelectedIndex = 1;
                    await FinishSuggestionsAsync(originalSuggestion);
                    Assert.IsEmpty(Find<TextBlock>(panel, "UsageText").Text ?? "", "取消的候选建议不能结算到新会话");
                    Find<ListBox>(panel, "ProviderList").SelectedIndex = 2;
                    panel.SendExternal("OTHER_SESSION_PROMPT");
                    Assert.IsTrue(await WaitForAsync(() => !ReferenceEquals(SuggestionTask(panel), originalSuggestion)
                        && SuggestionTask(panel) is not null));
                    await FinishSuggestionsAsync(SuggestionTask(panel)!);
                    candidate.Release();
                    Assert.HasCount(2, other.Requests);
                    Assert.DoesNotContain("ORIGINAL_SESSION_PROMPT", other.Requests[1]);
                    Assert.Contains("OTHER_SESSION_PROMPT", other.Requests[1]);
                    Assert.Contains("OTHER_SESSION_SUGGESTION", string.Join("|", Find<WrapPanel>(panel, "SuggestionBar")
                        .GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text)));
                    Find<ComboBox>(panel, "SessionCombo").SelectedIndex = 0;
                    await PumpAsync();
                    Assert.IsEmpty(Find<TextBlock>(panel, "UsageText").Text ?? "", "旧会话也不能把已取消的建议 usage 补结算");
                    Find<ComboBox>(panel, "SessionCombo").SelectedIndex = 1;
                    await PumpAsync();
                }
                else
                {
                    candidate.Release();
                    await FinishSuggestionsAsync(originalSuggestion);
                    Assert.Contains("CANDIDATE_SUGGESTION", string.Join("|", Find<WrapPanel>(panel, "SuggestionBar")
                        .GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text)));
                    Assert.IsEmpty(other.Requests);
                }
                Assert.Contains("Conversation total: in 1 / out 1", ToolTip.GetTip(Find<Grid>(panel, "UsageArea"))?.ToString() ?? "",
                    "只结算当前会话自己完成的建议 usage");
                Assert.HasCount(3, candidate.Requests, "会话切换和用量刷新不能重发候选建议");
            }
            finally { candidate.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void NewChat_CancelledOldStreamUsage_DoesNotPolluteNewConversation()
    {
        OnUi(async () =>
        {
            await using var target = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"正在回答"},"finish_reason":null}],"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}}

                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: [DONE]


                """, holdAfterFirstChunk: true);
            using var context = new TestPluginContext();
            AiProvider provider = StubProvider("stream", target.BaseUrl, "m");
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = provider.Models[0].Id,
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("旧会话");
                Assert.IsTrue(await WaitForAsync(() => AnswerRenderers(Find<StackPanel>(panel, "MessagesPanel")).Count > 0),
                    "正文和 usage 同一个真实 SSE 事件已消费，流尚未结束");
                Click(panel, "NewChatButton");
                Assert.IsTrue(await WaitForAsync(() => !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.IsEmpty(Find<TextBlock>(panel, "UsageText").Text ?? "", "旧流取消结算不属于已重置的新会话");
                Assert.IsFalse(Find<TextBlock>(panel, "TokenFlowText").IsVisible);
                target.Release();
                panel.SendExternal("新会话");
                Assert.IsTrue(await WaitForAsync(() => !Find<Viewbox>(panel, "StopIcon").IsVisible));
                string tip = ToolTip.GetTip(Find<Grid>(panel, "UsageArea"))?.ToString() ?? "";
                Assert.Contains("Conversation total: in 7 / out 3", tip, "新会话只结算自己的真实请求");
                Assert.AreEqual("↑7 ↓3", Find<TextBlock>(panel, "TokenFlowText").Text);
            }
            finally { target.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void StickyFailover_OriginalModelDeleted_CancellationKeepsValidBackup()
    {
        OnUi(async () =>
        {
            await using var broken = new ErrorStub();
            using var backup = new HeldStreamingReply();
            await using var unused = new SseStub(LifecycleAnswer, jsonContent: "OK");
            using var context = new TestPluginContext();
            AiProvider c = StubProvider("c", unused.BaseUrl, "m");
            AiProvider a = StubProvider("a", broken.BaseUrl, "m");
            AiProvider b = StubProvider("b", backup.BaseUrl, "m");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [c, a, b],
                ActiveModelId = a.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = b.Models[0].Id }],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? host = null;
            try
            {
                panel.SendExternal("备用站挂起时删除原模型");
                Assert.IsTrue(await WaitForAsync(() => backup.StreamRequest.IsCompleted));
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var editor = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(editor);
                await PumpAsync(10);
                FindIn<ListBox>(editor, "ProvidersList").SelectedIndex = 3; // c,c/m,a,a/m,b,b/m
                await PumpAsync(5);
                RaiseClick(FindIn<Button>(editor, "DeleteButton"));
                Assert.IsTrue(await WaitForAsync(() => Find<ListBox>(panel, "ProviderList").ItemCount == 2));
                Assert.AreEqual(b.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                Click(panel, "SendButton");
                Assert.IsTrue(await WaitForAsync(() => !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.AreEqual(b.Models[0].Id, (await store.LoadAsync()).ActiveModelId, "原 A 已删除，不可把不存在的 ID 写回或兜底选第一 C");
                Assert.AreEqual(1, Find<ListBox>(panel, "ProviderList").SelectedIndex);
                Assert.IsEmpty(unused.Requests, "删除非当前模型不该让取消选择转移到 C");
            }
            finally { backup.Release(); host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CancelledFailover_DelayedHistoryPreservesReopenedSelectionOrRollsBackSamePanel(bool reopen)
    {
        OnUi(async () =>
        {
            await using var broken = new ErrorStub();
            await using var backup = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"半截回答"},"finish_reason":null}]}

                data: [DONE]


                """, holdAfterFirstChunk: true, firstStreamContent: "OK");
            await using var unused = new SseStub(LifecycleAnswer, jsonContent: "OK");
            ITimeSeriesApi timeSeries = DispatchProxy.Create<ITimeSeriesApi, DelayedHistoryApi>();
            var gate = (DelayedHistoryApi)timeSeries;
            gate.Inner = new InMemoryTimeSeries();
            using var context = new TestPluginContext { TimeSeries = timeSeries };
            AiProvider a = StubProvider("a", broken.BaseUrl, "m");
            AiProvider b = StubProvider("b", backup.BaseUrl, "m");
            AiProvider c = StubProvider("c", unused.BaseUrl, "m");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [a, b, c],
                ActiveModelId = a.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = b.Models[0].Id }],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? reopenedWindow = null;
            ChatPanelView? reopened = null;
            try
            {
                panel.SendExternal("保留半截回答但不覆盖新选择");
                Assert.IsTrue(await WaitForAsync(() => AnswerRenderers(Find<StackPanel>(panel, "MessagesPanel")).Count > 0));
                Assert.AreEqual(b.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                if (reopen) panel.Detach();
                else Click(panel, "SendButton");
                Assert.IsTrue(await WaitForAsync(() => gate.Entered.Task.IsCompleted), "旧取消已到不可取消的 assistant 历史写入");
                if (reopen)
                {
                    (reopenedWindow, reopened) = await ShowAsync(context);
                    Find<ListBox>(reopened, "ProviderList").SelectedIndex = 2;
                    await PumpAsync(5);
                    Assert.AreEqual(c.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                }
                gate.Release.TrySetResult();
                Assert.IsTrue(await WaitForAsync(() => !Find<Viewbox>(panel, "StopIcon").IsVisible));
                Assert.AreEqual(reopen ? c.Models[0].Id : a.Models[0].Id, (await store.LoadAsync()).ActiveModelId,
                    "关闭的面板不能回滚共享配置;同面板取消仍应回滚");
                if (reopened is not null) Assert.AreEqual(2, Find<ListBox>(reopened, "ProviderList").SelectedIndex);
                var history = new ChatHistoryStore(context);
                await history.InitAsync();
                IReadOnlyList<ChatSessionSummary> sessions = await history.ListSessionsAsync();
                IReadOnlyList<ChatEntry> entries = await history.LoadAsync(sessions.Single().Id);
                Assert.Contains(entry => entry.Role == "assistant" && entry.Text == "半截回答", entries,
                    "面板关闭不应阻止已消费正文的历史结算");
                Assert.IsEmpty(unused.Requests, "新选择不应重放已出正文的旧请求");
            }
            finally
            {
                gate.Release.TrySetResult(); backup.Release();
                reopened?.Detach(); reopenedWindow?.Close(); panel.Detach(); window.Close();
            }
        });
    }

    private static Task? SuggestionTask(ChatPanelView panel) => panel.SuggestionTask;

    private static async Task FinishSuggestionsAsync(Task task)
    {
        Assert.IsTrue(await WaitForAsync(() => task.IsCompleted), "必须等整个建议任务收尾,不能只等 HTTP handler 返回");
        await task;
    }


    public class DelayedHistoryApi : DispatchProxy
    {
        public ITimeSeriesApi Inner { get; set; } = null!;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            object? result = targetMethod!.Invoke(Inner, args);
            return targetMethod.Name == nameof(ITimeSeriesApi.OpenAsync)
                ? WrapAsync((Task<ITimeSeries>)result!) : result;
        }
        private async Task<ITimeSeries> WrapAsync(Task<ITimeSeries> opening)
        {
            ITimeSeries inner = await opening;
            if (inner.Name != "chat_messages") return inner;
            ITimeSeries proxy = Create<ITimeSeries, DelayedHistorySeries>();
            var wrapper = (DelayedHistorySeries)proxy;
            wrapper.Inner = inner;
            wrapper.Gate = this;
            return proxy;
        }
    }

    public class DelayedHistorySeries : DispatchProxy
    {
        public ITimeSeries Inner { get; set; } = null!;
        public DelayedHistoryApi Gate { get; set; } = null!;
        private int _writes;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.Name == nameof(ITimeSeries.WriteAsync) && ++_writes == 2
                ? WriteAfterReleaseAsync(targetMethod, args) : targetMethod.Invoke(Inner, args);
        private async Task WriteAfterReleaseAsync(MethodInfo method, object?[]? args)
        {
            Gate.Entered.TrySetResult();
            await Gate.Release.Task;
            await (Task)method.Invoke(Inner, args)!;
        }
    }

}
