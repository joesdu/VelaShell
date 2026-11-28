using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Interop;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Secrets;
using VelaShell.PluginSdk.Storage;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 「连接供应商」那一页的 headless 装载。
/// </summary>
/// <remarks>
/// 这一页的交互约定就两条,两条都在这儿钉着:
/// <list type="number">
/// <item><b>点行只展开,绝不自动干事</b> —— 尤其不能一点就把浏览器弹出去。</item>
/// <item><b>展开后只问程序确实不知道的那几样</b> —— 其余收进「高级设置」。</item>
/// <item><b>同一家可加多把 Key</b> —— 纯 Key 草稿不复制地址、模型或机密。</item>
/// </list>
/// 整页是代码搭的,没有 XAML 名字域,所以按控件的 <c>Name</c> 走逻辑树找。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class ProviderSetupViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [TestMethod]
    public void ProxyRecommendations_AreLinksInsideTheLocalizedSentence()
    {
        OnUi(() =>
        {
            foreach (string locale in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
            {
                var loc = new Loc(locale);
                string text = loc["SetupApiKeyOnlyNotice"];
                var notice = ProviderProxyLinks.CreateNotice(loc);
                var blocks = notice.GetLogicalDescendants().OfType<TextBlock>().ToArray();
                Assert.Contains(block => block.Text == loc["NoticeTitle"], blocks);
                var inlines = blocks.Single(block => block.Inlines?.Count > 0).Inlines!;
                var links = inlines.OfType<Avalonia.Controls.Documents.InlineUIContainer>()
                    .Select(item => (HyperlinkButton)item.Child!).ToArray();
                CollectionAssert.AreEqual(new[] { "https://usemagpie.ai/", "https://astra-gate.si/" },
                    links.Select(link => link.NavigateUri!.AbsoluteUri).ToArray());
                Assert.AreEqual(text, string.Concat(inlines.Select(item => item switch
                {
                    Avalonia.Controls.Documents.Run run => run.Text,
                    Avalonia.Controls.Documents.InlineUIContainer { Child: HyperlinkButton link } => link.Content as string,
                    _ => null
                })));
            }
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    [DataRow(11, 720)]
    [DataRow(18, 270)]
    public void ProxyRecommendations_AlignWithSurroundingText(int fontSize, int width)
    {
        OnUi(() =>
        {
            var hint = new TextBlock { FontSize = fontSize, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            ProviderProxyLinks.SetNotice(hint, new Loc("zh-Hans")["SetupApiKeyOnlyNotice"]);
            var window = new Window { Width = width, Height = 600, Content = hint };
            try
            {
                window.Show();
                window.UpdateLayout();
                var lines = hint.TextLayout.TextLines;
                var links = hint.GetVisualDescendants().OfType<HyperlinkButton>().ToArray();
                Assert.HasCount(2, links);
                foreach (var link in links)
                {
                    var label = link.GetVisualDescendants().OfType<TextBlock>().Single();
                    double actual = label.TranslatePoint(new Point(0, label.TextLayout.TextLines[0].Baseline), hint)!.Value.Y;
                    double top = 0;
                    var line = lines.First(l => (top += l.Height) > actual);
                    Assert.AreEqual(top - line.Height + line.Baseline, actual, 1.0, $"{link.Content} baseline");
                }
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("json")]
    [DataRow("cancel")]
    [DataRow("live-conflict")]
    public void CatalogueHeldInRealStorage_KeepsDraftsSourcesAndHealthUntilSuccessfulPublication(string outcome)
    {
        OnUi(async () =>
        {
            using var catalogue = new HeldModelsEndpoint(response: """{"data":[{"id":"old-model","context_length":65536},{"id":"added-model","context_length":16000}]}""");
            var storage = new KeySaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            AiProvider source = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            source.BaseUrl = catalogue.BaseUrl + "/v1";
            source.AvailableModels = ["old-choice"];
            source.ModelsExpanded = false;
            AiModelConfig model = source.Models[0];
            model.Model = "old-model";
            ModelsDevCatalog.Apply(model, new ModelSpec(model.Model, "", 128000, 1000, 1, 2, .5, false), newModel: true);
            var unchanged = new AiModelConfig { Model = "manual-model", MaxInputTokens = 9000, ContextWindowIsManual = true };
            source.Models.Add(unchanged);
            await context.Secrets.SetAsync("apikey:" + source.Id, "sk-original");
            var health = new ProviderHealth();
            health.Record(model.Id, false);
            health.Record(unchanged.Id, false);
            health.RecordKey(source.Id, false);
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [source], ActiveModelId = model.Id }, "custom-openai", health);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            int notifications = 0;
            view.ModelsChanged += () => notifications++;
            Task? pull = null;
            try
            {
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                TextBox nameBox = Find<TextBox>(view, "SetupNameBox");
                TextBox modelBox = Find<TextBox>(view, "SetupModelBox");
                nameBox.Text = "unsaved-name";
                modelBox.Text = "unsaved-model";
                string formBaseline = (string)typeof(ProviderSetupView).GetField("_providerFormBaseline", flags)!.GetValue(view)!;
                pull = (Task)typeof(ProviderSetupView).GetMethod("PullModelsAsync", flags)!
                    .Invoke(view, [ProviderCatalog.Find("custom-openai")!, source, false])!;
                await catalogue.Requested.WaitAsync(TimeSpan.FromSeconds(15));
                source.Name = "saved-during-network";
                model.MaxTokens = 2000;
                await store.SaveAsync(settings).WaitAsync(TimeSpan.FromSeconds(15));
                string diskBefore = System.Text.Json.JsonSerializer.Serialize(await context.Storage.GetAsync<System.Text.Json.JsonElement>("settings"));
                string providerBefore = System.Text.Json.JsonSerializer.Serialize(source);
                long epoch = AiSettingsStore.ProviderConfigurationVersion(source);
                storage.Hold = true;
                catalogue.Release();
                await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(providerBefore, System.Text.Json.JsonSerializer.Serialize(source), "等待 JSON 时不得发布新 GUID、来源或窗口");
                Assert.AreEqual(diskBefore, System.Text.Json.JsonSerializer.Serialize(await context.Storage.GetAsync<System.Text.Json.JsonElement>("settings")));
                Assert.AreEqual(formBaseline, typeof(ProviderSetupView).GetField("_providerFormBaseline", flags)!.GetValue(view));
                Assert.AreEqual(epoch, AiSettingsStore.ProviderConfigurationVersion(source));
                Assert.IsTrue(health.IsCooling(model.Id));
                Assert.AreEqual(0, notifications);
                await Assert.ThrowsAsync<AiSettingsStore.ApiKeySlotChangedException>(() => store.ResolveCredentialAsync(settings.FindModel(model.Id)!));
                string liveBeforeRelease = providerBefore;
                if (outcome == "live-conflict")
                {
                    model.MaxInputTokens = 32000;
                    model.ContextWindowIsManual = true;
                    liveBeforeRelease = System.Text.Json.JsonSerializer.Serialize(source);
                }
                storage.Failure = outcome is "json" or "cancel" ? outcome : null;
                storage.Release.TrySetResult();
                if (outcome == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => pull);
                else await pull.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreSame(source, settings.Providers.Single());
                Assert.AreSame(model, source.Models[0]);
                Assert.AreSame(nameBox, Find<TextBox>(view, "SetupNameBox"));
                Assert.AreSame(modelBox, Find<TextBox>(view, "SetupModelBox"));
                Assert.AreEqual("unsaved-name", nameBox.Text);
                Assert.AreEqual("unsaved-model", modelBox.Text);
                Assert.IsTrue(health.IsCooling(unchanged.Id), "目录不能清除未改模型的健康证据");
                Assert.IsTrue(health.IsKeyCooling(source.Id), "目录不修改 Key，也不能清除 Key 证据");
                Assert.AreEqual("sk-original", await store.GetApiKeyAsync(source.Id));
                if (outcome == "success")
                {
                    Assert.AreEqual("saved-during-network", source.Name);
                    Assert.AreEqual(2000, model.MaxTokens, "分离稿必须基于网络返回后的当前活对象合并");
                    Assert.AreEqual(65536, model.MaxInputTokens);
                    Assert.AreEqual(65536, model.LastFetchedSpec?.ContextTokens);
                    Assert.IsFalse(model.DefaultContextWindow);
                    Assert.IsFalse(model.ContextWindowIsManual);
                    Assert.Contains("added-model", source.AvailableModels);
                    Assert.HasCount(3, source.Models);
                    Assert.IsNull(source.ModelsExpanded);
                    Assert.AreEqual(epoch + 1, AiSettingsStore.ProviderConfigurationVersion(source));
                    Assert.IsFalse(health.IsCooling(model.Id));
                    Assert.AreEqual(1, notifications);
                    AiProvider accepted = System.Text.Json.JsonSerializer.Deserialize<AiProvider>((string)typeof(ProviderSetupView)
                        .GetField("_providerFormBaseline", flags)!.GetValue(view)!)!;
                    Assert.AreEqual(65536, accepted.Models[0].MaxInputTokens);
                    Assert.AreEqual(1000, accepted.Models[0].MaxTokens, "用户网络等待编辑不应吸收为目录表单基线");
                    Assert.AreNotEqual(source.Name, accepted.Name);
                    AiProvider saved = (await store.LoadAsync()).Providers.Single();
                    Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(source), System.Text.Json.JsonSerializer.Serialize(saved));
                }
                else
                {
                    Assert.AreEqual(liveBeforeRelease, System.Text.Json.JsonSerializer.Serialize(source));
                    Assert.AreEqual(diskBefore, System.Text.Json.JsonSerializer.Serialize(await context.Storage.GetAsync<System.Text.Json.JsonElement>("settings")));
                    Assert.AreEqual(formBaseline, typeof(ProviderSetupView).GetField("_providerFormBaseline", flags)!.GetValue(view));
                    Assert.AreEqual(128000, model.LastFetchedSpec?.ContextTokens);
                    Assert.IsFalse(model.DefaultContextWindow);
                    Assert.AreEqual(outcome == "live-conflict", model.ContextWindowIsManual);
                    Assert.HasCount(2, source.Models, "失败目录不能遗留新增型号与 ghost 来源标记");
                    Assert.AreEqual(epoch, AiSettingsStore.ProviderConfigurationVersion(source));
                    Assert.IsTrue(health.IsCooling(model.Id));
                    Assert.AreEqual(0, notifications);
                }
            }
            finally
            {
                catalogue.Release();
                storage.Release.TrySetResult();
                try { if (pull is not null) await pull.WaitAsync(TimeSpan.FromSeconds(40)); }
                catch (OperationCanceledException) when (outcome == "cancel") { }
                finally { window.Close(); }
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SavedCatalogueRequestChangesRejectOldHistoryAfterMcpButMetadataDoesNot(bool requestChanged)
    {
        OnUi(async () =>
        {
            await using var mcp = new SseStub("", hold: true);
            await using var endpoint = new SseStub(KeySaveProbeSse);
            using var context = new TestPluginContext();
            EnsureFreshModelCache(context);
            var store = new AiSettingsStore(context);
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl + "/v1", Models = [new AiModelConfig { Model = "m", MaxInputTokens = 128000 }] };
            await store.SetApiKeyAsync(provider.Id, "sk-original");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveModelId = provider.Models[0].Id,
                Mode = ChatMode.Agent,
                SuggestFollowUps = false,
                McpServers = [new McpServerConfig { Name = "held", Transport = McpTransportType.Http, Url = mcp.BaseUrl + "/mcp" }]
            });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            try
            {
                await PumpAsync(40);
                panel.SendExternal("private-history-before-catalogue");
                await mcp.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                var live = (AiSettings)typeof(ChatPanelView).GetField("_settings",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
                AiProvider owner = live.Providers.Single();
                AiModelConfig model = owner.Models.Single();
                long epoch = AiSettingsStore.ProviderConfigurationVersion(owner);
                string expected = System.Text.Json.JsonSerializer.Serialize(owner);
                AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
                draft.Name = "catalogue display name";
                draft.AvailableModels = ["m", "candidate"];
                draft.Models[0].InputPricePerMillion = 3;
                if (requestChanged) draft.Models[0].MaxInputTokens = 65536;
                await store.SaveProviderCatalogueAsync(live, owner, draft, expected);
                Assert.AreSame(model, owner.Models.Single());
                Assert.AreEqual(epoch + (requestChanged ? 1 : 0), AiSettingsStore.ProviderConfigurationVersion(owner));
                mcp.Release();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                PropertyInfo busy = typeof(ChatPanelView).GetProperty("Busy",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                while ((bool)busy.GetValue(panel)!)
                {
                    await Task.Delay(5, timeout.Token);
                    Dispatcher.UIThread.RunJobs();
                }
                Assert.HasCount(requestChanged ? 0 : 1, endpoint.Requests,
                    "相同型号和地址的目录限额变化仍使旧历史失效；展示元数据不应误伤旧请求");
                if (!requestChanged) Assert.Contains("private-history-before-catalogue", endpoint.Requests.Single());
            }
            finally { mcp.Release(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("https://bücher.example/v1", "https://xn--bcher-kva.example/v2", true)]
    [DataRow("https://xn--bcher-kva.example/v1", "https://bücher.example/v2", true)]
    [DataRow("https://BÜCHER.example/v1", "https://xn--bcher-kva.example:443/v2", true)]
    [DataRow("http://bücher.example", "http://xn--bcher-kva.example:80/v1", true)]
    [DataRow("https://bücher.example", "https://xn--bcher-kva.example:444", false)]
    [DataRow("https://bücher.example", "http://xn--bcher-kva.example:443", false)]
    [DataRow("https://bücher.example", "https://bucher.example", false)]
    [DataRow("https://bücher.example", "https://xn--bcher-kva.example.evil", false)]
    [DataRow("https://bücher.example", "https://xn--bcher-kva.example@evil.example", false)]
    [DataRow(null, "https://example.test", false)]
    [DataRow("https://example.test", null, false)]
    [DataRow("", "", false)]
    [DataRow("/relative", "/relative", false)]
    [DataRow("https://", "https://", false)]
    [DataRow("mailto:ops", "mailto:ops", false)]
    [DataRow("urn:example:test", "urn:example:test", false)]
    public void SameOrigin_NormalizesIdnaWithoutRelaxingDestinationBoundaries(string? left, string? right, bool expected) => Assert.AreEqual(expected, AiSettingsStore.SameOrigin(left, right));

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ProviderSetupViewUiTests).Assembly);

    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static Task PumpAsync(int rounds = 20) => HeadlessPump.RunAsync(rounds);

    private static T Find<T>(Control root, string name) where T : Control
        => root.GetLogicalDescendants().OfType<T>().First(c => c.Name == name);

    private static T? FindOrNull<T>(Control root, string name) where T : Control
        => root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    /// <summary>展开区里此刻<b>真正露在外面</b>的输入框(收在「高级设置」里的不算)。</summary>
    private static List<string> VisibleBoxes(Control root)
        => [.. root.GetLogicalDescendants().OfType<TextBox>()
                   .Where(box => box.Name is not (null or "SetupSearch") && IsShown(box))
                   .Select(box => box.Name!)];

    private static bool IsShown(Control control)
    {
        for (StyledElement? node = control; node is not null; node = node.Parent)
        {
            if (node is Control visual && !visual.IsVisible)
            {
                return false;
            }
        }
        return true;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void EnsureFreshModelCache(TestPluginContext context)
    {
        string cache = Path.Combine(context.DataDirectory, "models-dev.json");
        if (!File.Exists(cache)) File.WriteAllText(cache, "{}");
        File.SetLastWriteTimeUtc(cache, DateTime.UtcNow);
    }

    private static async Task WaitForEnabledAsync(Button button)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (!button.IsEnabled)
        {
            await Task.Delay(5, timeout.Token);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task ClickPrimaryAsync(ProviderSetupView view)
    {
        Button primary = Find<Button>(view, "SetupPrimaryButton");
        Click(primary);
        await WaitForEnabledAsync(primary);
    }

    /// <summary>点主按钮,等确认框弹出来,返回框里的说明(按钮此时还锁着)。</summary>
    private static async Task<string?> ClickPrimaryUntilConfirmAsync(ProviderSetupView view)
    {
        Click(Find<Button>(view, "SetupPrimaryButton"));
        return await WaitForConfirmAsync(view);
    }

    private static ConfirmOverlay ConfirmBox(Control view) => view.GetLogicalDescendants().OfType<ConfirmOverlay>().Single();

    private static async Task<string?> WaitForConfirmAsync(Control view)
    {
        ConfirmOverlay box = ConfirmBox(view);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (!box.IsOpen)
        {
            await Task.Delay(5, timeout.Token);
            Dispatcher.UIThread.RunJobs();
        }
        return box.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "ConfirmMessage").Text;
    }

    /// <summary>点确认框里的「确认」或「取消」,等主按钮放开(这一轮提交收尾)。</summary>
    private static async Task AnswerConfirmAsync(ProviderSetupView view, bool accept)
    {
        Click(ConfirmBox(view).GetLogicalDescendants().OfType<Button>()
            .Single(button => button.Name == (accept ? "ConfirmAcceptButton" : "ConfirmCancelButton")));
        await PumpAsync(2);
        if (!ConfirmBox(view).IsOpen) await WaitForEnabledAsync(Find<Button>(view, "SetupPrimaryButton"));
    }

    /// <summary>照用户的操作展开一行:真点在行上(命中测试、冒泡都真跑)。</summary>
    private static async Task ClickRowAsync(Window window, ProviderSetupView view, string entryId)
    {
        Border card = Find<Border>(view, $"SetupRow.{entryId}");
        if (!IsShown(card))
        {
            Click(Find<Button>(view, "SetupBack"));
            await PumpAsync();
            if (ConfirmBox(view).IsOpen)
            {
                Click(Find<Button>(ConfirmBox(view), "ConfirmAcceptButton"));
                await PumpAsync();
            }
        }
        // 从详情返回时先等目录重新排版，再把长列表中的目标行滚到视口。
        await PumpAsync(2);
        card.BringIntoView();
        await PumpAsync(10);
        // 点行的左上角一带:那儿是字母牌/标题,肯定落在行内
        Point spot = card.TranslatePoint(new Point(20, 18), window)!.Value;
        Assert.IsTrue(spot.Y > 0 && spot.Y < window.Height,
            $"{entryId} 那一行没滚进可见区(y={spot.Y}),这一点会落空");
        window.MouseDown(spot, MouseButton.Left);
        window.MouseUp(spot, MouseButton.Left);
        await PumpAsync(20);
        if (ConfirmBox(view).IsOpen)
        {
            Click(Find<Button>(ConfirmBox(view), "ConfirmAcceptButton"));
            await PumpAsync();
        }
    }

    private static async Task<(Window Window, ProviderSetupView View, AiSettings Settings, AiSettingsStore Store)>
        ShowAsync(TestPluginContext context, AiSettings? seed = null, string? focus = null, ProviderHealth? health = null)
    {
        // 自动拉取只读新鲜规格缓存;已有自定义规格必须原样保留。
        EnsureFreshModelCache(context);
        AiSettings settings = seed ?? new AiSettings();
        var store = new AiSettingsStore(context);
        await store.SaveAsync(settings);
        var view = new ProviderSetupView(context, store, settings, new Loc("en"), focus, health: health);
        var window = new Window { Width = 720, Height = 720, Content = view };
        window.Show();
        await PumpAsync(40);
        return (window, view, settings, store);
    }

    private static HttpListener StartListener(out string baseUrl, string host = "127.0.0.1")
    {
        // TCP 分配的空闲端口可能受 Windows HTTP.sys 保留或被别的进程抢占。
        for (int attempt = 0; ; attempt++)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            string url = $"http://{host}:{port}";
            listener.Prefixes.Add(url + "/");
            try
            {
                listener.Start();
                baseUrl = url;
                return listener;
            }
            catch (HttpListenerException ex) when (attempt < 9 && ex.ErrorCode is 5 or 183)
            {
                listener.Close();
            }
            catch
            {
                listener.Close();
                throw;
            }
        }
    }

    private sealed class HeldModelsEndpoint : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _response;
        public string BaseUrl { get; }
        public Task Requested => _requested.Task;
        public string? Authorization { get; private set; }

        public HeldModelsEndpoint(string host = "127.0.0.1", string? response = null)
        {
            _response = response ?? """{"data":[{"id":"old-model"}]}""";
            _listener = StartListener(out string baseUrl, host);
            BaseUrl = baseUrl;
            _ = ServeAsync();
        }

        private async Task ServeAsync()
        {
            CancellationToken shutdown = _shutdown.Token;
            try
            {
                // 保存供应商也会刷新清单,放行后仍需应答同一用例中的后续请求。
                while (await _listener.AcceptAsync(shutdown) is { } request)
                {
                    Authorization = request.Request.Headers["Authorization"];
                    _requested.TrySetResult();
                    await _released.Task;
                    byte[] payload = Encoding.UTF8.GetBytes(_response);
                    request.Response.ContentType = "application/json";
                    request.Response.ContentLength64 = payload.Length;
                    await request.Response.OutputStream.WriteAsync(payload, shutdown);
                    request.Response.Close();
                }
            }
            catch (Exception) when (shutdown.IsCancellationRequested)
            {
                // 测试收尾会关闭可能仍在等待的端点。
            }
        }

        public void Release() => _released.TrySetResult();
        public void Dispose()
        {
            _shutdown.Cancel();
            _released.TrySetCanceled();
            _listener.Close();
            _shutdown.Dispose();
        }
    }

    private const string KeySaveProbeSse = "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

    private sealed class KeySaveStorage(IPluginStorage inner) : IPluginStorage
    {
        public string? Failure { get; set; }
        public bool Hold { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
            => inner.GetAsync<T>(key, cancellationToken);
        public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            if (Hold)
            {
                Hold = false;
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            if (Failure == "json") throw new IOException("Storage failed");
            if (Failure == "cancel") throw new OperationCanceledException();
            await inner.SetAsync(key, value, cancellationToken);
        }
        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
            => inner.RemoveAsync(key, cancellationToken);
        public Task<IReadOnlyList<string>> GetKeysAsync(CancellationToken cancellationToken = default)
            => inner.GetKeysAsync(cancellationToken);
    }

    private sealed class KeySaveSecrets(ISecretsApi inner) : ISecretsApi
    {
        public bool Fail { get; set; }
        public bool Hold { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
            => inner.GetAsync(name, cancellationToken);
        public async Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
        {
            if (value == "sk-new")
            {
                Started.TrySetResult();
                if (Hold) await Released.Task.WaitAsync(cancellationToken);
                if (Fail) throw new IOException("Secret write failed");
            }
            await inner.SetAsync(name, value, cancellationToken);
        }
        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(name, cancellationToken);
    }

    private static async Task ClickVisibleAsync(Window window, Button button)
    {
        button.BringIntoView();
        await PumpAsync(5);
        Point spot = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        Assert.IsTrue(spot.Y > 0 && spot.Y < window.Height, "按钮必须在窗口内,才能验证真实点击");
        window.MouseDown(spot, MouseButton.Left);
        window.MouseUp(spot, MouseButton.Left);
    }

    // ---- 交互约定 ----

    [TestMethod]
    public void FirstConnection_UsesOneWindowAndLandsOnTheCreatedProviderBeforeModelDiscoveryFinishes()
    {
        OnUi(async () =>
        {
            using var endpoint = new HeldModelsEndpoint();
            using var context = new TestPluginContext();
            EnsureFreshModelCache(context);
            var store = new AiSettingsStore(context);
            var settings = new AiSettings { Providers = [] };
            await store.SaveAsync(settings);
            var discovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workspace = new SettingsView(context, store, settings, new Loc("en"), () => { });
            workspace.ModelsChanged += () => discovered.TrySetResult();
            workspace.EnableProviderSetup();
            Panel pages = workspace.GetControl<Panel>("WorkspacePages");
            Grid editor = workspace.GetControl<Grid>("SettingsLayout");
            var window = new Window { Width = 900, Height = 700, Content = workspace };
            window.Show();
            try
            {
                await PumpAsync();
                ProviderSetupView setup = pages.Children.OfType<ProviderSetupView>().Single();
                Assert.IsFalse(editor.IsVisible);
                await ClickRowAsync(window, setup, "custom-openai");
                Assert.IsFalse(IsShown(Find<Border>(setup, "SetupRow.custom-anthropic")));
                Find<TextBox>(setup, "SetupBaseUrlBox").Text = endpoint.BaseUrl + "/v1";
                Find<TextBox>(setup, "SetupKeyBox").Text = "test-only";
                Click(Find<Button>(setup, "SetupPrimaryButton"));
                await endpoint.Requested.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsTrue(editor.IsVisible);
                Assert.IsFalse(pages.Children.Contains(setup));
                ListBox list = workspace.GetControl<ListBox>("ProvidersList");
                Assert.AreSame(settings.Providers.Single(), ((ProviderNavItem)list.SelectedItem!).Provider);
                Assert.IsNull(((ProviderNavItem)list.SelectedItem!).Model);
                endpoint.Release();
                await discovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Contains("old-model", settings.Providers.Single().AvailableModels);
            }
            finally { endpoint.Release(); window.Close(); }
        });
    }

    [TestMethod]
    public void CatalogSearch_FiltersPresetsAndBackProtectsTheDraft()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context);
            try
            {
                TextBox search = Find<TextBox>(view, "SetupSearch");
                Border anthropic = Find<Border>(view, "SetupRow.custom-anthropic");
                Border other = Find<Border>(view, "SetupRow.custom-openai");
                search.Text = "  ANTHROPIC  ";
                await PumpAsync();
                Assert.IsTrue(anthropic.IsVisible);
                Assert.IsFalse(other.IsVisible);
                await ClickRowAsync(window, view, "custom-anthropic");
                Assert.IsFalse(IsShown(search));
                TextBox key = Find<TextBox>(view, "SetupKeyBox");
                key.Text = "test-draft";
                Click(Find<Button>(view, "SetupBack"));
                await WaitForConfirmAsync(view);
                await AnswerConfirmAsync(view, false);
                Assert.AreSame(key, Find<TextBox>(view, "SetupKeyBox"));
                Assert.AreEqual("test-draft", key.Text);
                Click(Find<Button>(view, "SetupBack"));
                await WaitForConfirmAsync(view);
                Click(Find<Button>(ConfirmBox(view), "ConfirmAcceptButton"));
                await PumpAsync();
                Assert.IsTrue(IsShown(search));
                Assert.IsFalse(other.IsVisible);
                Assert.IsNull(FindOrNull<TextBox>(view, "SetupKeyBox"));
                search.Text = "no-such-preset";
                await PumpAsync();
                Assert.IsTrue(Find<TextBlock>(view, "SetupNoMatch").IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void RowsCarryNoActionButtonAndStartCollapsed()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context);
            try
            {
                CollectionAssert.AreEquivalent(new[] { "SetupRow.custom-openai", "SetupRow.custom-anthropic" },
                    view.GetLogicalDescendants().OfType<Border>().Select(border => border.Name)
                        .Where(name => name?.StartsWith("SetupRow.", StringComparison.Ordinal) == true).ToArray());
                foreach (ProviderCatalogEntry entry in ProviderCatalog.All)
                {
                    Assert.IsNotNull(FindOrNull<Border>(view, $"SetupRow.{entry.Id}"), entry.Id);
                    // 行上不摆按钮:登录/添加一律在展开区里点
                    Assert.IsNull(FindOrNull<Button>(view, $"SetupAction.{entry.Id}"), entry.Id);
                }
                Assert.IsEmpty(VisibleBoxes(view), "一上来全是收起的");
                Assert.IsNull(FindOrNull<Button>(view, "SetupPrimaryButton"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void ClickingTheSameRowTwice_CollapsesIt()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context);
            try
            {
                await ClickRowAsync(window, view, "custom-anthropic");
                Assert.IsNotNull(FindOrNull<Button>(view, "SetupPrimaryButton"));

                await ClickRowAsync(window, view, "custom-anthropic");

                Assert.IsNull(FindOrNull<Button>(view, "SetupPrimaryButton"), "再点一次该收起来");
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---- 只问缺的那几样 ----

    [TestMethod]
    public void AnApiKeyProvider_AsksForTheKeyAndNothingElse()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var endpoint = new SseStub("");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, focus: "custom-anthropic");
            try
            {
                CollectionAssert.AreEquivalent(new[] { "SetupBaseUrlBox", "SetupKeyBox" }, VisibleBoxes(view));
                Assert.IsTrue(string.IsNullOrEmpty(Find<TextBox>(view, "SetupBaseUrlBox").Text));
                Find<TextBox>(view, "SetupBaseUrlBox").Text = endpoint.BaseUrl + "/v1";

                Find<TextBox>(view, "SetupKeyBox").Text = "sk-deepseek";
                await ClickPrimaryAsync(view);

                Assert.HasCount(1, settings.Providers);
                AiProvider added = settings.Providers[0];
                Assert.AreEqual("custom-anthropic", added.CatalogId);
                Assert.AreEqual(endpoint.BaseUrl + "/v1", added.BaseUrl, "实际保存并拉取的是环回接入");
                Assert.AreEqual(added.Models[0].Id, settings.ActiveModelId);
                Assert.AreEqual("sk-deepseek", await store.GetApiKeyAsync(added.Id));
                Assert.AreEqual("Ready", Find<TextBlock>(view, "SetupPill.custom-anthropic").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void Advanced_StaysFoldedUntilAsked()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context, focus: "custom-anthropic");
            try
            {
                CollectionAssert.AreEquivalent(new[] { "SetupBaseUrlBox", "SetupKeyBox" }, VisibleBoxes(view));

                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                await PumpAsync();

                List<string> boxes = VisibleBoxes(view);
                Assert.Contains("SetupNameBox", boxes);
                Assert.Contains("SetupModelBox", boxes);
                Assert.Contains("SetupBaseUrlBox", boxes);
                Assert.AreEqual("", Find<TextBox>(view, "SetupBaseUrlBox").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void AProviderWithNoKnownEndpoint_AsksForItAndNeverPrefillsThePlaceholder()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context, focus: "custom-anthropic");
            try
            {
                List<string> boxes = VisibleBoxes(view);
                Assert.DoesNotContain("SetupClientIdBox", boxes);
                Assert.Contains("SetupBaseUrlBox", boxes);
                // 占位符不能当默认值端上来 —— 用户十有八九连尖括号一起提交
                Assert.AreEqual("", Find<TextBox>(view, "SetupBaseUrlBox").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---- 拉回来的模型 ----

    [TestMethod]
    public void PulledModels_ShowUpAsAPickerInAdvanced()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.Models[0].Model = "llama-3.3-70b-versatile";
            provider.AvailableModels = ["llama-3.3-70b-versatile", "mixtral-8x7b"];
            var seed = new AiSettings { Providers = [provider] };
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context, seed, focus: "custom-openai");
            try
            {
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                await PumpAsync();

                ComboBox picker = Find<ComboBox>(view, "SetupModelPicker");
                Assert.AreSequenceEqual(provider.AvailableModels, (System.Collections.ICollection)picker.ItemsSource!);
                // 出厂示例正好在列表里,应当已经选中
                Assert.AreEqual("llama-3.3-70b-versatile", picker.SelectedItem);

                picker.SelectedItem = "mixtral-8x7b";
                await PumpAsync();
                Assert.AreEqual("mixtral-8x7b", Find<TextBox>(view, "SetupModelBox").Text,
                    "从下拉里挑一个就该填进模型框");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void WithoutAPulledList_ThereIsNoEmptyPicker()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context, focus: "custom-openai");
            try
            {
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                await PumpAsync();

                Assert.IsNull(FindOrNull<ComboBox>(view, "SetupModelPicker"), "没拉到过就别摆一个空下拉");
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---- 已连接 / 移除 ----

    [TestMethod]
    public void RemovingAnApiKeyProvider_TakesItsSecretsWithIt()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            var seed = new AiSettings
            {
                Providers = [provider],
                ActiveModelId = provider.Models[0].Id,
                // 链里钉着这条将被移除的模型:移除后必须连这条死条目一起剪掉(review⑥#9)
                FailoverChain = [new FailoverEntry { ModelId = provider.Models[0].Id }]
            };
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, seed);
            try
            {
                await store.SetApiKeyAsync(provider.Id, "gsk-1");
                await view.RefreshStatusAsync();
                await PumpAsync();
                Assert.AreEqual("Ready", Find<TextBlock>(view, "SetupPill.custom-openai").Text);

                view.FocusEntry("custom-openai");
                await PumpAsync();
                Button remove = Find<Button>(view, "SetupSecondaryButton");
                Assert.AreEqual("Remove", (string?)remove.Content);
                Click(remove);
                await PumpAsync(40);

                Assert.IsEmpty(settings.Providers);
                Assert.IsNull(await store.GetApiKeyAsync(provider.Id), "移除要连机密一起清,别留孤儿凭据");
                Assert.IsEmpty(settings.FailoverChain,
                    "移除连带剪掉故障转移链里的死条目 —— 否则设置窗口一开就是一行灰掉点不动的幽灵(review⑥#9)");
                Assert.IsNull(settings.ActiveModelId);
                Assert.AreEqual("Not added", Find<TextBlock>(view, "SetupPill.custom-openai").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---- 协议可选 / 同一家加多份 ----

    /// <summary>openai 那一条出厂钉的是 Responses,但用户得能在添加时改成 Chat Completions。</summary>
    [TestMethod]
    public void AnApiKeyEntry_ExposesTheProtocolPickerInAdvanced()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var endpoint = new SseStub("");
            (Window window, ProviderSetupView view, AiSettings settings, _) =
                await ShowAsync(context, focus: "custom-openai");
            try
            {
                ComboBox protocol = Find<ComboBox>(view, "SetupProtocolCombo");
                Assert.IsFalse(IsShown(protocol), "「高级设置」没展开,它就不该露在外面");
                Assert.AreEqual((int)ChatProtocol.OpenAiChatCompletions, protocol.SelectedIndex);

                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                await PumpAsync();
                Assert.IsTrue(IsShown(Find<ComboBox>(view, "SetupProtocolCombo")));

                protocol.SelectedIndex = (int)ChatProtocol.OpenAiResponses;
                Find<TextBox>(view, "SetupBaseUrlBox").Text = endpoint.BaseUrl + "/v1";
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-test";
                await ClickPrimaryAsync(view);

                Assert.HasCount(1, settings.Providers);
                Assert.AreEqual(ChatProtocol.OpenAiResponses, settings.Providers[0].DefaultProtocol,
                    "协议要落到新加的这一份上");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NewProvider_WithExistingType_SavesIndependentCloneAndKeepsSource(bool reuseWindow)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var endpoint = new SseStub("", jsonContent: "[]");
            AiProvider first = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            AiProvider source = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            source.Name = "relay";
            source.BaseUrl = endpoint.BaseUrl + "/v1";
            source.Models[0].Model = "configured-model";
            source.Models[0].Temperature = 0.35f;
            source.AdditionalApiKeyIds = ["source-extra"];
            await context.Secrets.SetAsync($"apikey:{source.Id}", "sk-source");
            await context.Secrets.SetAsync("apikey:source-extra", "sk-extra");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [first, source], ActiveModelId = source.Models[0].Id });
            string sourceJson = System.Text.Json.JsonSerializer.Serialize(source);
            try
            {
                if (reuseWindow)
                {
                    view.FocusEntry(source.CatalogId, source.Id);
                    Find<TextBox>(view, "SetupNameBox").Text = "discarded-edit";
                    view.StartNewProvider();
                }
                await ClickRowAsync(window, view, "custom-openai");
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                Assert.AreEqual("relay 2", Find<TextBox>(view, "SetupNameBox").Text);
                Assert.AreEqual(source.BaseUrl, Find<TextBox>(view, "SetupBaseUrlBox").Text);
                Assert.AreEqual("configured-model", Find<TextBox>(view, "SetupModelBox").Text);
                Assert.IsTrue(string.IsNullOrEmpty(Find<TextBox>(view, "SetupKeyBox").Text));
                Assert.IsFalse(Find<Button>(view, "SetupPullButton").IsVisible);
                Assert.IsFalse(Find<Button>(view, "SetupAddAnotherButton").IsVisible);
                Find<TextBox>(view, "SetupNameBox").Text = "new-relay";
                Find<TextBox>(view, "SetupModelBox").Text = "new-model";
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-new";
                await ClickPrimaryAsync(view);

                Assert.HasCount(3, settings.Providers);
                AiProvider created = settings.Providers.Single(p => p.Name == "new-relay");
                Assert.AreNotEqual(source.Id, created.Id);
                Assert.AreNotEqual(source.Models[0].Id, created.Models[0].Id);
                Assert.AreEqual("new-model", created.Models[0].Model);
                Assert.AreEqual(source.Models[0].Temperature, created.Models[0].Temperature);
                Assert.IsEmpty(created.AdditionalApiKeyIds);
                Assert.AreEqual("sk-new", await store.GetApiKeyAsync(created.Id));
                Assert.AreEqual("sk-source", await store.GetApiKeyAsync(source.Id));
                Assert.AreEqual("sk-extra", await store.GetApiKeyAsync("source-extra"));
                Assert.AreEqual(sourceJson, System.Text.Json.JsonSerializer.Serialize(source));
                Assert.AreEqual(source.Models[0].Id, settings.ActiveModelId);
                Assert.AreEqual("Bearer sk-new", endpoint.Authorizations.Single());

                Find<TextBox>(view, "SetupNameBox").Text = "saved-again";
                await ClickPrimaryAsync(view);
                AiSettings persisted = await store.LoadAsync();
                Assert.HasCount(3, persisted.Providers, "创建完成后再次保存只能编辑新实例");
                Assert.AreEqual("saved-again", persisted.Providers.Single(p => p.Id == created.Id).Name);
                Assert.AreEqual(sourceJson, System.Text.Json.JsonSerializer.Serialize(persisted.Providers.Single(p => p.Id == source.Id)));
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-cancelled";
                Click(Find<Button>(view, "SetupSecondaryButton"));
                Assert.AreEqual("saved-again", Find<TextBox>(view, "SetupNameBox").Text);
                Assert.IsEmpty(created.AdditionalApiKeyIds);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NewProvider_ModelListsStayWithTheirInstance(bool sameAddress)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new HeldModelsEndpoint(response: """{"data":[{"id":"new-only"},{"id":"shared"}]}""");
            endpoint.Release();
            AiProvider source = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            source.BaseUrl = endpoint.BaseUrl + (sameAddress ? "/v1" : "/old/v1");
            var catalog = new ModelsDevCatalog(context);
            ModelsDevCatalog.Materialise(source, catalog.Describe(null, [("old-first", 0), ("old-only", 0), ("shared", 0)]));
            source.AvailableModels = ["old-first", "old-only", "shared"];
            await context.Secrets.SetAsync($"apikey:{source.Id}", "sk-source");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [source], ActiveModelId = source.Models[0].Id });
            string sourceJson = System.Text.Json.JsonSerializer.Serialize(source);
            try
            {
                await ClickRowAsync(window, view, "custom-openai");
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                Assert.IsNull(FindOrNull<ComboBox>(view, "SetupModelPicker"), "同地址但不同 Key 也不能借用旧实例候选");
                Find<TextBox>(view, "SetupBaseUrlBox").Text = endpoint.BaseUrl + "/v1";
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-new";
                await ClickPrimaryAsync(view);

                AiProvider created = settings.Providers.Single(provider => provider.Id != source.Id);
                Assert.AreSequenceEqual(["new-only", "shared"], created.AvailableModels);
                Assert.AreSequenceEqual(["new-only", "shared"], [.. created.Models.Select(model => model.Model)]);
                Assert.AreSequenceEqual(created.AvailableModels, [.. Find<ComboBox>(view, "SetupModelPicker").Items.Cast<string>()]);
                Assert.AreEqual("Bearer sk-new", endpoint.Authorization);
                Assert.AreEqual(sourceJson, System.Text.Json.JsonSerializer.Serialize(source));
                AiSettings persisted = await store.LoadAsync();
                Assert.AreSequenceEqual(["new-only", "shared"], [.. persisted.Providers.Single(provider => provider.Id == created.Id).Models.Select(model => model.Model)]);
                Assert.AreEqual(sourceJson, System.Text.Json.JsonSerializer.Serialize(persisted.Providers.Single(provider => provider.Id == source.Id)));
                view.FocusEntry(source.CatalogId, source.Id);
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                Assert.AreSequenceEqual(source.AvailableModels, [.. Find<ComboBox>(view, "SetupModelPicker").Items.Cast<string>()]);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PullModels_ReplacesForeignAutomaticModelsButKeepsManualAndConcurrentEdits(bool editDuringPull)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new HeldModelsEndpoint(response: """{"data":[{"id":"local-model"}]}""");
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = endpoint.BaseUrl + "/v1";
            var catalog = new ModelsDevCatalog(context);
            ModelsDevCatalog.Materialise(provider, catalog.Describe(null, [("local-model", 0), ("foreign-model", 0), ("tuned-model", 0)]));
            AiModelConfig foreign = provider.Models.Single(model => model.Model == "foreign-model");
            provider.Models.Single(model => model.Model == "tuned-model").SystemPrompt = "keep tuned model";
            provider.Models.Add(new AiModelConfig { Model = "manual-model" });
            AiProvider other = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            ModelsDevCatalog.Materialise(other, catalog.Describe(null, [("other-model", 0)]));
            string otherJson = System.Text.Json.JsonSerializer.Serialize(other);
            await context.Secrets.SetAsync($"apikey:{provider.Id}", "sk-instance");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings
                {
                    Providers = [provider, other],
                    ActiveModelId = other.Models[0].Id,
                    FailoverChain = [new FailoverEntry { ModelId = other.Models[0].Id }]
                });
            try
            {
                view.FocusEntry(provider.CatalogId, provider.Id);
                Button primary = Find<Button>(view, "SetupPrimaryButton");
                Click(primary);
                await endpoint.Requested.WaitAsync(TimeSpan.FromSeconds(10));
                if (editDuringPull)
                {
                    foreign.SystemPrompt = "saved while fetching";
                    await store.SaveAsync(settings);
                }
                endpoint.Release();
                await WaitForEnabledAsync(primary);
                string[] expected = editDuringPull
                    ? ["local-model", "foreign-model", "tuned-model", "manual-model"]
                    : ["local-model", "tuned-model", "manual-model"];
                Assert.AreSequenceEqual(expected, [.. provider.Models.Select(model => model.Model)]);
                Assert.AreSequenceEqual(["local-model"], provider.AvailableModels);
                Assert.AreEqual(other.Models[0].Id, settings.ActiveModelId);
                Assert.AreSequenceEqual([other.Models[0].Id], [.. settings.FailoverChain.Select(entry => entry.ModelId)]);
                AiSettings persisted = await store.LoadAsync();
                Assert.AreSequenceEqual(expected, [.. persisted.Providers.Single(item => item.Id == provider.Id).Models.Select(model => model.Model)]);
                Assert.AreEqual(settings.ActiveModelId, persisted.ActiveModelId);
                Assert.AreEqual(otherJson, System.Text.Json.JsonSerializer.Serialize(persisted.Providers.Single(item => item.Id == other.Id)));
            }
            finally { endpoint.Release(); window.Close(); }
        });
    }

    /// <summary>
    /// 端点清单里暂时少了的模型,只要当前选中、故障转移链或 IM 桥接还指着它就不清理 ——
    /// 中转站渠道被自动禁用时清单会缩水;删掉的话选择漂到别处、用户排好的链永久丢一站。
    /// </summary>
    [TestMethod]
    [DataRow("active")]
    [DataRow("failover")]
    [DataRow("bridge")]
    public void PullModels_KeepsUnlistedModelsThatAreStillReferenced(string reference)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new HeldModelsEndpoint(response: """{"data":[{"id":"local-model"}]}""");
            endpoint.Release();
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = endpoint.BaseUrl + "/v1";
            var catalog = new ModelsDevCatalog(context);
            ModelsDevCatalog.Materialise(provider, catalog.Describe(null, [("local-model", 0), ("referenced-model", 0), ("stale-model", 0)]));
            AiModelConfig local = provider.Models.Single(model => model.Model == "local-model");
            AiModelConfig referenced = provider.Models.Single(model => model.Model == "referenced-model");
            await context.Secrets.SetAsync($"apikey:{provider.Id}", "sk-instance");
            if (reference == "bridge")
            {
                await new Bridge.BridgeSettingsStore(context).SaveAsync(new Bridge.BridgeSettings { ModelId = referenced.Id });
            }
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings
                {
                    Providers = [provider],
                    ActiveModelId = reference == "active" ? referenced.Id : local.Id,
                    FailoverChain = reference == "failover" ? [new FailoverEntry { ModelId = referenced.Id }] : []
                });
            try
            {
                view.FocusEntry(provider.CatalogId, provider.Id);
                await ClickPrimaryAsync(view);

                Assert.AreSequenceEqual(["local-model", "referenced-model"], [.. provider.Models.Select(model => model.Model)],
                    "没人引用的过期自动模型照常清理,被引用的留下");
                Assert.AreSequenceEqual(["local-model"], provider.AvailableModels);
                Assert.AreEqual(reference == "active" ? referenced.Id : local.Id, settings.ActiveModelId);
                Assert.AreEqual(reference == "failover", settings.FailoverChain.Any(entry => entry.ModelId == referenced.Id));
                AiSettings persisted = await store.LoadAsync();
                Assert.AreSequenceEqual(["local-model", "referenced-model"],
                    [.. persisted.Providers.Single().Models.Select(model => model.Model)]);
                Assert.AreEqual(settings.ActiveModelId, persisted.ActiveModelId);
            }
            finally { endpoint.Release(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("custom-openai")]
    [DataRow("custom-anthropic")]
    public void NewProvider_CancelAndReselectNeverReturnsToEditingSource(string catalogId)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider source = ProviderCatalog.Find(catalogId)!.CreateProvider();
            source.Name = "original";
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [source] });
            string original = System.Text.Json.JsonSerializer.Serialize(source);
            try
            {
                await ClickRowAsync(window, view, catalogId);
                Find<TextBox>(view, "SetupNameBox").Text = "cancelled";
                Click(Find<Button>(view, "SetupSecondaryButton"));
                Assert.IsNull(FindOrNull<Button>(view, "SetupPrimaryButton"));
                await ClickRowAsync(window, view, catalogId);
                Assert.AreEqual("original 2", Find<TextBox>(view, "SetupNameBox").Text);
                Assert.IsFalse(Find<Button>(view, "SetupAddAnotherButton").IsVisible);
                Assert.AreEqual(original, System.Text.Json.JsonSerializer.Serialize(settings.Providers.Single()));
                Assert.AreEqual(original, System.Text.Json.JsonSerializer.Serialize((await store.LoadAsync()).Providers.Single()));
            }
            finally { window.Close(); }
        });
    }

    /// <summary>
    /// 「新增供应商」窗口里加完一家,只有那一行转入编辑;换到另一种已有实例的类型仍是新建 ——
    /// 打开旧实例的编辑表单并预填它的 Key,用户以为在新建,一保存就覆盖了旧实例。
    /// </summary>
    [TestMethod]
    public void NewProviderWindow_AfterOneCreate_OtherTypesStillOpenAsNewInstances()
    {
        OnUi(async () =>
        {
            await using var endpoint = new SseStub("", jsonContent: "[]");
            using var context = new TestPluginContext();
            AiProvider old = ProviderCatalog.Find("custom-anthropic")!.CreateProvider();
            old.Name = "old-deepseek";
            old.BaseUrl = endpoint.BaseUrl + "/v1";
            await context.Secrets.SetAsync($"apikey:{old.Id}", "sk-old");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [old] });
            string oldJson = System.Text.Json.JsonSerializer.Serialize(old);
            try
            {
                await ClickRowAsync(window, view, "custom-openai");
                Find<TextBox>(view, "SetupBaseUrlBox").Text = endpoint.BaseUrl + "/v1";
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-relay";
                await ClickPrimaryAsync(view);
                Assert.HasCount(2, settings.Providers, "前提:第一家已经加上");
                Assert.AreEqual("Save", (string?)Find<Button>(view, "SetupPrimaryButton").Content, "刚加的那一行转入编辑");

                await ClickRowAsync(window, view, "custom-anthropic");
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                await PumpAsync(20);
                Assert.AreEqual("old-deepseek 2", Find<TextBox>(view, "SetupNameBox").Text, "换到已有实例的类型仍以它为底稿新建");
                Assert.AreEqual("", Find<TextBox>(view, "SetupKeyBox").Text ?? "", "新建不能预填旧实例的 Key");
                Assert.AreEqual("Add", (string?)Find<Button>(view, "SetupPrimaryButton").Content);
                Assert.AreEqual("Cancel", (string?)Find<Button>(view, "SetupSecondaryButton").Content);

                Find<TextBox>(view, "SetupKeyBox").Text = "sk-second";
                await ClickPrimaryAsync(view);
                Assert.HasCount(3, settings.Providers, "保存的是一份新实例");
                Assert.AreEqual(oldJson, System.Text.Json.JsonSerializer.Serialize(old));
                Assert.AreEqual("sk-old", await store.GetApiKeyAsync(old.Id));

                await ClickRowAsync(window, view, "custom-openai");
                Assert.AreEqual("Add", (string?)Find<Button>(view, "SetupPrimaryButton").Content,
                    "收起后重开刚加过的那一类,同样是再新建一份");
            }
            finally { window.Close(); }
        });
    }

    /// <summary>
    /// 复用的目录窗口再点一次「新增供应商」,不能丢掉填到一半的表单。
    /// </summary>
    [TestMethod]
    public void NewProviderAgain_KeepsTheUnsavedDraft()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context);
            try
            {
                await ClickRowAsync(window, view, "custom-openai");
                var key = Find<TextBox>(view, "SetupKeyBox");
                key.Text = "sk-half-filled";
                view.StartNewProvider();
                await PumpAsync();

                Assert.AreSame(key, Find<TextBox>(view, "SetupKeyBox"));
                Assert.AreEqual("sk-half-filled", key.Text);
            }
            finally
            {
                await PumpAsync();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 填 Key 的新建草稿自成一体(不带来源的 Key 与模型清单):来源实例在别处被删,
    /// 草稿照样能加成独立实例,不该以「来源已移除」拒绝。
    /// </summary>
    [TestMethod]
    public void NewProvider_ApiKeyDraftStillAddsAfterItsSourceIsRemoved()
    {
        OnUi(async () =>
        {
            await using var endpoint = new SseStub("", jsonContent: "[]");
            using var context = new TestPluginContext();
            AiProvider source = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            source.Name = "relay";
            source.BaseUrl = endpoint.BaseUrl + "/v1";
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [source] });
            try
            {
                await ClickRowAsync(window, view, "custom-openai");
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-new";
                settings.Providers.Remove(source);
                await store.SaveAsync(settings);
                await ClickPrimaryAsync(view);

                Assert.AreNotEqual(new Loc("en")["SetupSourceRemoved"], Find<TextBlock>(view, "SetupProgressText").Text);
                AiProvider created = settings.Providers.Single();
                Assert.AreNotEqual(source.Id, created.Id);
                Assert.AreEqual("relay 2", created.Name);
                Assert.AreEqual(source.BaseUrl, created.BaseUrl);
                Assert.AreEqual("sk-new", await store.GetApiKeyAsync(created.Id));
                Assert.AreEqual(created.Id, (await store.LoadAsync()).Providers.Single().Id);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_AddsEncryptedSlotWithoutCloningProviderOrModels()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var endpoint = new SseStub("");
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = endpoint.BaseUrl + "/v1";
            string modelId = provider.Models[0].Id;
            await context.Secrets.SetAsync($"apikey:{provider.Id}", "sk-first");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider], ActiveModelId = modelId }, focus: "custom-openai");
            try
            {
                int changed = 0;
                view.ProviderChanged += id => { Assert.AreEqual(provider.Id, id); changed++; };
                Assert.AreEqual("Add API Key", Find<Button>(view, "SetupAddAnotherButton").Content);
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                Assert.IsNull(FindOrNull<TextBox>(view, "SetupBaseUrlBox"));
                Assert.IsNull(FindOrNull<TextBox>(view, "SetupNameBox"));
                Assert.IsNull(FindOrNull<TextBox>(view, "SetupModelBox"));
                Assert.AreEqual("", Find<TextBox>(view, "SetupKeyBox").Text ?? "");
                Assert.AreEqual('●', Find<TextBox>(view, "SetupKeyBox").PasswordChar);
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-second";
                await ClickPrimaryAsync(view);
                Assert.HasCount(1, settings.Providers);
                Assert.HasCount(1, provider.AdditionalApiKeyIds);
                Assert.AreEqual("sk-first", await store.GetApiKeyAsync(provider.Id));
                Assert.AreEqual("sk-second", await store.GetApiKeyAsync(provider.AdditionalApiKeyIds[0]));
                Assert.AreEqual(modelId, provider.Models[0].Id);
                Assert.AreEqual(modelId, settings.ActiveModelId);
                Assert.AreEqual(1, changed);
                Assert.IsEmpty(endpoint.Requests, "添加 Key 不得拉取模型或发送 HTTP");
                Assert.HasCount(1, (await store.LoadAsync()).Providers[0].AdditionalApiKeyIds);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ExistingProvider_BaseUrlToAnotherOriginAsksThenClearsKeysAndSkipsThePull()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var oldEndpoint = new SseStub("");
            await using var newEndpoint = new SseStub("");
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = oldEndpoint.BaseUrl + "/v1";
            string extra = Guid.NewGuid().ToString("N");
            provider.AdditionalApiKeyIds = [extra];
            await context.Secrets.SetAsync($"apikey:{provider.Id}", "sk-first");
            await context.Secrets.SetAsync($"apikey:{extra}", "sk-second");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id },
                    focus: "custom-openai");
            try
            {
                var loc = new Loc("en");
                TextBlock progress = Find<TextBlock>(view, "SetupProgressText");
                Assert.AreEqual("sk-first", Find<TextBox>(view, "SetupKeyBox").Text, "表单预填着旧地址的 Key");
                Find<TextBox>(view, "SetupBaseUrlBox").Text = newEndpoint.BaseUrl + "/v1";
                Assert.AreEqual(loc.F("SetupBaseUrlKeyResetConfirm", 2), await ClickPrimaryUntilConfirmAsync(view));
                Assert.AreEqual(oldEndpoint.BaseUrl + "/v1", provider.BaseUrl, "没点确认就什么都不改");
                Assert.AreEqual("sk-first", await store.GetApiKeyAsync(provider.Id));
                Assert.IsFalse(Find<Button>(view, "SetupPrimaryButton").IsEnabled, "框开着时主按钮锁着,连点不算确认");

                // 先取消一次:什么都不改,草稿留着
                await AnswerConfirmAsync(view, accept: false);
                Assert.AreEqual(loc["SetupResetCancelled"], progress.Text);
                Assert.AreEqual(oldEndpoint.BaseUrl + "/v1", provider.BaseUrl);
                Assert.AreEqual("sk-first", await store.GetApiKeyAsync(provider.Id));
                Assert.AreEqual(newEndpoint.BaseUrl + "/v1", Find<TextBox>(view, "SetupBaseUrlBox").Text);

                Assert.AreEqual(loc.F("SetupBaseUrlKeyResetConfirm", 2), await ClickPrimaryUntilConfirmAsync(view));
                await AnswerConfirmAsync(view, accept: true);
                Assert.AreEqual(loc["SetupSavedKeysCleared"], progress.Text);
                Assert.AreEqual(newEndpoint.BaseUrl + "/v1", provider.BaseUrl);
                Assert.IsNull(await store.GetApiKeyAsync(provider.Id));
                Assert.IsNull(await context.Secrets.GetAsync($"apikey:{extra}"));
                Assert.IsEmpty(provider.AdditionalApiKeyIds);
                Assert.AreEqual("", Find<TextBox>(view, "SetupKeyBox").Text ?? "", "清掉的 Key 不再摆在框里");
                Assert.IsEmpty(newEndpoint.Requests, "新地址还没有 Key,不去拉模型");
                Assert.IsEmpty(oldEndpoint.Requests);

                // 之后为新地址填 Key:照常保存,不再提示
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-new";
                await ClickPrimaryAsync(view);
                Assert.AreEqual("sk-new", await store.GetApiKeyAsync(provider.Id));
                Assert.IsTrue(newEndpoint.Authorizations.All(header => header is null or "Bearer sk-new"),
                    string.Join(", ", newEndpoint.Authorizations));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ExistingProvider_KeyAddedElsewhereAfterThePromptAsksAgain()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var oldEndpoint = new SseStub("");
            await using var newEndpoint = new SseStub("");
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = oldEndpoint.BaseUrl + "/v1";
            await context.Secrets.SetAsync($"apikey:{provider.Id}", "sk-first");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id },
                    focus: "custom-openai");
            try
            {
                var loc = new Loc("en");
                TextBlock progress = Find<TextBlock>(view, "SetupProgressText");
                Find<TextBox>(view, "SetupBaseUrlBox").Text = newEndpoint.BaseUrl + "/v1";
                Assert.AreEqual(loc.F("SetupBaseUrlKeyResetConfirm", 1), await ClickPrimaryUntilConfirmAsync(view));

                // 确认框开着的时候另一个窗口给这一家加了 Key:点确认后这边冲突重载,什么都不清
                var external = new AiSettingsStore(context);
                AiSettings other = await external.LoadAsync();
                string added = await external.AddProviderApiKeyAsync(other, other.Providers[0], "sk-added-elsewhere");
                await AnswerConfirmAsync(view, accept: true);
                Assert.AreEqual(loc["SetupConfigChanged"], progress.Text);
                Assert.AreEqual(newEndpoint.BaseUrl + "/v1", Find<TextBox>(view, "SetupBaseUrlBox").Text, "草稿地址保留");
                Assert.AreEqual("sk-added-elsewhere", await store.GetApiKeyAsync(added), "旧的确认不能把它一起清掉");

                // 再存:按最新的两把重新问
                Assert.AreEqual(loc.F("SetupBaseUrlKeyResetConfirm", 2), await ClickPrimaryUntilConfirmAsync(view));
                Assert.AreEqual("sk-added-elsewhere", await store.GetApiKeyAsync(added));
                await AnswerConfirmAsync(view, accept: true);
                Assert.AreEqual(loc["SetupSavedKeysCleared"], progress.Text);
                Assert.IsNull(await store.GetApiKeyAsync(added));
                Assert.AreEqual(newEndpoint.BaseUrl + "/v1", provider.BaseUrl);
                Assert.IsEmpty(newEndpoint.Requests);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ExistingProvider_ModelKeysClearedStillPullsTheNewModelList(bool newProviderKey)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var oldEndpoint = new SseStub("");
            await using var newEndpoint = new SseStub("", jsonContent: "");
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = oldEndpoint.BaseUrl + "/v1";
            provider.Models[0].Model = "inherits";
            var own = new AiModelConfig { Model = "own-model", HasOwnApiKey = true };
            provider.Models.Add(own);
            if (newProviderKey) await context.Secrets.SetAsync($"apikey:{provider.Id}", "sk-first");
            await context.Secrets.SetAsync($"apikey:{own.Id}", "own-old");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id },
                    focus: "custom-openai");
            try
            {
                var loc = new Loc("en");
                TextBlock progress = Find<TextBlock>(view, "SetupProgressText");
                // 换到新主机,只清掉模型自带的那把:同时填好了新主机的 Key,或者这家本来就不用 Key(无鉴权接入),
                // 都不是「供应商的 Key 没了」
                Find<TextBox>(view, "SetupBaseUrlBox").Text = newEndpoint.BaseUrl + "/v1";
                if (newProviderKey) Find<TextBox>(view, "SetupKeyBox").Text = "sk-new";
                Assert.AreEqual(loc.F("SetupBaseUrlKeyResetConfirm", 1) + "\n" + loc.F("SetupBaseUrlKeyResetModels", "own-model"),
                    await ClickPrimaryUntilConfirmAsync(view));
                await AnswerConfirmAsync(view, accept: true);
                Assert.AreEqual(newProviderKey ? "sk-new" : null, await store.GetApiKeyAsync(provider.Id));
                Assert.IsNull(await store.GetApiKeyAsync(own.Id));
                // 照常拉新地址的模型清单,点名提示仍在
                Assert.IsNotEmpty(newEndpoint.Requests, "换了地址要拉新地址的模型清单");
                string named = "\n" + loc.F("SetupModelKeysCleared", "own-model");
                Assert.IsTrue((progress.Text ?? "").EndsWith(named, StringComparison.Ordinal), progress.Text);
                Assert.IsEmpty(oldEndpoint.Requests);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ExistingProvider_BlankKeyClearedByTheMoveIsNotKeptAsTheBaseline()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var oldEndpoint = new SseStub("");
            await using var newEndpoint = new SseStub("", jsonContent: "");
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = oldEndpoint.BaseUrl + "/v1";
            // 只有空白的「Key」(界面当没有):换源保存时照清,不必确认,也不在清掉的名单里
            await context.Secrets.SetAsync($"apikey:{provider.Id}", "   ");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id },
                    focus: "custom-openai");
            try
            {
                var loc = new Loc("en");
                Find<TextBox>(view, "SetupBaseUrlBox").Text = newEndpoint.BaseUrl + "/v1";
                await ClickPrimaryAsync(view);
                Assert.AreEqual(newEndpoint.BaseUrl + "/v1", provider.BaseUrl);
                Assert.IsNull(await context.Secrets.GetAsync($"apikey:{provider.Id}"));

                // 同一张表单接着再存(新主机上换个路径):Key 基线若还是那串空白,就对不上库里的「没有」,被误报成配置已变
                Find<TextBox>(view, "SetupBaseUrlBox").Text = newEndpoint.BaseUrl + "/v2";
                await ClickPrimaryAsync(view);
                Assert.AreNotEqual(loc["SetupConfigChanged"], Find<TextBlock>(view, "SetupProgressText").Text);
                Assert.AreEqual(newEndpoint.BaseUrl + "/v2", provider.BaseUrl);
                Assert.IsEmpty(oldEndpoint.Requests);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ExistingProvider_ClearedModelsNoteDoesNotLandOnAnotherCard()
    {
        OnUi(async () =>
        {
            using var endpoint = new HeldModelsEndpoint();
            var storage = new KeySaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            await using var oldEndpoint = new SseStub("");
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = oldEndpoint.BaseUrl + "/v1";
            provider.Models[0].Model = "inherits";
            var own = new AiModelConfig { Model = "own-model", HasOwnApiKey = true };
            provider.Models.Add(own);
            await context.Secrets.SetAsync($"apikey:{provider.Id}", "sk-first");
            await context.Secrets.SetAsync($"apikey:{own.Id}", "own-old");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id },
                    focus: "custom-openai");
            var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.ModelsChanged += () => pulled.TrySetResult();
            try
            {
                var loc = new Loc("en");
                // 换到新主机并填好新 Key:只清模型自带的那把,保存后照常拉新地址的模型清单
                Find<TextBox>(view, "SetupBaseUrlBox").Text = endpoint.BaseUrl + "/v1";
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-new";
                Assert.AreEqual(loc.F("SetupBaseUrlKeyResetConfirm", 1) + "\n" + loc.F("SetupBaseUrlKeyResetModels", "own-model"),
                    await ClickPrimaryUntilConfirmAsync(view));

                // 点了确认、写盘途中,用户打开了另一张卡片
                storage.Hold = true;
                Click(ConfirmBox(view).GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ConfirmAcceptButton"));
                await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                view.FocusEntry("custom-anthropic");
                TextBlock other = Find<TextBlock>(view, "SetupProgressText");
                storage.Release.TrySetResult();
                await endpoint.Requested.WaitAsync(TimeSpan.FromSeconds(10));
                endpoint.Release();
                await pulled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await PumpAsync(10);
                Assert.IsNull(await store.GetApiKeyAsync(own.Id), "前提:这次保存确实清了模型自带的 Key");
                // 「这些模型要重填 Key」是刚才那张卡片的保存结果,不能接到另一张卡片的进度行上
                Assert.DoesNotContain(loc.F("SetupModelKeysCleared", "own-model"), other.Text ?? "");
            }
            finally
            {
                storage.Release.TrySetResult();
                endpoint.Release();
                window.Close();
            }
        });
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("collapse")]
    [DataRow("close")]
    public void ProviderApiKey_InFlightCancelledDraftNeverWritesToSourceOrAnotherInstance(string action)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider source = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            source.BaseUrl = "https://original.example/v1";
            await context.Secrets.SetAsync($"apikey:{source.Id}", "sk-source");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [source] }, focus: "custom-openai");
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var gate = (SemaphoreSlim)typeof(AiSettingsStore).GetProperty("ProviderKeyGate", flags)!.GetValue(store)!;
            await gate.WaitAsync();
            bool released = false;
            try
            {
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-never-saved";
                object row = ((System.Collections.IEnumerable)typeof(ProviderSetupView)
                    .GetField("_cards", flags)!.GetValue(view)!).Cast<object>().Single(item =>
                        ((ProviderCatalogEntry)item.GetType().GetProperty("Entry")!.GetValue(item)!).Id == "custom-openai");
                var saving = (Task)typeof(ProviderSetupView).GetMethod("PrimaryAsync", flags)!.Invoke(view, [row])!;
                Assert.IsFalse(Find<Button>(view, "SetupPrimaryButton").IsEnabled);
                if (action == "cancel") Click(Find<Button>(view, "SetupSecondaryButton"));
                else if (action == "collapse") await ClickRowAsync(window, view, "custom-openai");
                else window.Close();
                await saving.WaitAsync(TimeSpan.FromSeconds(10));
                gate.Release();
                released = true;
                var baseline = (Task<string?>)typeof(ProviderSetupView)
                    .GetField("_primaryKeyBaseline", flags)!.GetValue(view)!;
                await baseline.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.HasCount(1, settings.Providers);
                Assert.IsEmpty(source.AdditionalApiKeyIds);
                Assert.AreEqual("sk-source", await store.GetApiKeyAsync(source.Id));
                Assert.IsEmpty((await store.LoadAsync()).Providers[0].AdditionalApiKeyIds);
            }
            finally
            {
                if (!released) gate.Release();
                window.Close();
            }
        });
    }

    /// <summary>「取消」只是放弃起草:已经落库的那份一个字都不该动,表单退回编辑它。</summary>
    [TestMethod]
    public void CancellingAnother_ReturnsToEditingTheFirstInstance()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var endpoint = new SseStub("");
            (Window window, ProviderSetupView view, AiSettings settings, _) =
                await ShowAsync(context, focus: "custom-openai");
            try
            {
                Find<TextBox>(view, "SetupBaseUrlBox").Text = endpoint.BaseUrl + "/v1";
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-relay";
                await ClickPrimaryAsync(view);
                Assert.HasCount(1, settings.Providers);

                Click(Find<Button>(view, "SetupAddAnotherButton"));
                await PumpAsync(20);
                Assert.AreEqual("Add", (string?)Find<Button>(view, "SetupPrimaryButton").Content);

                Click(Find<Button>(view, "SetupSecondaryButton"));
                await PumpAsync(20);

                Assert.HasCount(1, settings.Providers, "取消只是放弃起草,不动已经落库的那份");
                Assert.AreEqual("Save", (string?)Find<Button>(view, "SetupPrimaryButton").Content,
                    "退回编辑最近一份:能改、能存");
                Assert.AreEqual("Remove", (string?)Find<Button>(view, "SetupSecondaryButton").Content);
                Assert.IsTrue(Find<Button>(view, "SetupAddAnotherButton").IsVisible,
                    "取消之后「再添加一个」该回来");
                Assert.AreEqual("", Find<TextBlock>(view, "SetupProgressText").Text ?? "",
                    "重建出来的表单是干净的,不带着上一轮的进度文字");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 首次添加一落库,「再添加一个」就得露出来 —— 主按钮的收尾排在「拉取模型」的网络
    /// 请求后面,慢网下要等约 30 秒,期间用户想加第二份却没钮可按。
    /// 用 hold 住的端点把拉取钉在天上,断言就不靠时序。
    /// </summary>
    [TestMethod]
    public void AddAnother_AppearsAsSoonAsItIsPersisted_WhileTheModelPullIsStillInFlight()
    {
        OnUi(async () =>
        {
            await using var stub = new SseStub("data: [DONE]\n\n", jsonContent: "[]", hold: true);
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, AiSettings settings, _) =
                await ShowAsync(context, focus: "custom-openai");
            try
            {
                Find<TextBox>(view, "SetupBaseUrlBox").Text = $"{stub.BaseUrl}/v1";
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-relay";
                Click(Find<Button>(view, "SetupPrimaryButton"));
                await stub.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.HasCount(1, settings.Providers, "前提:已经落库");
                Assert.IsTrue(Find<Button>(view, "SetupAddAnotherButton").IsVisible,
                    "已落库就露出「再添加一个」—— 收尾等网络拉取回来的话,慢网下要 30 秒才加得了第二份");
            }
            finally
            {
                stub.Release(); // 兜底放行,免得在途的拉取挂到测试之后
                await WaitForEnabledAsync(Find<Button>(view, "SetupPrimaryButton"));
                window.Close();
            }
        });
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("sk-first")]
    public void ProviderApiKey_RejectsBlankOrDuplicateDraftWithoutChangingProvider(string key)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = "https://original.example/v1";
            await context.Secrets.SetAsync($"apikey:{provider.Id}", "sk-first");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider] }, focus: "custom-openai");
            try
            {
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                Find<TextBox>(view, "SetupKeyBox").Text = key;
                await ClickPrimaryAsync(view);
                Assert.HasCount(1, settings.Providers);
                Assert.IsEmpty(provider.AdditionalApiKeyIds);
                Assert.AreEqual("sk-first", await store.GetApiKeyAsync(provider.Id));
                Assert.AreEqual(new Loc("en")["SetupUniqueApiKey"], Find<TextBlock>(view, "SetupProgressText").Text);
                Assert.AreEqual("Cancel", Find<Button>(view, "SetupSecondaryButton").Content);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProviderApiKey_SupplementarySlotIsReadyAndCannotBeCopiedIntoMain(bool hasMain)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = "https://original.example/v1";
            string secondId = Guid.NewGuid().ToString("N");
            provider.AdditionalApiKeyIds.Add(secondId);
            await context.Secrets.SetAsync($"apikey:{secondId}", "sk-secondary");
            if (hasMain) await context.Secrets.SetAsync($"apikey:{provider.Id}", "sk-main");
            (Window window, ProviderSetupView view, _, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider] }, focus: "custom-openai");
            try
            {
                Assert.AreEqual("Ready", Find<TextBlock>(view, "SetupPill.custom-openai").Text);
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-secondary";
                await ClickPrimaryAsync(view);
                Assert.AreEqual(new Loc("en")["SetupUniqueApiKey"], Find<TextBlock>(view, "SetupProgressText").Text);
                Assert.AreEqual(hasMain ? "sk-main" : null, await store.GetApiKeyAsync(provider.Id));
                Assert.AreEqual("sk-secondary", await store.GetApiKeyAsync(secondId));
                Assert.HasCount(1, provider.AdditionalApiKeyIds);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("secret")]
    [DataRow("json")]
    [DataRow("cancel")]
    public void ProviderApiKey_MainSaveFailure_RestoresConfigurationAndKeepsRetryDraft(string failure)
    {
        OnUi(async () =>
        {
            await using var original = new SseStub(KeySaveProbeSse);
            await using var changed = new SseStub(KeySaveProbeSse);
            using var secretSource = new TestPluginContext();
            var secrets = new KeySaveSecrets(secretSource.Secrets);
            var storage = new KeySaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Secrets = secrets, Storage = storage };
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.Name = "original provider";
            provider.BaseUrl = original.BaseUrl + "/v1";
            provider.DefaultProtocol = ChatProtocol.OpenAiResponses;
            AiModelConfig model = provider.Models[0];
            model.Model = "original-model";
            model.Protocol = ChatProtocol.OpenAiChatCompletions;
            model.MaxTokens = 1234;
            model.Reasoning = ReasoningLevel.High;
            string additionalId = Guid.NewGuid().ToString("N");
            provider.AdditionalApiKeyIds.Add(additionalId);
            await secrets.SetAsync($"apikey:{provider.Id}", "sk-original");
            await secrets.SetAsync($"apikey:{additionalId}", "sk-additional");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider], ActiveModelId = model.Id }, "custom-openai");
            int notifications = 0;
            view.ProviderChanged += _ => notifications++;
            try
            {
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                Find<TextBox>(view, "SetupNameBox").Text = "new provider";
                Find<TextBox>(view, "SetupBaseUrlBox").Text = changed.BaseUrl + "/v1";
                Find<TextBox>(view, "SetupModelBox").Text = "new-model";
                Find<ComboBox>(view, "SetupProtocolCombo").SelectedIndex = (int)ChatProtocol.AnthropicMessages;
                TextBox keyBox = Find<TextBox>(view, "SetupKeyBox");
                string draftKey = failure == "duplicate" ? "sk-additional" : "sk-new";
                keyBox.Text = draftKey;
                secrets.Fail = failure == "secret";
                storage.Failure = failure;
                // 换到了另一台主机:先确认清掉旧地址的补充槽。确认之后写机密 / 写 JSON 失败或被取消都要整个恢复;
                // 新 Key 与补充槽那把相同在换源时不算重复(旧 Key 都要清),那一行改为在确认框里取消,同样什么都不改
                Assert.AreEqual(new Loc("en").F("SetupBaseUrlKeyResetConfirm", 1), await ClickPrimaryUntilConfirmAsync(view));
                await AnswerConfirmAsync(view, accept: failure != "duplicate");

                Assert.AreEqual(original.BaseUrl + "/v1", provider.BaseUrl, "失败不能把旧凭据留在新端点");
                Assert.AreEqual("original provider", provider.Name);
                Assert.AreEqual(ChatProtocol.OpenAiResponses, provider.DefaultProtocol);
                Assert.AreSame(model, provider.Models[0]);
                Assert.AreEqual("original-model", model.Model);
                Assert.AreEqual(ChatProtocol.OpenAiChatCompletions, model.Protocol);
                Assert.AreEqual(1234, model.MaxTokens);
                Assert.AreEqual(ReasoningLevel.High, model.Reasoning);
                AiProvider persisted = (await store.LoadAsync()).Providers.Single();
                Assert.AreEqual(provider.BaseUrl, persisted.BaseUrl);
                Assert.AreEqual(provider.Name, persisted.Name);
                Assert.AreEqual(provider.DefaultProtocol, persisted.DefaultProtocol);
                Assert.AreEqual(model.Model, persisted.Models[0].Model);
                Assert.AreEqual(model.Protocol, persisted.Models[0].Protocol);
                Assert.AreEqual("sk-original", await store.GetApiKeyAsync(provider.Id));
                Assert.AreEqual("sk-original", await secretSource.Secrets.GetAsync($"apikey:{provider.Id}"));
                Assert.AreEqual(0, notifications);
                Assert.AreSame(keyBox, Find<TextBox>(view, "SetupKeyBox"));
                Assert.AreEqual(draftKey, keyBox.Text, "保留本次 Key 草稿供重试");
                Assert.AreEqual(changed.BaseUrl + "/v1", Find<TextBox>(view, "SetupBaseUrlBox").Text);
                Assert.AreEqual("new-model", Find<TextBox>(view, "SetupModelBox").Text);
                Assert.IsTrue(Find<Button>(view, "SetupPrimaryButton").IsEnabled);

                (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(provider, model));
                Assert.IsNull(error, error?.ToString());
                Assert.AreEqual("Bearer sk-original", original.Authorizations.Single());
                Assert.Contains("\"model\":\"original-model\"", original.Requests.Single());
                Assert.IsEmpty(changed.Requests, "失败后消费者不能向新主机发送旧 Key");
                Assert.HasCount(1, settings.Providers);
            }
            finally { secrets.Released.TrySetResult(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProviderApiKey_MainSaveSuccess_PersistsChangedFormEvenWhenKeyIsUnchanged(bool sameKey)
    {
        OnUi(async () =>
        {
            await using var original = new SseStub(KeySaveProbeSse);
            await using var changed = new SseStub(KeySaveProbeSse);
            using var context = new TestPluginContext();
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = original.BaseUrl + "/v1";
            provider.Models[0].Model = "original-model";
            await context.Secrets.SetAsync($"apikey:{provider.Id}", "sk-original");
            (Window window, ProviderSetupView view, _, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider] }, "custom-openai");
            try
            {
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                Find<TextBox>(view, "SetupNameBox").Text = "saved provider";
                Find<TextBox>(view, "SetupBaseUrlBox").Text = changed.BaseUrl + "/v1";
                Find<TextBox>(view, "SetupModelBox").Text = "saved-model";
                TextBox keyBox = Find<TextBox>(view, "SetupKeyBox");
                keyBox.Text = "sk-typed";
                keyBox.Text = sameKey ? "sk-original" : "sk-new";
                await ClickPrimaryAsync(view);
                AiProvider persisted = (await store.LoadAsync()).Providers.Single();
                Assert.AreEqual("saved provider", persisted.Name);
                Assert.AreEqual(changed.BaseUrl + "/v1", persisted.BaseUrl);
                Assert.AreEqual("saved-model", persisted.Models[0].Model);
                (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(provider, provider.Models[0]));
                Assert.IsNull(error, error?.ToString());
                Assert.IsEmpty(original.Requests);
                Assert.IsNotEmpty(changed.Requests);
                Assert.IsTrue(changed.Authorizations.All(auth => auth == (sameKey ? "Bearer sk-original" : "Bearer sk-new")),
                    "成功后模型拉取与正式消费者必须使用保存后的 URL/Key 配对");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProviderApiKey_OpenedNameBaseline_RejectsExternalRenameWithoutOverwriting(bool separateSnapshot)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var endpoint = new SseStub(KeySaveProbeSse);
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.Name = "opened-name";
            provider.BaseUrl = endpoint.BaseUrl + "/v1";
            provider.Models[0].Model = "m";
            await context.Secrets.SetAsync($"apikey:{provider.Id}", "sk-original");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider] }, "custom-openai");
            int notifications = 0;
            view.ProviderChanged += _ => notifications++;
            try
            {
                AiSettings external = separateSnapshot ? await store.LoadAsync() : settings;
                external.Providers.Single().Name = "external-name";
                await store.SaveAsync(external);
                Assert.AreEqual("opened-name", Find<TextBox>(view, "SetupNameBox").Text);
                await ClickPrimaryAsync(view);
                AiProvider persisted = (await store.LoadAsync()).Providers.Single();
                Assert.AreEqual("external-name", persisted.Name);
                Assert.AreEqual("external-name", Find<TextBox>(view, "SetupNameBox").Text,
                    "未编辑名称必须跟随外部更新,不能把原值当成待覆盖草稿");
                Assert.AreEqual(0, notifications);
                Assert.IsEmpty(endpoint.Requests, "冲突保存不得继续拉取模型或发布成功通知");
                await ClickPrimaryAsync(view);
                Assert.AreEqual("external-name", provider.Name);
                Assert.AreEqual("external-name", (await store.LoadAsync()).Providers.Single().Name);
                (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(provider, provider.Models[0]));
                Assert.IsNull(error, error?.ToString());
                Assert.AreEqual("Bearer sk-original", endpoint.Authorizations[^1]);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("sk-original")]
    [DataRow(null)]
    public void ProviderApiKey_OpenedKeyBaseline_RejectsExternalRotationIncludingEmptySlot(string? originalKey)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var endpoint = new SseStub(KeySaveProbeSse);
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = endpoint.BaseUrl + "/v1";
            provider.Models[0].Model = "m";
            if (originalKey is not null) await context.Secrets.SetAsync($"apikey:{provider.Id}", originalKey);
            (Window window, ProviderSetupView view, _, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider] }, "custom-openai");
            int notifications = 0;
            view.ProviderChanged += _ => notifications++;
            try
            {
                Find<TextBox>(view, "SetupKeyBox").Text = "stale-form-key";
                await store.SetApiKeyAsync(provider.Id, "new-key");
                await ClickPrimaryAsync(view);
                Assert.AreEqual("new-key", await store.GetApiKeyAsync(provider.Id));
                Assert.AreEqual("stale-form-key", Find<TextBox>(view, "SetupKeyBox").Text);
                Assert.AreEqual(0, notifications);
                Assert.IsEmpty(endpoint.Requests);
                (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(provider, provider.Models[0]));
                Assert.IsNull(error, error?.ToString());
                Assert.IsNotEmpty(endpoint.Requests);
                Assert.IsTrue(endpoint.Authorizations.All(auth => auth == "Bearer new-key"),
                    "真实模型消费者必须继续使用外部轮换后的 Key");
                await ClickPrimaryAsync(view);
                Assert.AreEqual("stale-form-key", await store.GetApiKeyAsync(provider.Id));
                Assert.AreEqual(1, notifications, "用户核对后再次保存必须成功,不能永久拒绝旧表单");
                (Exception? retryError, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(provider, provider.Models[0]));
                Assert.IsNull(retryError, retryError?.ToString());
                Assert.AreEqual("Bearer stale-form-key", endpoint.Authorizations[^1]);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("name")]
    [DataRow("url")]
    [DataRow("model")]
    [DataRow("protocol")]
    [DataRow("key")]
    public void ProviderApiKey_UnsavedProviderEditsPreventEnteringKeyDraft(string field)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = "https://original.example/v1";
            (Window window, ProviderSetupView view, _, _) =
                await ShowAsync(context, new AiSettings { Providers = [provider] }, focus: "custom-openai");
            try
            {
                TextBox original = Find<TextBox>(view, "SetupKeyBox");
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                switch (field)
                {
                    case "name": Find<TextBox>(view, "SetupNameBox").Text = "unsaved"; break;
                    case "url": Find<TextBox>(view, "SetupBaseUrlBox").Text = "https://new.example/v1"; break;
                    case "model": Find<TextBox>(view, "SetupModelBox").Text = "new-model"; break;
                    case "protocol": Find<ComboBox>(view, "SetupProtocolCombo").SelectedIndex = 1; break;
                    case "key": original.Text = "sk-unsaved"; break;
                }
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                Assert.AreSame(original, Find<TextBox>(view, "SetupKeyBox"));
                Assert.AreEqual(new Loc("en")["SetupSaveProviderFirst"], Find<TextBlock>(view, "SetupProgressText").Text);
                Assert.IsEmpty(provider.AdditionalApiKeyIds);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_LateModelsNeverOverwriteEmptyKeyDraftOrTriggerAnotherPull()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new HeldModelsEndpoint();
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = endpoint.BaseUrl + "/v1";
            await context.Secrets.SetAsync($"apikey:{provider.Id}", "sk-first");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider] }, focus: "custom-openai");
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.ModelsChanged += () => arrived.TrySetResult();
            try
            {
                Click(Find<Button>(view, "SetupPrimaryButton"));
                await endpoint.Requested.WaitAsync(TimeSpan.FromSeconds(10));
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                TextBox draft = Find<TextBox>(view, "SetupKeyBox");
                draft.Text = "sk-second";
                endpoint.Release();
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreSame(draft, Find<TextBox>(view, "SetupKeyBox"));
                Assert.AreEqual("sk-second", draft.Text);
                Assert.IsNull(FindOrNull<TextBox>(view, "SetupModelBox"));
                await ClickPrimaryAsync(view);
                Assert.HasCount(1, settings.Providers);
                Assert.HasCount(1, provider.AdditionalApiKeyIds);
                Assert.AreEqual("sk-second", await store.GetApiKeyAsync(provider.AdditionalApiKeyIds[0]));
                Assert.Contains("old-model", provider.AvailableModels);
            }
            finally { endpoint.Release(); window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_EmptyPoolRefillsOriginalSlotAndRemovalDeletesEverySecret()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = "https://original.example/v1";
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider] }, focus: "custom-openai");
            try
            {
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-first";
                await ClickPrimaryAsync(view);
                Assert.IsEmpty(provider.AdditionalApiKeyIds);
                Assert.AreEqual("sk-first", await store.GetApiKeyAsync(provider.Id));
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-second";
                await ClickPrimaryAsync(view);
                string secondId = provider.AdditionalApiKeyIds.Single();
                var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                view.ProviderChanged += _ => removed.TrySetResult();
                Click(Find<Button>(view, "SetupSecondaryButton"));
                await removed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsNull(await store.GetApiKeyAsync(provider.Id));
                Assert.IsNull(await store.GetApiKeyAsync(secondId));
            }
            finally { window.Close(); }
        });
    }

    /// <summary>
    /// #582 复审⑤:拉模型把首模型自动填成清单里的型号之后,型号框要跟上;再点保存不能把它写回空串。
    /// </summary>
    /// <remarks>
    /// 自定义接入的出厂首模型型号是空的(规格来源也是空 id),拉到清单时由 <c>Materialise</c> 对齐成清单里的头一个。
    /// 改前型号框还是空的,表单的输入基线也还是空 —— 用户什么都没改,再点一次保存,<c>ApplyForm</c>
    /// 就把空的型号框写回首模型,刚填进去的型号没了。用户自己改过的型号框不在此列(见下一条用例)。
    /// </remarks>
    [TestMethod]
    [DataRow("save", DisplayName = "保存后自动拉取")]
    [DataRow("pull", DisplayName = "点「拉取模型」")]
    public void PulledModels_RefreshTheUntouchedModelBoxAndSaveKeepsIt(string trigger)
    {
        OnUi(async () =>
        {
            using var endpoint = new HeldModelsEndpoint();
            endpoint.Release();
            using var context = new TestPluginContext();
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = endpoint.BaseUrl + "/v1";
            Assert.AreEqual("", provider.Models[0].Model, "前提:出厂首模型的型号是空的");
            (Window window, ProviderSetupView view, _, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider] }, focus: "custom-openai");
            int pulls = 0;
            view.ModelsChanged += () => pulls++;
            try
            {
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                TextBox model = Find<TextBox>(view, "SetupModelBox");
                Assert.AreEqual("", model.Text);
                if (trigger == "save")
                {
                    await ClickPrimaryAsync(view);
                }
                else
                {
                    Button pull = Find<Button>(view, "SetupPullButton");
                    Click(pull);
                    await WaitForEnabledAsync(pull);
                }
                for (int i = 0; i < 400 && pulls == 0; i++) await PumpAsync(2);
                Assert.IsGreaterThan(0, pulls, "清单应当已经拉到并落库");
                await PumpAsync(2);
                Assert.AreEqual("old-model", provider.Models[0].Model, "前提:拉取把首模型对齐到了清单里的型号");
                Assert.AreSame(model, Find<TextBox>(view, "SetupModelBox"), "不该为此重建表单");
                Assert.AreEqual("old-model", model.Text, "用户没碰过的型号框要跟上自动填入的型号");

                await ClickPrimaryAsync(view);
                Assert.AreEqual(2, pulls, "再次保存应完成第二次模型清单刷新,不能靠请求超时才返回");
                Assert.AreEqual("old-model", provider.Models[0].Model, "再点保存不能把空的型号框写回去");
                Assert.AreEqual("old-model", (await store.LoadAsync()).Providers.Single().Models[0].Model);
            }
            finally
            {
                await WaitForEnabledAsync(Find<Button>(view, "SetupPrimaryButton"));
                window.Close();
            }
        });
    }

    [TestMethod]
    public void CurrentProviderLateModels_KeepUnsavedDraftAndOfferNewModelInPicker()
    {
        OnUi(async () =>
        {
            using var endpoint = new HeldModelsEndpoint();
            using var context = new TestPluginContext();
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.BaseUrl = endpoint.BaseUrl + "/v1";
            (Window window, ProviderSetupView view, _, _) =
                await ShowAsync(context, new AiSettings { Providers = [provider] }, focus: "custom-openai");
            var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.ModelsChanged += () => pulled.TrySetResult();
            try
            {
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                Assert.IsNull(FindOrNull<ComboBox>(view, "SetupModelPicker"), "一开始还没有清单");
                Click(Find<Button>(view, "SetupPrimaryButton")); // 保存已有接入会自动拉模型,不强制刷新外网规格库
                await endpoint.Requested.WaitAsync(TimeSpan.FromSeconds(10));
                TextBox name = Find<TextBox>(view, "SetupNameBox");
                TextBox model = Find<TextBox>(view, "SetupModelBox");
                TextBox key = Find<TextBox>(view, "SetupKeyBox");
                name.Text = "unsaved-name";
                model.Text = "unsaved-model";
                key.Text = "sk-unsaved";
                endpoint.Release();
                await pulled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await PumpAsync(2);

                Assert.Contains("old-model", provider.AvailableModels);
                Assert.AreSame(name, Find<TextBox>(view, "SetupNameBox"));
                Assert.AreEqual("unsaved-name", name.Text);
                Assert.AreEqual("unsaved-model", model.Text);
                Assert.AreEqual("sk-unsaved", key.Text);
                Assert.AreNotEqual("unsaved-name", provider.Name, "未保存的表单不能被拉取落库");
                ComboBox picker = Find<ComboBox>(view, "SetupModelPicker");
                Assert.IsTrue(IsShown(picker));
                picker.SelectedItem = "old-model";
                Assert.AreEqual("old-model", model.Text, "新型号可从刚刷新的候选中选中");
            }
            finally
            {
                endpoint.Release();
                await pulled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await WaitForEnabledAsync(Find<Button>(view, "SetupPrimaryButton"));
                window.Close();
            }
        });
    }

    [TestMethod]
    public void OldSaveCompletion_CannotRevealAnotherOrEnableSecondaryOnANewerDraft()
    {
        OnUi(async () =>
        {
            using var endpoint = new HeldModelsEndpoint();
            var storage = new KeySaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            EnsureFreshModelCache(context);
            AiProvider old = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            old.BaseUrl = endpoint.BaseUrl + "/v1";
            AiProvider current = ProviderCatalog.Find("custom-anthropic")!.CreateProvider();
            AiSettings seed = new() { Providers = [old, current] };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(seed);
            await store.SetApiKeyAsync(old.Id, "sk-old");
            await store.SetApiKeyAsync(current.Id, "sk-current");
            var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var view = new ProviderSetupView(context, store, seed, new Loc("en"), "custom-openai");
            view.ProviderChanged += id => { if (id == old.Id) saved.TrySetResult(); };
            var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.ModelsChanged += () => pulled.TrySetResult();
            var window = new Window { Width = 720, Height = 720, Content = view };
            window.Show();
            try
            {
                await PumpAsync(40);
                storage.Hold = true;
                Click(Find<Button>(view, "SetupPrimaryButton"));
                await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                view.FocusEntry("custom-anthropic");
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                TextBox draft = Find<TextBox>(view, "SetupKeyBox");
                draft.Text = "new-unsaved-draft";
                Button another = Find<Button>(view, "SetupAddAnotherButton");
                Button secondary = Find<Button>(view, "SetupSecondaryButton");
                Assert.IsFalse(another.IsVisible);
                Assert.AreEqual("Cancel", secondary.Content);

                storage.Release.TrySetResult();
                await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await PumpAsync(2);
                Assert.AreSame(draft, Find<TextBox>(view, "SetupKeyBox"));
                Assert.AreEqual("new-unsaved-draft", draft.Text);
                Assert.IsFalse(another.IsVisible, "旧保存不能暴露新建态的再添加按钮");
                Assert.AreEqual("Cancel", secondary.Content);
                Assert.IsTrue(secondary.IsEnabled);
                Assert.HasCount(2, seed.Providers, "新实例仍只是草稿");
            }
            finally
            {
                storage.Release.TrySetResult();
                endpoint.Release();
                await pulled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                window.Close();
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SharedSettingsRemovingDisplayedProvider_CannotRetargetOldRemoveOrSave(bool save)
    {
        OnUi(async () =>
        {
            await using var endpoint = new SseStub("");
            using var context = new TestPluginContext();
            AiProvider survivor = ProviderCatalog.Find("custom-anthropic")!.CreateProvider();
            survivor.Name = "survivor";
            survivor.BaseUrl = endpoint.BaseUrl + "/v1";
            AiProvider displayed = ProviderCatalog.Find("custom-anthropic")!.CreateProvider();
            displayed.Name = "displayed-latest";
            displayed.BaseUrl = survivor.BaseUrl;
            var settings = new AiSettings { Providers = [survivor, displayed] };
            (Window window, ProviderSetupView view, _, AiSettingsStore store) = await ShowAsync(context, settings);
            try
            {
                await store.SetApiKeyAsync(survivor.Id, "sk-survivor");
                await store.SetApiKeyAsync(survivor.Models[0].Id, "sk-survivor-model");
                await store.SetApiKeyAsync(displayed.Id, "sk-displayed");
                await view.RefreshStatusAsync();
                view.FocusEntry("custom-anthropic", displayed.Id);
                Assert.AreEqual("displayed-latest", Find<TextBox>(view, "SetupNameBox").Text);
                Find<TextBox>(view, "SetupNameBox").Text = "stale-form";
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-stale";
                // 与设置页共享同一个 settings;外部移除后不重建目录当前的表单。
                settings.Providers.Remove(displayed);
                await store.DeleteApiKeyAsync(displayed.Id);
                await store.SaveAsync(settings);
                Click(Find<Button>(view, save ? "SetupPrimaryButton" : "SetupSecondaryButton"));
                await PumpAsync();
                Assert.HasCount(1, settings.Providers);
                Assert.AreSame(survivor, settings.Providers[0]);
                Assert.AreEqual("survivor", survivor.Name);
                Assert.AreEqual("sk-survivor", await store.GetApiKeyAsync(survivor.Id));
                Assert.AreEqual("sk-survivor-model", await store.GetApiKeyAsync(survivor.Models[0].Id));
                Assert.AreEqual(new Loc("en")["SetupSourceRemoved"],
                    Find<TextBlock>(view, "SetupProgressText").Text);
                Assert.IsEmpty(endpoint.Requests, "失效的旧表单不能保存或拉取幸存实例");

                await ClickRowAsync(window, view, "custom-anthropic"); // 收起失效表单
                await ClickRowAsync(window, view, "custom-anthropic"); // 新展开默认幸存实例
                Assert.AreEqual("survivor", Find<TextBox>(view, "SetupNameBox").Text);
                Find<TextBox>(view, "SetupNameBox").Text = "survivor-edited";
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-survivor-edited";
                var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                view.ProviderChanged += id => saved.TrySetResult(id);
                await ClickPrimaryAsync(view);
                Assert.AreEqual(survivor.Id, await saved.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.HasCount(1, settings.Providers);
                Assert.AreEqual("survivor-edited", survivor.Name);
                Assert.AreEqual("sk-survivor-edited", await store.GetApiKeyAsync(survivor.Id));
                Assert.AreEqual("sk-survivor-model", await store.GetApiKeyAsync(survivor.Models[0].Id));
                Assert.AreEqual("survivor-edited", (await store.LoadAsync()).Providers.Single().Name);
            }
            finally { endpoint.Release(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AddAnother_AfterAnotherWindowDeletesDisplayedProvider_PreservesRecoverableForm(bool hasSurvivor)
    {
        OnUi(async () =>
        {
            await using var endpoint = new SseStub("");
            using var context = new TestPluginContext();
            AiProvider survivor = ProviderCatalog.Find("custom-anthropic")!.CreateProvider();
            survivor.Name = "survivor";
            survivor.BaseUrl = endpoint.BaseUrl + "/v1";
            AiProvider displayed = ProviderCatalog.Find("custom-anthropic")!.CreateProvider();
            displayed.Name = "displayed-latest";
            displayed.BaseUrl = survivor.BaseUrl;
            var settings = new AiSettings
            {
                Providers = hasSurvivor ? [survivor, displayed] : [displayed],
                ActiveModelId = displayed.Models[0].Id
            };
            (Window window, ProviderSetupView view, _, AiSettingsStore store) = await ShowAsync(context, settings);
            var editor = new SettingsView(context, store, settings, new Loc("en"), () => { });
            var editorWindow = new Window { Width = 900, Height = 700, Content = editor };
            editorWindow.Show();
            try
            {
                if (hasSurvivor)
                {
                    await store.SetApiKeyAsync(survivor.Id, "sk-survivor");
                    await store.SetApiKeyAsync(survivor.Models[0].Id, "sk-survivor-model");
                }
                await store.SetApiKeyAsync(displayed.Id, "sk-displayed");
                await view.RefreshStatusAsync();
                view.FocusEntry("custom-anthropic", displayed.Id);
                TextBox oldName = Find<TextBox>(view, "SetupNameBox");
                Assert.AreEqual("displayed-latest", oldName.Text);
                oldName.Text = "removed-unsaved";
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-stale";
                Button primary = Find<Button>(view, "SetupPrimaryButton");
                Button secondary = Find<Button>(view, "SetupSecondaryButton");
                ListBox nav = editor.GetControl<ListBox>("ProvidersList");
                nav.SelectedItem = ((IEnumerable<ProviderNavItem>)nav.ItemsSource!).First(item =>
                    item.Provider.Id == displayed.Id && item.Model is null);
                await PumpAsync(5);
                Click(editor.GetControl<Button>("DeleteButton"));
                Click(editor.GetControl<Button>("DeleteButton"));
                await PumpAsync(40);
                Assert.HasCount(hasSurvivor ? 1 : 0, settings.Providers);

                Click(Find<Button>(view, "SetupAddAnotherButton"));
                await PumpAsync();
                Assert.AreSame(oldName, Find<TextBox>(view, "SetupNameBox"), "失效来源不能被重建成无法提交且没有取消的草稿");
                Assert.AreEqual("removed-unsaved", oldName.Text, "保留旧表单,让用户通过折叠展开恢复");
                Assert.AreSame(primary, Find<Button>(view, "SetupPrimaryButton"));
                Assert.AreSame(secondary, Find<Button>(view, "SetupSecondaryButton"));
                Assert.IsTrue(IsShown(secondary));
                Assert.AreEqual("Remove", secondary.Content);
                Assert.AreEqual(new Loc("en")["SetupSourceRemoved"], Find<TextBlock>(view, "SetupProgressText").Text);
                Click(primary);
                Click(secondary);
                await PumpAsync();
                Assert.HasCount(hasSurvivor ? 1 : 0, settings.Providers);
                Assert.IsEmpty(endpoint.Requests, "失效来源不能保存、拉取模型或重定向幸存接入");
                if (hasSurvivor)
                {
                    Assert.AreSame(survivor, settings.Providers.Single());
                    Assert.AreEqual("survivor", survivor.Name);
                    Assert.AreEqual("sk-survivor", await store.GetApiKeyAsync(survivor.Id));
                    Assert.AreEqual("sk-survivor-model", await store.GetApiKeyAsync(survivor.Models[0].Id));
                }

                await ClickRowAsync(window, view, "custom-anthropic");
                Assert.IsNull(FindOrNull<Button>(view, "SetupPrimaryButton"));
                await ClickRowAsync(window, view, "custom-anthropic");
                Assert.AreEqual("", Find<TextBlock>(view, "SetupProgressText").Text ?? "");
                if (hasSurvivor) Assert.AreEqual("survivor", Find<TextBox>(view, "SetupNameBox").Text);
                Find<TextBox>(view, "SetupNameBox").Text = "recovered";
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-recovered";
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                Find<TextBox>(view, "SetupBaseUrlBox").Text = endpoint.BaseUrl + "/v1";
                var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                view.ProviderChanged += id => saved.TrySetResult(id);
                await ClickPrimaryAsync(view);
                string savedId = await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
                AiProvider recovered = settings.Providers.Single();
                Assert.AreEqual(recovered.Id, savedId);
                Assert.AreNotEqual(displayed.Id, recovered.Id);
                if (hasSurvivor) Assert.AreSame(survivor, recovered);
                Assert.AreEqual("recovered", recovered.Name);
                Assert.AreEqual("sk-recovered", await store.GetApiKeyAsync(recovered.Id));
                Assert.AreEqual("recovered", (await store.LoadAsync()).Providers.Single().Name);
            }
            finally { editorWindow.Close(); endpoint.Release(); window.Close(); }
        });
    }

    [TestMethod]
    public void RepeatedRemoveWhileFirstDeleteWaits_CannotDeleteTheRemainingInstance()
    {
        OnUi(async () =>
        {
            var storage = new KeySaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            AiProvider survivor = ProviderCatalog.Find("custom-anthropic")!.CreateProvider();
            AiProvider removed = ProviderCatalog.Find("custom-anthropic")!.CreateProvider();
            var settings = new AiSettings { Providers = [survivor, removed] };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            storage.Hold = true;
            var view = new ProviderSetupView(context, store, settings, new Loc("en"), "custom-anthropic");
            var window = new Window { Width = 720, Height = 720, Content = view };
            window.Show();
            try
            {
                await PumpAsync(40);
                Button remove = Find<Button>(view, "SetupSecondaryButton");
                Click(remove);
                await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsFalse(remove.IsEnabled, "删除等待落盘时原按钮已禁用");
                Click(remove); // 第二个 Click 即便被排进事件队列也不得落在幸存者身上
                Assert.HasCount(2, settings.Providers, "等待真实存储时不能提前发布删除");
                Assert.AreSame(removed, settings.Providers[1]);
                storage.Release.TrySetResult();
                await PumpAsync(40);
                Assert.HasCount(1, settings.Providers);
                Assert.AreEqual(survivor.Id, (await store.LoadAsync()).Providers[0].Id);
                Assert.AreNotSame(remove, Find<Button>(view, "SetupSecondaryButton"));
            }
            finally { storage.Release.TrySetResult(); window.Close(); }
        });
    }

    [TestMethod]
    public void ProviderApiKey_DeletedDraftTargetCannotBindToSurvivingProvider()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider survivor = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            AiProvider source = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            source.BaseUrl = survivor.BaseUrl = "https://original.example/v1";
            await context.Secrets.SetAsync($"apikey:{source.Id}", "sk-source");
            var settings = new AiSettings { Providers = [survivor, source] };
            (Window window, ProviderSetupView view, _, AiSettingsStore store) =
                await ShowAsync(context, settings, focus: "custom-openai");
            try
            {
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-draft";
                settings.Providers.Remove(source);
                await ClickPrimaryAsync(view);
                Assert.HasCount(1, settings.Providers);
                Assert.IsEmpty(survivor.AdditionalApiKeyIds);
                Assert.IsNull(await store.GetApiKeyAsync(survivor.Id));
                Assert.AreEqual(new Loc("en")["SetupSourceRemoved"], Find<TextBlock>(view, "SetupProgressText").Text);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void LateModelPull_ReopenedInstance_RefreshesChoicesWithoutReplacingDraft()
    {
        OnUi(async () =>
        {
            using var endpoint = new HeldModelsEndpoint();
            await using var savedEndpoint = new SseStub(KeySaveProbeSse);
            using var context = new TestPluginContext();
            AiProvider source = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            source.BaseUrl = endpoint.BaseUrl + "/v1";
            (Window window, ProviderSetupView view, _, _) =
                await ShowAsync(context, new AiSettings { Providers = [source] }, focus: "custom-openai");
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.ModelsChanged += () => arrived.TrySetResult();
            Task saving = Task.CompletedTask;
            try
            {
                BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                object row = ((System.Collections.IEnumerable)typeof(ProviderSetupView).GetField("_cards", flags)!.GetValue(view)!)
                    .Cast<object>().Single(card => ((ProviderCatalogEntry)card.GetType().GetProperty("Entry")!.GetValue(card)!).Id == "custom-openai");
                saving = (Task)typeof(ProviderSetupView).GetMethod("PrimaryAsync", flags)!.Invoke(view, [row])!;
                await Task.WhenAny(endpoint.Requested, saving).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsTrue(endpoint.Requested.IsCompleted, Find<TextBlock>(view, "SetupProgressText").Text);
                view.FocusEntry("custom-openai");
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                TextBox model = Find<TextBox>(view, "SetupModelBox");
                TextBox name = Find<TextBox>(view, "SetupNameBox");
                model.Text = "my-unsaved-model";
                name.Text = "my-unsaved-name";
                endpoint.Release();
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await saving.WaitAsync(TimeSpan.FromSeconds(10));
                await PumpAsync(2);
                ComboBox choices = Find<ComboBox>(view, "SetupModelPicker");
                Assert.IsTrue(IsShown(choices), "当前表单必须看到晚到的候选");
                Assert.Contains("old-model", choices.Items.Cast<string>());
                Assert.AreEqual("my-unsaved-model", model.Text);
                Assert.AreEqual("my-unsaved-name", name.Text);
                Assert.AreSame(model, Find<TextBox>(view, "SetupModelBox"));
                Find<TextBox>(view, "SetupBaseUrlBox").Text = savedEndpoint.BaseUrl + "/v1";
                await ClickPrimaryAsync(view);
                Assert.AreEqual("my-unsaved-name", source.Name);
                Assert.AreEqual("my-unsaved-model", source.Models[0].Model);
            }
            finally
            {
                endpoint.Release();
                await saving.WaitAsync(TimeSpan.FromSeconds(40));
                window.Close();
            }
        });
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProviderApiKey_CollapsingOrCancellingDiscardsDraft(bool cancel)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider source = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            source.BaseUrl = "https://source.invalid/v1";
            await context.Secrets.SetAsync($"apikey:{source.Id}", "sk-source");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [source] }, focus: "custom-openai");
            try
            {
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-abandoned";
                if (cancel) Click(Find<Button>(view, "SetupSecondaryButton"));
                else await ClickRowAsync(window, view, "custom-openai");
                Assert.HasCount(1, settings.Providers);
                Assert.IsEmpty(source.AdditionalApiKeyIds);
                Assert.AreEqual("sk-source", await store.GetApiKeyAsync(source.Id));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoveAwaitingPersistence_BlocksSaveAndCloneWithoutUnlockingNewForm(bool reopen)
    {
        OnUi(async () =>
        {
            var storage = new KeySaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            AiProvider survivor = ProviderCatalog.Find("custom-anthropic")!.CreateProvider();
            survivor.Name = "survivor";
            AiProvider removed = ProviderCatalog.Find("custom-anthropic")!.CreateProvider();
            var settings = new AiSettings { Providers = [survivor, removed] };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            await store.SetApiKeyAsync(survivor.Id, "sk-survivor");
            await store.SetApiKeyAsync(removed.Id, "sk-removed");
            storage.Hold = true;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var view = new ProviderSetupView(context, store, settings, new Loc("en"), "custom-anthropic");
            view.ProviderChanged += _ => completed.TrySetResult();
            var window = new Window { Width = 720, Height = 720, Content = view };
            window.Show();
            try
            {
                await PumpAsync(40);
                Find<TextBox>(view, "SetupNameBox").Text = "removed-draft";
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-wrong";
                Button primary = Find<Button>(view, "SetupPrimaryButton");
                Button another = Find<Button>(view, "SetupAddAnotherButton");
                Click(Find<Button>(view, "SetupSecondaryButton"));
                await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsFalse(primary.IsEnabled, "移除原表单期间不能保存幸存者");
                Assert.IsFalse(another.IsEnabled, "移除原表单期间不能克隆幸存者");
                Click(primary);
                Click(another);
                // 即使控件被重新启用,重入的动作也不能把旧表单写到幸存者。
                primary.IsEnabled = another.IsEnabled = true;
                Click(primary);
                Click(another);
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                await ((Task)typeof(ProviderSetupView).GetMethod("AddOrSaveKeyAsync", flags)!
                    .Invoke(view, [ProviderCatalog.Find("custom-anthropic")!])!).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual("survivor", survivor.Name);
                Assert.AreEqual("sk-survivor", await store.GetApiKeyAsync(survivor.Id));
                Assert.AreSame(primary, Find<Button>(view, "SetupPrimaryButton"));
                if (reopen)
                {
                    view.FocusEntry("custom-anthropic");
                    primary = Find<Button>(view, "SetupPrimaryButton");
                    another = Find<Button>(view, "SetupAddAnotherButton");
                    primary.IsEnabled = another.IsEnabled = false;
                }
                storage.Release.TrySetResult();
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.HasCount(1, settings.Providers);
                Assert.AreSame(survivor, settings.Providers[0]);
                Assert.AreEqual("survivor", survivor.Name);
                Assert.AreEqual("sk-survivor", await store.GetApiKeyAsync(survivor.Id));
                Assert.IsNull(await store.GetApiKeyAsync(removed.Id));
                if (reopen)
                {
                    Assert.AreSame(primary, Find<Button>(view, "SetupPrimaryButton"));
                    Assert.IsFalse(primary.IsEnabled);
                    Assert.IsFalse(another.IsEnabled);
                }
                else
                {
                    Assert.AreNotSame(primary, Find<Button>(view, "SetupPrimaryButton"));
                }
            }
            finally { storage.Release.TrySetResult(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LateModels_ChangedFullAddressNeverShowsOldEndpointCandidates(bool sameOrigin)
    {
        OnUi(async () =>
        {
            using var endpoint = new HeldModelsEndpoint();
            using var context = new TestPluginContext();
            AiProvider source = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            source.BaseUrl = endpoint.BaseUrl + "/tenant-a";
            (Window window, ProviderSetupView view, _, _) =
                await ShowAsync(context, new AiSettings { Providers = [source] }, focus: "custom-openai");
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.ModelsChanged += () => arrived.TrySetResult();
            try
            {
                Click(Find<Button>(view, "SetupPrimaryButton"));
                await endpoint.Requested.WaitAsync(TimeSpan.FromSeconds(10));
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                TextBox address = Find<TextBox>(view, "SetupBaseUrlBox");
                address.Text = sameOrigin ? endpoint.BaseUrl + "/tenant-b" : "https://other.invalid/v1";
                TextBox model = Find<TextBox>(view, "SetupModelBox");
                model.Text = "unsaved-model";
                endpoint.Release();
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Contains("old-model", source.AvailableModels, "源端点仍正确落库");
                ComboBox? choices = FindOrNull<ComboBox>(view, "SetupModelPicker");
                Assert.IsTrue(choices is null || !IsShown(choices)
                    || !choices.Items.Cast<string>().Contains("old-model"), "新完整地址不能显示旧端点候选");
                Assert.AreSame(address, Find<TextBox>(view, "SetupBaseUrlBox"));
                Assert.AreEqual("unsaved-model", model.Text);
            }
            finally
            {
                endpoint.Release();
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                window.Close();
            }
        });
    }
    [TestMethod]
    [DataRow("secret")]
    [DataRow("json")]
    [DataRow("cancel")]
    [DataRow("conflict")]
    public void FirstApiKeyCreate_FailureNeverPublishesAndTheSameDraftCanRetry(string failure)
    {
        OnUi(async () =>
        {
            await using var endpoint = new SseStub(KeySaveProbeSse);
            using var secretSource = new TestPluginContext();
            var secrets = new KeySaveSecrets(secretSource.Secrets);
            var storage = new KeySaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Secrets = secrets, Storage = storage };
            AiProvider survivor = ProviderCatalog.Find("custom-anthropic")!.CreateProvider();
            AiSettings settings = new() { Providers = [survivor], ActiveModelId = survivor.Models[0].Id };
            (Window window, ProviderSetupView view, _, AiSettingsStore store) = await ShowAsync(context, settings, "custom-openai");
            int notifications = 0;
            view.ProviderChanged += _ => notifications++;
            try
            {
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                Find<TextBox>(view, "SetupNameBox").Text = "my retry provider";
                Find<TextBox>(view, "SetupBaseUrlBox").Text = endpoint.BaseUrl + "/v1";
                Find<TextBox>(view, "SetupModelBox").Text = "my-retry-model";
                TextBox key = Find<TextBox>(view, "SetupKeyBox");
                key.Text = "sk-new";
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                string draftId = ((AiProvider)typeof(ProviderSetupView).GetField("_newProviderDraft", flags)!.GetValue(view)!).Id;
                if (failure == "conflict")
                {
                    AiSettings external = await store.LoadAsync();
                    external.SystemPrompt = "external prompt";
                    await store.SaveAsync(external);
                }
                secrets.Fail = failure == "secret";
                storage.Failure = failure;
                await ClickPrimaryAsync(view);
                Assert.HasCount(1, settings.Providers);
                Assert.AreSame(survivor, settings.Providers.Single());
                Assert.AreEqual(survivor.Models[0].Id, settings.ActiveModelId);
                Assert.HasCount(1, (await store.LoadAsync()).Providers);
                Assert.IsNull(await secretSource.Secrets.GetAsync($"apikey:{draftId}"), "失败不得留下幽灵 Key");
                Assert.AreEqual(0, notifications);
                Assert.IsEmpty(endpoint.Requests, "未提交不能拉取模型或调用消费者");
                Assert.AreSame(key, Find<TextBox>(view, "SetupKeyBox"));
                Assert.AreEqual("sk-new", key.Text);
                Assert.AreEqual("my retry provider", Find<TextBox>(view, "SetupNameBox").Text);
                secrets.Fail = false;
                storage.Failure = null;
                await ClickPrimaryAsync(view);
                AiProvider created = settings.Providers.Single(provider => provider.Id == draftId);
                Assert.AreEqual("my retry provider", created.Name);
                Assert.AreEqual("my-retry-model", created.Models[0].Model);
                Assert.AreEqual(survivor.Models[0].Id, settings.ActiveModelId);
                Assert.AreEqual(1, notifications);
                if (failure == "conflict") Assert.AreEqual("external prompt", settings.SystemPrompt);
                System.Text.Json.JsonElement savedJson = await context.Storage.GetAsync<System.Text.Json.JsonElement>("settings");
                Assert.IsFalse(savedJson.GetRawText().Contains("sk-new", StringComparison.Ordinal), "API Key不得进入JSON");
                (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(created, created.Models[0]));
                Assert.IsNull(error, error?.ToString());
                Assert.IsTrue(endpoint.Authorizations.All(header => header == "Bearer sk-new"));
                Assert.Contains(body => body.Contains("\"model\":\"my-retry-model\"", StringComparison.Ordinal), endpoint.Requests);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("shared")]
    [DataRow("independent")]
    [DataRow("replacement")]
    public void StaleProviderForm_ReloadsTheOriginalSettingsThenExplicitRetryUsesLatestHiddenFields(string mode)
    {
        OnUi(async () =>
        {
            await using var endpoint = new SseStub(KeySaveProbeSse);
            using var context = new TestPluginContext();
            AiProvider opened = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            opened.Name = "opened-name";
            opened.BaseUrl = endpoint.BaseUrl + "/v1";
            opened.Models[0].Model = "opened-model";
            await context.Secrets.SetAsync($"apikey:{opened.Id}", "sk-original");
            AiSettings settings = new() { Providers = [opened], ActiveModelId = opened.Models[0].Id };
            (Window window, ProviderSetupView view, _, AiSettingsStore store) = await ShowAsync(context, settings, "custom-openai");
            int notifications = 0;
            view.ProviderChanged += _ => notifications++;
            try
            {
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                TextBox name = Find<TextBox>(view, "SetupNameBox");
                TextBox model = Find<TextBox>(view, "SetupModelBox");
                name.Text = "my-draft-name";
                model.Text = "my-draft-model";
                AiSettings external = mode == "shared" ? settings : await store.LoadAsync();
                AiProvider latest = external.Providers.Single();
                latest.Name = "external-name";
                latest.Models[0].Model = "external-model";
                latest.Models[0].MaxTokens = 4321;
                latest.Models[0].SystemPrompt = "external model prompt";
                external.SystemPrompt = "external global prompt";
                await store.SaveAsync(external);
                if (mode == "replacement")
                {
                    settings.Providers[0] = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(
                        System.Text.Json.JsonSerializer.Serialize(opened))!;
                }

                AiProvider current = settings.Providers.Single();
                AiModelConfig currentModel = current.Models[0];
                await ClickPrimaryAsync(view);
                Assert.AreSame(current, settings.Providers.Single());
                Assert.AreSame(currentModel, settings.Providers.Single().Models[0]);
                Assert.AreEqual("external-name", current.Name);
                Assert.AreEqual("external-model", currentModel.Model);
                Assert.AreEqual("external global prompt", settings.SystemPrompt);
                Assert.AreSame(name, Find<TextBox>(view, "SetupNameBox"));
                Assert.AreSame(model, Find<TextBox>(view, "SetupModelBox"));
                Assert.AreEqual("my-draft-name", name.Text);
                Assert.AreEqual("my-draft-model", model.Text);
                TextBlock conflict = Find<TextBlock>(view, "SetupConflictValues");
                Assert.IsTrue(IsShown(conflict));
                Assert.Contains("external-name", conflict.Text!);
                Assert.Contains("external-model", conflict.Text!);
                Assert.AreEqual(0, notifications);
                Assert.IsEmpty(endpoint.Requests);
                await ClickPrimaryAsync(view);
                Assert.AreEqual("my-draft-name", current.Name);
                Assert.AreEqual("my-draft-model", currentModel.Model);
                Assert.AreEqual(4321, currentModel.MaxTokens);
                Assert.AreEqual("external model prompt", currentModel.SystemPrompt);
                Assert.AreEqual("external global prompt", settings.SystemPrompt);
                Assert.AreEqual(1, notifications);
                AiProvider saved = (await store.LoadAsync()).Providers.Single();
                Assert.AreEqual("my-draft-model", saved.Models[0].Model);
                Assert.AreEqual(4321, saved.Models[0].MaxTokens);
                (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(current, currentModel));
                Assert.IsNull(error, error?.ToString());
                Assert.AreEqual("Bearer sk-original", endpoint.Authorizations[^1]);
                Assert.Contains("\"model\":\"my-draft-model\"", endpoint.Requests[^1]);
                if (mode == "replacement")
                {
                    Assert.AreEqual("opened-model", opened.Models[0].Model,
                    "旧引用不能偷偷重新绑定到另一份live实例");
                }
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("model")]
    [DataRow("matching-model")]
    [DataRow("protocol")]
    public void LateCatalogue_OnlyAbsorbsItsOwnChangesAndStillRejectsAnExternalModelEdit(string field)
    {
        OnUi(async () =>
        {
            using var catalogue = new HeldModelsEndpoint();
            await using var endpoint = new SseStub(KeySaveProbeSse);
            using var context = new TestPluginContext();
            AiProvider source = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            source.BaseUrl = catalogue.BaseUrl + "/v1";
            source.Models[0].Model = "opened-model";
            await context.Secrets.SetAsync($"apikey:{source.Id}", "sk-original");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [source] }, "custom-openai");
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.ModelsChanged += () => arrived.TrySetResult();
            int notifications = 0;
            view.ProviderChanged += _ => notifications++;
            Button originalPrimary = Find<Button>(view, "SetupPrimaryButton");
            Task? saving = null;
            try
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                object row = ((System.Collections.IEnumerable)typeof(ProviderSetupView)
                    .GetField("_cards", flags)!.GetValue(view)!).Cast<object>().Single(item =>
                        ((ProviderCatalogEntry)item.GetType().GetProperty("Entry")!.GetValue(item)!).Id == "custom-openai");
                saving = (Task)typeof(ProviderSetupView).GetMethod("PrimaryAsync", flags)!.Invoke(view, [row])!;
                Assert.IsFalse(originalPrimary.IsEnabled);
                await catalogue.Requested.WaitAsync(TimeSpan.FromSeconds(10));
                view.FocusEntry("custom-openai");
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                TextBox name = Find<TextBox>(view, "SetupNameBox");
                TextBox model = Find<TextBox>(view, "SetupModelBox");
                name.Text = "my-draft-name";
                model.Text = "my-draft-model";
                string externalModel = field == "matching-model" ? "old-model" : "external-model";
                if (field != "protocol") source.Models[0].Model = externalModel;
                else source.Models[0].Protocol = ChatProtocol.AnthropicMessages;
                catalogue.Release();
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await saving.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual("my-draft-name", name.Text);
                Assert.AreEqual("my-draft-model", model.Text);
                Assert.Contains("old-model", source.AvailableModels);
                if (field == "matching-model")
                {
                    Assert.HasCount(1, source.Models.Where(item => item.Model == "old-model").ToArray(),
                    "等待目录期间已添加或改名为该型号,不能再生成同型号配置");
                }

                if (field != "protocol") Assert.AreEqual(externalModel, source.Models[0].Model);
                else Assert.AreEqual(ChatProtocol.AnthropicMessages, source.Models[0].Protocol);
                Find<TextBox>(view, "SetupBaseUrlBox").Text = endpoint.BaseUrl + "/v1";
                await ClickPrimaryAsync(view);
                Assert.AreEqual(1, notifications, "晚到目录不能把外部模型修改当成可覆盖基线");
                Assert.IsEmpty(endpoint.Requests);
                Assert.AreEqual(catalogue.BaseUrl + "/v1", source.BaseUrl);
                Assert.AreEqual("my-draft-model", model.Text);
                Assert.AreEqual(new Loc("en")["SetupConfigChanged"], Find<TextBlock>(view, "SetupProgressText").Text);
                if (field == "protocol")
                {
                    ComboBox protocol = Find<ComboBox>(view, "SetupProtocolCombo");
                    Assert.AreEqual((int)ChatProtocol.AnthropicMessages, protocol.SelectedIndex,
                        "未编辑协议先跟随外部生效值");
                    protocol.SelectedIndex = (int)ChatProtocol.OpenAiChatCompletions;
                }
                // 换到了另一台主机:旧 Key 不跟过去,为新主机填一把(只有这一把要换,不用二次确认)
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-for-new-host";
                await ClickPrimaryAsync(view);
                Assert.AreEqual(2, notifications);
                Assert.AreEqual("my-draft-name", source.Name);
                Assert.AreEqual("my-draft-model", source.Models[0].Model);
                Assert.AreEqual(ChatProtocol.OpenAiChatCompletions, new ResolvedModel(source, source.Models[0]).Protocol);
                Assert.AreSame(source, settings.Providers.Single());
                (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(source, source.Models[0]));
                Assert.IsNull(error, error?.ToString());
                Assert.AreEqual("Bearer sk-for-new-host", endpoint.Authorizations[^1]);
                Assert.DoesNotContain("Bearer sk-original", endpoint.Authorizations);
                Assert.Contains("\"model\":\"my-draft-model\"", endpoint.Requests[^1]);
            }
            finally
            {
                catalogue.Release();
                try
                {
                    if (saving is not null) await saving.WaitAsync(TimeSpan.FromSeconds(40));
                }
                finally { window.Close(); }
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConflictRecovery_UntouchedProviderFieldsAndKeyFollowExternalBeforeSecondSave(bool independent)
    {
        OnUi(async () =>
        {
            await using var original = new SseStub(KeySaveProbeSse);
            await using var latest = new SseStub(KeySaveProbeSse);
            using var context = new TestPluginContext();
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.Name = "opened-name";
            provider.BaseUrl = original.BaseUrl + "/v1";
            provider.Models[0].Model = "opened-model";
            provider.Models[0].Protocol = ChatProtocol.AnthropicMessages;
            await context.Secrets.SetAsync($"apikey:{provider.Id}", "sk-original");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider] }, "custom-openai");
            try
            {
                AiModelConfig model = provider.Models[0];
                AiSettings external = independent ? await store.LoadAsync() : settings;
                AiProvider changed = external.Providers.Single();
                changed.Name = "latest-name";
                changed.BaseUrl = latest.BaseUrl + "/v1";
                changed.Models[0].Model = "latest-model";
                changed.Models[0].Protocol = null;
                changed.Models[0].MaxTokens = 3456;
                external.SystemPrompt = "latest prompt";
                await store.SaveAsync(external);
                await store.SetApiKeyAsync(provider.Id, "sk-latest");
                await ClickPrimaryAsync(view);
                Assert.AreSame(provider, settings.Providers.Single());
                Assert.AreSame(model, provider.Models[0]);
                Assert.AreEqual("latest-name", Find<TextBox>(view, "SetupNameBox").Text);
                Assert.AreEqual(latest.BaseUrl + "/v1", Find<TextBox>(view, "SetupBaseUrlBox").Text);
                Assert.AreEqual("latest-model", Find<TextBox>(view, "SetupModelBox").Text);
                Assert.AreEqual((int)ChatProtocol.OpenAiChatCompletions, Find<ComboBox>(view, "SetupProtocolCombo").SelectedIndex);
                Assert.AreEqual("sk-latest", Find<TextBox>(view, "SetupKeyBox").Text);
                Assert.IsEmpty(original.Requests);
                Assert.IsEmpty(latest.Requests, "首次冲突只恢复,不自动提交或调用模型");
                await ClickPrimaryAsync(view);
                AiProvider saved = (await store.LoadAsync()).Providers.Single();
                Assert.AreEqual("latest-name", saved.Name);
                Assert.AreEqual(latest.BaseUrl + "/v1", saved.BaseUrl);
                Assert.AreEqual("latest-model", saved.Models[0].Model);
                Assert.IsNull(saved.Models[0].Protocol);
                Assert.AreEqual(3456, saved.Models[0].MaxTokens);
                Assert.AreEqual("latest prompt", settings.SystemPrompt);
                Assert.AreEqual("sk-latest", await store.GetApiKeyAsync(provider.Id));
                (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(provider, model));
                Assert.IsNull(error, error?.ToString());
                Assert.IsEmpty(original.Requests);
                Assert.AreEqual("Bearer sk-latest", latest.Authorizations[^1]);
                Assert.Contains("\"model\":\"latest-model\"", latest.Requests[^1]);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ConflictRecovery_AfterHeldCommitUsesSubmittedInputBaselineAndKeepsTheLaterKeyDraft()
    {
        OnUi(async () =>
        {
            await using var endpoint = new SseStub(KeySaveProbeSse);
            using var secretSource = new TestPluginContext();
            var secrets = new KeySaveSecrets(secretSource.Secrets) { Hold = true };
            using var context = new TestPluginContext { Secrets = secrets };
            AiProvider provider = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            provider.Name = "opened-name";
            provider.BaseUrl = endpoint.BaseUrl + "/v1";
            provider.Models[0].Model = "my-model";
            await secrets.SetAsync($"apikey:{provider.Id}", "sk-original");
            (Window window, ProviderSetupView view, _, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider] }, "custom-openai");
            Button primary = Find<Button>(view, "SetupPrimaryButton");
            try
            {
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                await PumpAsync(2);
                TextBox name = Find<TextBox>(view, "SetupNameBox");
                TextBox key = Find<TextBox>(view, "SetupKeyBox");
                name.Text = "  saved-name  ";
                key.Text = "sk-new";
                Click(primary);
                await secrets.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                key.Text = "sk-later-draft";
                secrets.Released.TrySetResult();
                await WaitForEnabledAsync(primary);
                Assert.AreEqual("saved-name", provider.Name);
                Assert.AreEqual("sk-new", await store.GetApiKeyAsync(provider.Id));
                Assert.AreEqual("sk-later-draft", key.Text);
                AiSettings external = await store.LoadAsync();
                external.Providers.Single().Name = "external-name";
                await store.SaveAsync(external);
                int requests = endpoint.Requests.Count;
                await ClickPrimaryAsync(view);
                Assert.HasCount(requests, endpoint.Requests, "冲突不能自动提交或继续模型请求");
                Assert.AreEqual("external-name", name.Text,
                    "上次已保存且未继续编辑的原始输入即使含空格也必须跟随外部值");
                Assert.AreEqual("sk-later-draft", key.Text);
                Assert.AreEqual("sk-new", await store.GetApiKeyAsync(provider.Id));
                await ClickPrimaryAsync(view);
                Assert.AreEqual("external-name", provider.Name);
                Assert.AreEqual("sk-later-draft", await store.GetApiKeyAsync(provider.Id));
                (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(provider, provider.Models[0]));
                Assert.IsNull(error, error?.ToString());
                Assert.AreEqual("Bearer sk-later-draft", endpoint.Authorizations[^1]);
                Assert.Contains("\"model\":\"my-model\"", endpoint.Requests[^1]);
            }
            finally
            {
                secrets.Released.TrySetResult();
                try { await WaitForEnabledAsync(primary); }
                finally { window.Close(); }
            }
        });
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("missing-catalog")]
    [DataRow("custom-oauth")]
    [DataRow("openai")]
    public void InvalidCatalogFocus_PreservesTheDisplayedInstanceAndItsRemovalTarget(string? catalogId)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider first = ProviderCatalog.Find("custom-anthropic")!.CreateProvider();
            AiProvider latest = ProviderCatalog.Find("custom-anthropic")!.CreateProvider();
            first.Name = "first";
            latest.Name = "latest";
            await context.Secrets.SetAsync($"apikey:{first.Id}", "sk-first");
            await context.Secrets.SetAsync($"apikey:{latest.Id}", "sk-latest");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [first, latest] });
            try
            {
                view.FocusEntry("custom-anthropic", first.Id);
                await PumpAsync();
                TextBox name = Find<TextBox>(view, "SetupNameBox");
                name.Text = "first-unsaved";
                Button secondary = Find<Button>(view, "SetupSecondaryButton");
                view.FocusEntry(catalogId, latest.Id);
                Assert.AreSame(name, Find<TextBox>(view, "SetupNameBox"));
                Assert.AreSame(secondary, Find<Button>(view, "SetupSecondaryButton"));
                Assert.AreEqual("first-unsaved", name.Text);
                Click(secondary);
                await WaitForEnabledAsync(secondary);
                Assert.AreSame(latest, settings.Providers.Single());
                Assert.IsNull(await store.GetApiKeyAsync(first.Id));
                Assert.AreEqual("sk-latest", await store.GetApiKeyAsync(latest.Id));
                Assert.AreEqual(latest.Id, (await store.LoadAsync()).Providers.Single().Id);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AddKeyHeldInRealStorage_PreservesLaterInputAndFixedOwnerWithoutRepeatingSideEffects(bool cancelLater)
    {
        OnUi(async () =>
        {
            await using var endpoint = new SseStub("");
            var storage = new KeySaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            AiProvider displayed = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            AiProvider latest = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            displayed.BaseUrl = latest.BaseUrl = endpoint.BaseUrl + "/v1";
            await context.Secrets.SetAsync($"apikey:{displayed.Id}", "sk-original");
            await context.Secrets.SetAsync($"apikey:{latest.Id}", "sk-latest");
            (Window window, ProviderSetupView view, _, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [displayed, latest] });
            try
            {
                view.FocusEntry("custom-openai", displayed.Id);
                await PumpAsync();
                int notifications = 0;
                view.ProviderChanged += id => { Assert.AreEqual(displayed.Id, id); notifications++; };
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                TextBox input = Find<TextBox>(view, "SetupKeyBox");
                input.Text = "sk-click-snapshot";
                Button primary = Find<Button>(view, "SetupPrimaryButton");
                storage.Hold = true;
                Click(primary);
                await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsTrue(input.IsEnabled, "存储等待期间仍允许编辑下一把 Key");
                input.Text = "sk-later-draft";
                storage.Release.TrySetResult();
                await WaitForEnabledAsync(primary);
                Assert.AreSame(input, Find<TextBox>(view, "SetupKeyBox"));
                Assert.AreEqual("sk-later-draft", input.Text);
                Assert.AreEqual("sk-click-snapshot", await store.GetApiKeyAsync(displayed.AdditionalApiKeyIds.Single()));
                Assert.IsEmpty(latest.AdditionalApiKeyIds);
                Assert.AreEqual(1, notifications);
                Assert.AreEqual("Cancel", Find<Button>(view, "SetupSecondaryButton").Content);
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var cancellation = (CancellationTokenSource)typeof(ProviderSetupView)
                    .GetField("_keyDraftCancellation", flags)!.GetValue(view)!;
                Assert.IsFalse(cancellation.IsCancellationRequested);
                if (cancelLater) Click(Find<Button>(view, "SetupSecondaryButton"));
                else await ClickPrimaryAsync(view);
                Assert.HasCount(cancelLater ? 1 : 2, displayed.AdditionalApiKeyIds);
                Assert.AreEqual(cancelLater ? 1 : 2, notifications);
                if (!cancelLater)
                    Assert.AreEqual("sk-later-draft", await store.GetApiKeyAsync(displayed.AdditionalApiKeyIds[1]));
                Assert.IsNull(typeof(ProviderSetupView).GetField("_keyDraftCancellation", flags)!.GetValue(view));
                Assert.IsEmpty(endpoint.Requests, "仅添加或取消 Key 不应进行任何额外模型/付费请求");
                Assert.IsEmpty(latest.AdditionalApiKeyIds);
                Assert.HasCount(cancelLater ? 1 : 2, (await store.LoadAsync()).Providers[0].AdditionalApiKeyIds);
            }
            finally { storage.Release.TrySetResult(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void MainKeyHeldInRealStorage_UsesSubmittedInputBaselineAndRetainsLaterDraft(bool creating, bool editLater)
    {
        OnUi(async () =>
        {
            await using var endpoint = new SseStub("");
            var storage = new KeySaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            AiProvider seed = ProviderCatalog.Find("custom-openai")!.CreateProvider();
            seed.BaseUrl = endpoint.BaseUrl + "/v1";
            seed.Models[0].Model = "original-model";
            if (!creating) await context.Secrets.SetAsync($"apikey:{seed.Id}", "sk-original");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = creating ? [] : [seed] }, "custom-openai");
            try
            {
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                TextBox name = Find<TextBox>(view, "SetupNameBox");
                TextBox key = Find<TextBox>(view, "SetupKeyBox");
                Button primary = Find<Button>(view, "SetupPrimaryButton");
                name.Text = "  submitted-name  ";
                Find<TextBox>(view, "SetupBaseUrlBox").Text = endpoint.BaseUrl + "/v1";
                Find<TextBox>(view, "SetupModelBox").Text = "submitted-model";
                key.Text = "sk-submitted";
                storage.Hold = true;
                Click(primary);
                await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (editLater) { name.Text = "later-name"; key.Text = "sk-later"; }
                storage.Release.TrySetResult();
                await WaitForEnabledAsync(primary);
                AiProvider saved = settings.Providers.Single();
                Assert.AreEqual("submitted-name", saved.Name);
                Assert.AreEqual("sk-submitted", await store.GetApiKeyAsync(saved.Id));
                Assert.AreEqual(editLater ? "later-name" : "  submitted-name  ", name.Text);
                Assert.AreEqual(editLater ? "sk-later" : "sk-submitted", key.Text);
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                if (editLater)
                {
                    Assert.AreSame(key, Find<TextBox>(view, "SetupKeyBox"));
                    Assert.AreEqual(new Loc("en")["SetupSaveProviderFirst"], Find<TextBlock>(view, "SetupProgressText").Text);
                    await ClickPrimaryAsync(view);
                    Assert.AreEqual("later-name", saved.Name);
                    Assert.AreEqual("sk-later", await store.GetApiKeyAsync(saved.Id));
                    Click(Find<Button>(view, "SetupAddAnotherButton"));
                }
                Assert.AreNotSame(key, Find<TextBox>(view, "SetupKeyBox"), "已提交的原始空格输入不应被误判为新草稿");
                Assert.AreEqual("Cancel", Find<Button>(view, "SetupSecondaryButton").Content);
                Assert.HasCount(1, settings.Providers);
            }
            finally { storage.Release.TrySetResult(); window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("json")]
    [DataRow("cancel")]
    public void DeleteHeldInRealStorage_FailurePreservesSelectionDraftAndAllSecretsUntilRetry(string failure)
    {
        OnUi(async () =>
        {
            var storage = new KeySaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            AiProvider displayed = ProviderCatalog.Find("custom-anthropic")!.CreateProvider();
            AiProvider latest = ProviderCatalog.Find("custom-anthropic")!.CreateProvider();
            string additional = Guid.NewGuid().ToString("N");
            displayed.AdditionalApiKeyIds = [additional];
            await context.Secrets.SetAsync($"apikey:{displayed.Id}", "sk-original");
            await context.Secrets.SetAsync($"apikey:{additional}", "sk-additional");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [displayed, latest], ActiveModelId = displayed.Models[0].Id });
            try
            {
                view.FocusEntry("custom-anthropic", displayed.Id);
                await PumpAsync();
                TextBox name = Find<TextBox>(view, "SetupNameBox");
                TextBox key = Find<TextBox>(view, "SetupKeyBox");
                name.Text = "unsaved-name";
                key.Text = "sk-unsaved";
                int notifications = 0;
                view.ProviderChanged += _ => notifications++;
                Button secondary = Find<Button>(view, "SetupSecondaryButton");
                storage.Hold = true;
                Click(secondary);
                await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.HasCount(2, settings.Providers);
                storage.Failure = failure;
                storage.Release.TrySetResult();
                await WaitForEnabledAsync(secondary);
                Assert.HasCount(2, settings.Providers);
                Assert.AreSame(displayed, settings.Providers[0]);
                Assert.AreSame(name, Find<TextBox>(view, "SetupNameBox"));
                Assert.AreSame(key, Find<TextBox>(view, "SetupKeyBox"));
                Assert.AreEqual("unsaved-name", name.Text);
                Assert.AreEqual("sk-unsaved", key.Text);
                Assert.AreEqual(displayed.Models[0].Id, settings.ActiveModelId);
                Assert.AreEqual("sk-original", await store.GetApiKeyAsync(displayed.Id));
                Assert.AreEqual("sk-additional", await store.GetApiKeyAsync(additional));
                Assert.HasCount(2, (await store.LoadAsync()).Providers);
                Assert.AreEqual(0, notifications);
                storage.Failure = null;
                Click(secondary);
                await WaitForEnabledAsync(secondary);
                Assert.AreSame(latest, settings.Providers.Single());
                Assert.IsNull(await store.GetApiKeyAsync(displayed.Id));
                Assert.IsNull(await store.GetApiKeyAsync(additional));
                Assert.AreEqual(1, notifications);
            }
            finally { storage.Release.TrySetResult(); window.Close(); }
        });
    }
}
