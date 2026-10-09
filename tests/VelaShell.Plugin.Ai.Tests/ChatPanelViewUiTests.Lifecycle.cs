using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Reflection;
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
            using var history = new SseStub(LifecycleAnswer);
            using var broken = new SseStub("data: [DONE]\n\n", holdFirstStream: true, firstStreamContent: "");
            using var backup = new SseStub(LifecycleAnswer, jsonContent: "background-summary", holdFirstNonStream: true);
            using var context = new TestPluginContext();
            context.FakeSessions.AddConnected(host: "10.0.0.1", username: "root");
            context.FakeSessions.AddConnected(host: "10.0.0.2", username: "root");
            AiProvider original = StubProvider("history", history.BaseUrl, "m");
            AiProvider failed = StubProvider("broken", broken.BaseUrl, "m");
            AiProvider small = StubProvider("small", backup.BaseUrl, "m", maxInputTokens: 40);
            small.Models[0].MaxTokens = 1;
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [original, failed, small], ActiveModelId = original.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = small.Models[0].Id }],
                CompactContext = true, SuggestFollowUps = false
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
                    && !Find<Button>(panel, "StopButton").IsVisible));
                string statusB = status.Text ?? "";
                Assert.IsNotEmpty(statusB, "B 的真实回答完成后应保留未请求推理的提示状态");

                sessions.SelectedIndex = 0;
                await PumpAsync();
                Find<ComboBox>(panel, "ProviderCombo").SelectedIndex = 1;
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
                Providers = [provider], ActiveModelId = modelId, SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? settingsHost = null, catalogHost = null;
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
                var catalog = (ProviderSetupView)context.FakeUi.LastPanel.CreateContent();
                catalogHost = Host(catalog);
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
                Assert.Contains("configured-model", picker.ItemsSource!.Cast<string>().ToArray(),
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
                Assert.IsTrue(health.Version > evidenceVersion);
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
                catalogHost?.Close();
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
            Window? settingsHost = null, catalogHost = null;
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
                var catalog = (ProviderSetupView)context.FakeUi.LastPanel.CreateContent();
                catalogHost = Host(catalog);
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
                endpoint.Release(); catalogHost?.Close(); settingsHost?.Close(); panel.Detach(); window.Close();
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
            provider.BaseUrl = endpoint.BaseUrl + "/v1";
            provider.Models[0].Model = "configured-model";
            provider.Models[0].SupportsReasoning = false;
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            Window? settingsHost = null, catalogHost = null;
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
                var catalog = (ProviderSetupView)context.FakeUi.LastPanel.CreateContent();
                catalogHost = Host(catalog);
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
                endpoint.Release(); catalogHost?.Close(); settingsHost?.Close(); panel.Detach(); window.Close();
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

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(400, false)]
    [DataRow(401, false)]
    [DataRow(403, false)]
    [DataRow(400, true)]
    [DataRow(401, true)]
    [DataRow(403, true)]
    [DataRow(404, false)]
    public void InitialOAuthRefreshFailure_AuthRejectionFallsBackAndApiDecidesSwitch(int refreshStatus, bool apiRejects)
    {
        OnUi(async () =>
        {
            using var original = new SseStub(LifecycleAnswer, jsonContent: "OK");
            using var denied = new ErrorStub();
            using var backup = new SseStub(LifecycleAnswer, jsonContent: "OK");
            using var refresh = new FailedCredentialRefresh(refreshStatus);
            using var http = new HttpClient(refresh);
            using var context = new TestPluginContext();
            AiProvider a = StubProvider("subscription", apiRejects ? denied.BaseUrl : original.BaseUrl, "m");
            AiProvider b = StubProvider("backup", backup.BaseUrl, "m");
            a.Auth = AuthMethod.Subscription;
            a.OAuth = new OAuthConfig
            {
                Credential = OAuthCredential.AccessToken, TokenUrl = "https://auth.example/token", ClientId = "test"
            };
            var store = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
            await store.SaveTokensAsync(a.Id, new OAuthTokens
            {
                AccessToken = "expired", RefreshToken = "refresh", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            await store.SaveAsync(new AiSettings
            {
                Providers = [a, b], ActiveModelId = a.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = b.Models[0].Id }], SuggestFollowUps = false
            });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            try
            {
                await PumpAsync();
                panel.SendExternal("首站刷新失败");
                Assert.IsTrue(await WaitForAsync(() => refresh.Calls > 0 && !Find<Button>(panel, "StopButton").IsVisible));
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                if (refreshStatus == 404)
                {
                    Assert.IsEmpty(original.Requests, "坏刷新端点不可伪装授权拒绝后发送旧令牌");
                    Assert.IsEmpty(backup.Requests, "404 配置错误不是换站理由");
                    Assert.IsEmpty(AnswerRenderers(messages));
                    Assert.AreEqual(a.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                }
                else if (refreshStatus == 0 || apiRejects)
                {
                    Assert.IsEmpty(original.Requests);
                    Assert.AreEqual(apiRejects ? 1 : 0, denied.Requests,
                        "只有授权拒绝回退后正式 API 的 401 才触发鉴权换站;网络异常不发送过期令牌");
                    Assert.HasCount(2, backup.Requests, "先探活备用站再发正式流");
                    Assert.Contains("Reply with exactly:", backup.Requests[0]);
                    Assert.DoesNotContain("Reply with exactly:", backup.Requests[1]);
                    Assert.AreEqual("回答", AnswerRenderers(messages).Single().MarkdownBuilder.ToString());
                    Assert.AreEqual(b.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                }
                else
                {
                    Assert.HasCount(1, original.Requests, "400/401/403 刷新拒绝应沿用旧令牌让正式 API 决定");
                    Assert.AreEqual("Bearer expired", original.Authorizations.Single());
                    Assert.Contains("首站刷新失败", original.Requests.Single());
                    Assert.IsEmpty(backup.Requests, "正式 API 成功时不得仅因刷新拒绝切站");
                    Assert.AreEqual("回答", AnswerRenderers(messages).Single().MarkdownBuilder.ToString());
                    Assert.AreEqual(a.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                }
                Assert.AreEqual("expired", (await store.GetTokensAsync(a.Id))!.AccessToken, "失败的刷新不得覆盖原令牌");
            }
            finally { panel.Detach(); window.Close(); }
        });
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
    public void FirstStation_SettingsChangedDuringCredentialPreparation_UsesBackupOrLocalizedError(bool useBackup)
    {
        OnUi(async () =>
        {
            using var original = new SseStub(LifecycleAnswer);
            using var moved = new SseStub(LifecycleAnswer);
            using var backup = new SseStub(LifecycleAnswer, firstStreamContent: "OK");
            using var refresh = new HeldCredentialRefresh();
            using var http = new HttpClient(refresh);
            using var context = new TestPluginContext();
            File.WriteAllText(Path.Combine(context.DataDirectory, "models-dev.json"), "{}");
            AiProvider a = StubProvider("subscription", original.BaseUrl, "m");
            AiProvider b = StubProvider("backup", backup.BaseUrl, "m");
            a.Auth = AuthMethod.Subscription;
            a.OAuth = new OAuthConfig
            {
                Credential = OAuthCredential.AccessToken, TokenUrl = "https://auth.example/token", ClientId = "test"
            };
            var store = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
            await store.SaveTokensAsync(a.Id, new OAuthTokens
            {
                AccessToken = "old-token", RefreshToken = "refresh", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            await store.SaveAsync(new AiSettings
            {
                Providers = useBackup ? [a, b] : [a], ActiveModelId = a.Models[0].Id,
                FailoverChain = useBackup ? [new FailoverEntry { ModelId = b.Models[0].Id }] : [], SuggestFollowUps = false
            });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            Window? host = null;
            try
            {
                await PumpAsync();
                panel.SendExternal("protected-turn");
                Assert.IsTrue(await WaitForAsync(() => refresh.Started.Task.IsCompleted), "首站确实挂在凭据准备,尚未创建正文请求");
                Click(panel, "SettingsButton");
                await PumpAsync(5);
                var editor = (SettingsView)context.FakeUi.LastPanel.CreateContent();
                host = Host(editor);
                await PumpAsync(10);
                FindIn<ListBox>(editor, "ProvidersList").SelectedIndex = 0;
                await PumpAsync(5);
                FindIn<TextBox>(editor, "ProviderBaseUrlBox").Text = moved.BaseUrl;
                RaiseClick(FindIn<Button>(editor, "SaveButton"));
                Assert.IsTrue(await WaitForAsync(() => FindIn<TextBlock>(editor, "StatusText").Text == "Saved."));
                Assert.AreEqual(moved.BaseUrl, (await store.LoadAsync()).Providers[0].BaseUrl);
                var loc = (Loc)typeof(ChatPanelView).GetField("_loc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
                loc.Switch("zh-Hans");
                refresh.Release();
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                Assert.IsEmpty(original.Requests, "刷新结束后不得使用已失效的原地址发送");
                Assert.IsEmpty(moved.Requests, "未经验证的新地址不得收到刷新后的凭据与正文");
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                if (useBackup)
                {
                    Assert.HasCount(2, backup.Requests, "首站准备期配置失效也必须在 hop 内允许探活后切站");
                    Assert.Contains("Reply with exactly:", backup.Requests[0]);
                    Assert.Contains("protected-turn", backup.Requests[1]);
                    Assert.AreEqual("回答", AnswerRenderers(messages).Single().MarkdownBuilder.ToString());
                    Assert.AreEqual(b.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                }
                else
                {
                    Assert.IsEmpty(backup.Requests);
                    Border card = messages.GetVisualDescendants().OfType<Border>().Single(item => item.Classes.Contains("errorCard"));
                    string detail = card.GetVisualDescendants().OfType<SelectableTextBlock>()
                        .Single(block => block.Classes.Contains("mono")).Text ?? "";
                    Assert.AreEqual(loc["ErrorProviderChanged"], detail);
                    Assert.DoesNotContain("old-token", detail);
                }
            }
            finally { refresh.Release(); host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void FailoverCandidate_ForbiddenOAuthDestination_SkipsToHealthyCandidate()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var forbidden = new SseStub(LifecycleAnswer, jsonContent: "OK");
            using var healthy = new SseStub(LifecycleAnswer, jsonContent: "OK");
            using var context = new TestPluginContext();
            AiProvider a = StubProvider("a", broken.BaseUrl, "m");
            AiProvider b = StubProvider("b", forbidden.BaseUrl, "m");
            AiProvider c = StubProvider("c", healthy.BaseUrl, "m");
            b.CatalogId = "openai-codex";
            b.Auth = AuthMethod.Subscription;
            b.OAuth = new OAuthConfig { Credential = OAuthCredential.AccessToken };
            var store = new AiSettingsStore(context);
            await store.SaveTokensAsync(b.Id, new OAuthTokens { AccessToken = "must-not-leak" });
            await store.SaveAsync(new AiSettings
            {
                Providers = [a, b, c], ActiveModelId = a.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = b.Models[0].Id }, new FailoverEntry { ModelId = c.Models[0].Id }],
                SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("跳过不安全的订阅目标");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(2, healthy.Requests, "B 凭据主机检查失败不该中断 C 的探活及正式流");
                Assert.IsEmpty(forbidden.Requests, "内置 OAuth 令牌绝不可泄漏到配置的本地主机");
                Assert.AreEqual(c.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                Assert.IsNotEmpty(AnswerRenderers(Find<StackPanel>(panel, "MessagesPanel")));
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Suggestions_CredentialVerificationCancelled_DoesNotSendOrDisplay(bool detach)
    {
        OnUi(async () =>
        {
            using var target = new HeldStreamingReply();
            using var refresh = new HeldCredentialRefresh();
            using var http = new HttpClient(refresh);
            using var context = new TestPluginContext();
            AiProvider provider = StubProvider("subscription", target.BaseUrl, "m");
            provider.Auth = AuthMethod.Subscription;
            provider.OAuth = new OAuthConfig
            {
                Credential = OAuthCredential.AccessToken, TokenUrl = "https://auth.example/token", ClientId = "test"
            };
            var store = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
            await store.SaveTokensAsync(provider.Id, new OAuthTokens
            {
                AccessToken = "same-token", RefreshToken = "refresh", AccountId = "original-account",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });
            await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            try
            {
                await PumpAsync();
                panel.SendExternal("建议必须属于这一轮");
                Assert.IsTrue(await WaitForAsync(() => target.StreamRequest.IsCompleted));
                await store.SaveTokensAsync(provider.Id, new OAuthTokens
                {
                    AccessToken = "same-token", RefreshToken = "refresh", AccountId = "original-account",
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(20)
                });
                target.Release();
                Assert.IsTrue(await WaitForAsync(() => refresh.Started.Task.IsCompleted), "真实回答后，建议凭据验证正等待 OAuth 刷新");
                Task suggestionTask = SuggestionTask(panel)!;
                Assert.IsNotNull(suggestionTask);
                if (detach) panel.Detach();
                else Click(panel, "NewChatButton");
                Assert.IsTrue(await WaitForAsync(() => refresh.Cancelled.Task.IsCompleted), "清会话或关闭必须能取消建议的验证 await");
                refresh.Release();
                Assert.IsTrue(await WaitForAsync(() => refresh.Finished.Task.IsCompleted));
                await FinishSuggestionsAsync(suggestionTask);
                Assert.AreEqual(0, target.FollowUpRequests, "失效验证不能启动附带 HTTP 请求");
                Assert.IsFalse(Find<WrapPanel>(panel, "SuggestionBar").IsVisible);
                Assert.IsEmpty(Find<TextBlock>(panel, "UsageText").Text ?? "", "失效建议不能刷新新会话用量");
            }
            finally { refresh.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Suggestions_RefreshedToken_SendsOnlyForSameAccount(bool changedAccount)
    {
        OnUi(async () =>
        {
            using var target = new SseStub(LifecycleAnswer,
                jsonContent: "接下来检查什么\n如何定位错误\n怎样验证修复", holdAfterFirstChunk: true);
            string payload = changedAccount
                ? "{\"access_token\":\"new-token\",\"expires_in\":3600,\"id_token\":\"e30."
                    + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"account_id\":\"other-account\"}")).TrimEnd('=')
                    + ".x\"}"
                : "{\"access_token\":\"new-token\",\"expires_in\":3600}";
            using var refresh = new HeldCredentialRefresh(payload);
            using var http = new HttpClient(refresh);
            using var context = new TestPluginContext();
            AiProvider provider = StubProvider("subscription", target.BaseUrl, "m");
            provider.Auth = AuthMethod.Subscription;
            provider.OAuth = new OAuthConfig
            {
                Credential = OAuthCredential.AccessToken, TokenUrl = "https://auth.example/token", ClientId = "test",
                AccountIdClaim = "account_id"
            };
            var store = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
            var tokens = new OAuthTokens
            {
                AccessToken = "old-token", RefreshToken = "refresh", AccountId = "original-account",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };
            await store.SaveTokensAsync(provider.Id, tokens);
            await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            try
            {
                await PumpAsync();
                panel.SendExternal("这一轮回答应继续给出建议");
                Assert.IsTrue(await WaitForAsync(() => target.Requests.Count == 1));
                tokens.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(20);
                await store.SaveTokensAsync(provider.Id, tokens);
                target.Release();
                Assert.IsTrue(await WaitForAsync(() => refresh.Started.Task.IsCompleted));
                Task suggestionTask = SuggestionTask(panel)!;
                Assert.IsNotNull(suggestionTask);
                refresh.Release();
                Assert.IsTrue(await WaitForAsync(() => refresh.Finished.Task.IsCompleted));
                await FinishSuggestionsAsync(suggestionTask);
                if (changedAccount)
                {
                    Assert.HasCount(1, target.Requests, "续期换账号不可把原会话内容发送给新账号");
                    Assert.IsFalse(Find<WrapPanel>(panel, "SuggestionBar").IsVisible);
                }
                else
                {
                    Assert.IsTrue(await WaitForAsync(() => Find<WrapPanel>(panel, "SuggestionBar").IsVisible));
                    Assert.HasCount(2, target.Requests);
                    Assert.AreEqual("Bearer new-token", target.Authorizations[1]);
                    Assert.Contains("接下来检查什么", string.Join("|", Find<WrapPanel>(panel, "SuggestionBar")
                        .GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text)));
                }
                Assert.AreEqual("回答", AnswerRenderers(Find<StackPanel>(panel, "MessagesPanel")).Single().MarkdownBuilder.ToString());
            }
            finally { target.Release(); refresh.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("same-account")]
    [DataRow("changed-account")]
    [DataRow("issuer")]
    [DataRow("destination")]
    [DataRow("configuration-aba")]
    public void Suggestions_TokenRotatedByOtherRequestAfterBody_UsesOnlyVerifiedSource(string change)
    {
        OnUi(async () =>
        {
            using var target = new SseStub(LifecycleAnswer, jsonContent: "下一步检查什么");
            using var forbidden = new SseStub(LifecycleAnswer, jsonContent: "不应收到旧历史");
            string payload = change == "changed-account"
                ? "{\"access_token\":\"new-token\",\"expires_in\":3600,\"id_token\":\"e30."
                    + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"account_id\":\"other-account\"}")).TrimEnd('=') + ".x\"}"
                : "{\"access_token\":\"new-token\",\"expires_in\":3600}";
            using var refresh = new HeldCredentialRefresh(payload);
            using var http = new HttpClient(refresh);
            ITimeSeriesApi timeSeries = DispatchProxy.Create<ITimeSeriesApi, DelayedHistoryApi>();
            var gate = (DelayedHistoryApi)timeSeries;
            gate.Inner = new InMemoryTimeSeries();
            using var context = new TestPluginContext { TimeSeries = timeSeries };
            AiProvider provider = StubProvider("subscription", target.BaseUrl, "m");
            provider.Auth = AuthMethod.Subscription;
            provider.OAuth = new OAuthConfig
            {
                Credential = OAuthCredential.AccessToken, TokenUrl = "https://auth.example/token", ClientId = "test",
                AccountIdClaim = "account_id"
            };
            var store = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
            await store.SaveTokensAsync(provider.Id, new OAuthTokens
            {
                AccessToken = "old-token", RefreshToken = "refresh", AccountId = "original-account",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });
            await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            try
            {
                await PumpAsync();
                panel.SendExternal("BODY_SOURCE_MARKER");
                Assert.IsTrue(await WaitForAsync(() => gate.Entered.Task.IsCompleted), "正文已消费完成,助手历史写入暂缓建议入口");
                Assert.HasCount(1, target.Requests);
                Assert.IsNull(SuggestionTask(panel), "建议尚未启动,不能把建议自己刷新 token 当成并发旋转回归");
                await store.SaveTokensAsync(provider.Id, new OAuthTokens
                {
                    AccessToken = "old-token", RefreshToken = "refresh", AccountId = "original-account",
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(20)
                });
                var otherStore = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
                AiSettings external = await otherStore.LoadAsync();
                Task<ProviderCredential> rotating = otherStore.ResolveCredentialAsync(external.FindModel(provider.Models[0].Id)!);
                Assert.IsTrue(await WaitForAsync(() => refresh.Started.Task.IsCompleted));
                refresh.Release();
                Assert.AreEqual("new-token", (await rotating).Value);
                Assert.IsNull(SuggestionTask(panel), "其它真实请求已完成刷新,旧正文还没到建议入口");
                AiSettings live = PanelSettings(panel);
                if (change == "issuer") live.Providers[0].OAuth!.TokenUrl = "https://other-issuer.example/token";
                if (change == "destination")
                {
                    await otherStore.SaveTokensAsync(provider.Id, new OAuthTokens
                    {
                        AccessToken = "new-token", AccountId = "original-account", BaseUrl = forbidden.BaseUrl,
                        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
                    });
                }
                if (change == "configuration-aba")
                {
                    external.Providers[0].BaseUrl = forbidden.BaseUrl;
                    await otherStore.SaveAsync(external);
                    await store.ReloadIntoAsync(live);
                    external = await otherStore.LoadAsync();
                    external.Providers[0].BaseUrl = target.BaseUrl;
                    await otherStore.SaveAsync(external);
                    await store.ReloadIntoAsync(live);
                }
                gate.Release.TrySetResult();
                Assert.IsTrue(await WaitForAsync(() => SuggestionTask(panel) is not null));
                await FinishSuggestionsAsync(SuggestionTask(panel)!);
                Assert.IsEmpty(forbidden.Requests, "不同端点不能收到原正文历史");
                if (change == "same-account")
                {
                    Assert.HasCount(2, target.Requests, "同账号最新 token 应直接请求建议,不能额外探活");
                    Assert.AreEqual("Bearer old-token", target.Authorizations[0]);
                    Assert.AreEqual("Bearer new-token", target.Authorizations[1]);
                    Assert.Contains("BODY_SOURCE_MARKER", target.Requests[1]);
                    Assert.IsTrue(Find<WrapPanel>(panel, "SuggestionBar").IsVisible);
                    Assert.Contains("Conversation total: in 1 / out 1", ToolTip.GetTip(Find<TextBlock>(panel, "UsageText")) as string ?? "");
                }
                else
                {
                    Assert.HasCount(1, target.Requests, "账号、issuer、目标或配置代次变化都应禁止附带旧历史");
                    Assert.IsFalse(Find<WrapPanel>(panel, "SuggestionBar").IsVisible);
                    Assert.IsEmpty(Find<TextBlock>(panel, "UsageText").Text ?? "");
                }
                Assert.AreEqual("回答", AnswerRenderers(Find<StackPanel>(panel, "MessagesPanel")).Single().MarkdownBuilder.ToString());
            }
            finally { gate.Release.TrySetResult(); refresh.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Suggestions_FailoverCandidate_BindsParametersAndUsageToOriginalConversation(bool switchSession)
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var candidate = new SseStub(LifecycleAnswer, jsonContent: "CANDIDATE_SUGGESTION", holdFirstNonStream: true);
            using var other = new SseStub(LifecycleAnswer, jsonContent: "OTHER_SESSION_SUGGESTION");
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
                Providers = [first, backup, next], ActiveModelId = first.Models[0].Id,
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
                    Find<ComboBox>(panel, "ProviderCombo").SelectedIndex = 2;
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
                Assert.Contains("Conversation total: in 1 / out 1", ToolTip.GetTip(Find<TextBlock>(panel, "UsageText")) as string ?? "",
                    "只结算当前会话自己完成的建议 usage");
                Assert.HasCount(3, candidate.Requests, "会话切换和用量刷新不能重发候选建议");
            }
            finally { candidate.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("https://api.enterprise.githubcopilot.com", true)]
    [DataRow("https://api.individual.githubcopilot.com", true)]
    [DataRow("https://api.enterprise.githubcopilot.com.evil.example", false)]
    [DataRow("https://evilgithubcopilot.com", false)]
    [DataRow("http://api.enterprise.githubcopilot.com", false)]
    [DataRow("https://user@api.enterprise.githubcopilot.com", false)]
    [DataRow("https://api.enterprise.githubcopilot.com:444", false)]
    public void Suggestions_CopilotEndpointRenewal_OnlyAcceptsTrustedAccountHosts(string nextEndpoint, bool allowed)
    {
        AiProvider provider = ProviderCatalog.Find("github-copilot")!.CreateProvider();
        var model = new ResolvedModel(provider, provider.Models[0]);
        var before = new ProviderCredential("old-token", true, BaseUrl: "https://api.individual.githubcopilot.com");
        var after = new ProviderCredential("new-token", true, BaseUrl: nextEndpoint);
        MethodInfo guard = typeof(ChatPanelView).GetMethod("SameSuggestionDestination", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.AreEqual(allowed, (bool)guard.Invoke(null, [model, before, after])!);
    }

    [TestMethod]
    public void NewChat_CancelledOldStreamUsage_DoesNotPolluteNewConversation()
    {
        OnUi(async () =>
        {
            using var target = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"正在回答"},"finish_reason":null}],"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}}

                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: [DONE]


                """, holdAfterFirstChunk: true);
            using var context = new TestPluginContext();
            AiProvider provider = StubProvider("stream", target.BaseUrl, "m");
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id, SuggestFollowUps = false
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("旧会话");
                Assert.IsTrue(await WaitForAsync(() => AnswerRenderers(Find<StackPanel>(panel, "MessagesPanel")).Count > 0),
                    "正文和 usage 同一个真实 SSE 事件已消费，流尚未结束");
                Click(panel, "NewChatButton");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                Assert.IsEmpty(Find<TextBlock>(panel, "UsageText").Text ?? "", "旧流取消结算不属于已重置的新会话");
                target.Release();
                panel.SendExternal("新会话");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                string tip = ToolTip.GetTip(Find<TextBlock>(panel, "UsageText")) as string ?? "";
                Assert.Contains("Conversation total: in 7 / out 3", tip, "新会话只结算自己的真实请求");
            }
            finally { target.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void StickyFailover_OriginalModelDeleted_CancellationKeepsValidBackup()
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var backup = new HeldStreamingReply();
            using var unused = new SseStub(LifecycleAnswer, jsonContent: "OK");
            using var context = new TestPluginContext();
            AiProvider c = StubProvider("c", unused.BaseUrl, "m");
            AiProvider a = StubProvider("a", broken.BaseUrl, "m");
            AiProvider b = StubProvider("b", backup.BaseUrl, "m");
            var store = new AiSettingsStore(context);
            await store.SaveAsync(new AiSettings
            {
                Providers = [c, a, b], ActiveModelId = a.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = b.Models[0].Id }], SuggestFollowUps = false
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
                Assert.IsTrue(await WaitForAsync(() => Find<ComboBox>(panel, "ProviderCombo").ItemCount == 2));
                Assert.AreEqual(b.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                Click(panel, "StopButton");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                Assert.AreEqual(b.Models[0].Id, (await store.LoadAsync()).ActiveModelId, "原 A 已删除，不可把不存在的 ID 写回或兜底选第一 C");
                Assert.AreEqual(1, Find<ComboBox>(panel, "ProviderCombo").SelectedIndex);
                Assert.IsEmpty(unused.Requests, "删除非当前模型不该让取消选择转移到 C");
            }
            finally { backup.Release(); host?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void FailoverBackup_McpWaitThenUntrustedRenewalDestination_ContinuesToNextStation()
    {
        OnUi(async () =>
        {
            using var first = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}

                """, chunkDelay: TimeSpan.FromMilliseconds(1), abortAfterFirstChunk: true, holdAfterFirstChunk: true);
            using var oldBackup = new SseStub(LifecycleAnswer, jsonContent: "OK");
            using var renewedBackup = new ErrorStub(HttpStatusCode.TooManyRequests);
            using var healthy = new SseStub(LifecycleAnswer, jsonContent: "OK");
            using var mcp = new SseStub("", hold: true);
            using var context = new TestPluginContext();
            AiProvider a = StubProvider("a", first.BaseUrl, "m");
            AiProvider b = StubProvider("b", oldBackup.BaseUrl, "m");
            AiProvider c = StubProvider("c", healthy.BaseUrl, "m");
            b.Auth = AuthMethod.Subscription;
            b.OAuth = new OAuthConfig { Credential = OAuthCredential.AccessToken };
            var store = new AiSettingsStore(context);
            await store.SaveTokensAsync(b.Id, new OAuthTokens { AccessToken = "old", BaseUrl = oldBackup.BaseUrl, AccountId = "same-account" });
            await store.SaveAsync(new AiSettings
            {
                Providers = [a, b, c], ActiveModelId = a.Models[0].Id, Mode = ChatMode.Agent,
                FailoverChain = [new FailoverEntry { ModelId = b.Models[0].Id }, new FailoverEntry { ModelId = c.Models[0].Id }],
                SuggestFollowUps = false
            });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            Window? toolsHost = null, mcpHost = null;
            try
            {
                await PumpAsync();
                panel.SendExternal("备用站装配失败继续换站");
                Assert.IsTrue(await WaitForAsync(() => first.FirstChunkFlushedAsync.IsCompleted), "A 已装配并发送首个role-only事件,不是已解析可见正文");
                Click(panel, "ToolsButton");
                await PumpAsync(5);
                var picker = (ToolPickerView)context.FakeUi.LastPanel.CreateContent();
                toolsHost = Host(picker);
                RaiseClick(FindIn<Button>(picker, "McpRailAddButton"));
                await PumpAsync(5);
                var servers = (McpServersView)context.FakeUi.LastPanel.CreateContent();
                mcpHost = Host(servers);
                await PumpAsync(5);
                FindIn<ComboBox>(servers, "McpTransportCombo").SelectedIndex = (int)McpTransportType.Http;
                FindIn<TextBox>(servers, "McpUrlBox").Text = mcp.BaseUrl + "/mcp";
                FindIn<CheckBox>(servers, "McpEnabledCheck").IsChecked = true;
                RaiseClick(FindIn<Button>(servers, "McpSaveButton"));
                first.Release();
                Assert.IsTrue(await WaitForAsync(() => mcp.RequestBodyAsync.IsCompleted), "B 探活通过后，第一次 MCP 握手在 B 装配内挂住");
                Assert.HasCount(1, oldBackup.Requests, "B 只发过探活，尚未发正式流");
                await store.SaveTokensAsync(b.Id, new OAuthTokens { AccessToken = "renewed", BaseUrl = renewedBackup.BaseUrl, AccountId = "same-account" });
                mcp.Release();
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible, 1200));
                Assert.AreEqual(0, renewedBackup.Requests, "同账号续期不能把旧回合探活或正文交给新主机");
                Assert.HasCount(2, healthy.Requests, "B 的未授权目的地不能中断链；C 先探活，再真实回复");
                Assert.HasCount(1, oldBackup.Requests, "不允许使用已经失效的 B 客户端发送旧正式流");
                Assert.AreEqual(c.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                Assert.IsNotEmpty(AnswerRenderers(Find<StackPanel>(panel, "MessagesPanel")));
            }
            finally { first.Release(); mcp.Release(); mcpHost?.Close(); toolsHost?.Close(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ShortLivedRotatingOAuthToken_FormalRequestReceivesAnswer(bool failover)
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var target = new SseStub(LifecycleAnswer, jsonContent: "OK");
            using var refresh = new RotatingCredentialRefresh();
            using var http = new HttpClient(refresh);
            using var context = new TestPluginContext();
            AiProvider first = StubProvider("first", broken.BaseUrl, "m");
            AiProvider provider = StubProvider("subscription", target.BaseUrl, "m");
            provider.Auth = AuthMethod.Subscription;
            provider.OAuth = new OAuthConfig
            {
                Credential = OAuthCredential.AccessToken, TokenUrl = "https://auth.example/token", ClientId = "test"
            };
            var store = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
            await store.SaveTokensAsync(provider.Id, new OAuthTokens
            {
                AccessToken = "expired", RefreshToken = "refresh", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            await store.SaveAsync(new AiSettings
            {
                Providers = failover ? [first, provider] : [provider],
                ActiveModelId = failover ? first.Models[0].Id : provider.Models[0].Id,
                FailoverChain = failover ? [new FailoverEntry { ModelId = provider.Models[0].Id }] : [],
                SuggestFollowUps = false
            });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            try
            {
                await PumpAsync();
                panel.SendExternal("合法的短期令牌应能发送");
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                Assert.IsTrue(refresh.Count > 0, "过期令牌确实通过 OAuth 刷新");
                int formal = target.Requests.ToList().FindLastIndex(body => body.Contains("\"stream\":true", StringComparison.Ordinal));
                Assert.IsTrue(formal >= 0, "复验不能无限旋转 60 秒令牌而拒绝正式流");
                Assert.AreEqual("Bearer " + (await store.GetTokensAsync(provider.Id))!.AccessToken,
                    target.Authorizations[formal], "正式流使用当前落库旋转令牌");
                Assert.IsNotEmpty(AnswerRenderers(Find<StackPanel>(panel, "MessagesPanel")));
                Assert.AreEqual(provider.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CancelledFailover_DelayedHistoryPreservesReopenedSelectionOrRollsBackSamePanel(bool reopen)
    {
        OnUi(async () =>
        {
            using var broken = new ErrorStub();
            using var backup = new SseStub("""
                data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"半截回答"},"finish_reason":null}]}

                data: [DONE]


                """, holdAfterFirstChunk: true, firstStreamContent: "OK");
            using var unused = new SseStub(LifecycleAnswer, jsonContent: "OK");
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
                Providers = [a, b, c], ActiveModelId = a.Models[0].Id,
                FailoverChain = [new FailoverEntry { ModelId = b.Models[0].Id }], SuggestFollowUps = false
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
                else Click(panel, "StopButton");
                Assert.IsTrue(await WaitForAsync(() => gate.Entered.Task.IsCompleted), "旧取消已到不可取消的 assistant 历史写入");
                if (reopen)
                {
                    (reopenedWindow, reopened) = await ShowAsync(context);
                    Find<ComboBox>(reopened, "ProviderCombo").SelectedIndex = 2;
                    await PumpAsync(5);
                    Assert.AreEqual(c.Models[0].Id, (await store.LoadAsync()).ActiveModelId);
                }
                gate.Release.TrySetResult();
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible));
                Assert.AreEqual(reopen ? c.Models[0].Id : a.Models[0].Id, (await store.LoadAsync()).ActiveModelId,
                    "关闭的面板不能回滚共享配置;同面板取消仍应回滚");
                if (reopened is not null) Assert.AreEqual(2, Find<ComboBox>(reopened, "ProviderCombo").SelectedIndex);
                var history = new ChatHistoryStore(context);
                await history.InitAsync();
                var sessions = await history.ListSessionsAsync();
                var entries = await history.LoadAsync(sessions.Single().Id);
                Assert.IsTrue(entries.Any(entry => entry.Role == "assistant" && entry.Text == "半截回答"),
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

    private static Task? SuggestionTask(ChatPanelView panel)
        => (Task?)typeof(ChatPanelView).GetField("_suggestionTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel);

    private static async Task FinishSuggestionsAsync(Task task)
    {
        Assert.IsTrue(await WaitForAsync(() => task.IsCompleted), "必须等整个建议任务收尾,不能只等 HTTP handler 返回");
        await task;
    }

    private sealed class RotatingCredentialRefresh : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = ++Count;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"access_token":"rotated-{{count}}","refresh_token":"refresh-{{count}}","expires_in":60}""",
                    Encoding.UTF8, "application/json")
            });
        }
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
            ITimeSeries proxy = DispatchProxy.Create<ITimeSeries, DelayedHistorySeries>();
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

    private sealed class HeldCredentialRefresh(string? response = null) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _released.TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await _released.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response ?? """{"access_token":"same-token","expires_in":3600} """, Encoding.UTF8, "application/json")
                };
            }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            finally { Finished.TrySetResult(); }
        }
    }
}
