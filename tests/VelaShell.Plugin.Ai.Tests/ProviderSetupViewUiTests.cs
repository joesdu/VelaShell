using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Secrets;
using VelaShell.PluginSdk.Storage;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 「连接供应商」那一页的 headless 装载。
/// </summary>
/// <remarks>
/// 这一页的交互约定就两条,两条都在这儿钉着:
/// <list type="number">
/// <item><b>点行只展开,绝不自动干事</b> —— 尤其不能一点就把浏览器弹出去。</item>
/// <item><b>展开后只问程序确实不知道的那几样</b> —— 其余收进「高级设置」。</item>
/// <item><b>同一家可加多把 Key</b> —— 纯 Key 草稿不复制地址、模型或机密;OAuth 第二账号仍为独立实例。</item>
/// </list>
/// 整页是代码搭的,没有 XAML 名字域,所以按控件的 <c>Name</c> 走逻辑树找。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class ProviderSetupViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

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
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
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
                long epoch = store.ProviderConfigurationVersion(source);
                storage.Hold = true;
                catalogue.Release();
                await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(providerBefore, System.Text.Json.JsonSerializer.Serialize(source), "等待 JSON 时不得发布新 GUID、来源或窗口");
                Assert.AreEqual(diskBefore, System.Text.Json.JsonSerializer.Serialize(await context.Storage.GetAsync<System.Text.Json.JsonElement>("settings")));
                Assert.AreEqual(formBaseline, typeof(ProviderSetupView).GetField("_providerFormBaseline", flags)!.GetValue(view));
                Assert.AreEqual(epoch, store.ProviderConfigurationVersion(source));
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
                    Assert.AreEqual(epoch + 1, store.ProviderConfigurationVersion(source));
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
                    Assert.AreEqual(epoch, store.ProviderConfigurationVersion(source));
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
            using var mcp = new SseStub("", hold: true);
            using var endpoint = new SseStub(KeySaveProbeSse);
            using var context = new TestPluginContext();
            EnsureFreshModelCache(context);
            var store = new AiSettingsStore(context);
            var provider = new AiProvider { BaseUrl = endpoint.BaseUrl + "/v1", Models = [new AiModelConfig { Model = "m", MaxInputTokens = 128000 }] };
            await store.SetApiKeyAsync(provider.Id, "sk-original");
            await store.SaveAsync(new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id,
                Mode = ChatMode.Agent, SuggestFollowUps = false,
                McpServers = [new McpServerConfig { Name = "held", Transport = McpTransportType.Http, Url = mcp.BaseUrl + "/mcp" }] });
            var panel = new ChatPanelView(context, store);
            var window = new Window { Width = 800, Height = 600, Content = panel };
            window.Show();
            try
            {
                await PumpAsync(40);
                panel.SendExternal("private-history-before-catalogue");
                await mcp.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(15));
                var live = (AiSettings)typeof(ChatPanelView).GetField("_settings",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
                AiProvider owner = live.Providers.Single();
                AiModelConfig model = owner.Models.Single();
                long epoch = store.ProviderConfigurationVersion(owner);
                string expected = System.Text.Json.JsonSerializer.Serialize(owner);
                AiProvider draft = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(expected)!;
                draft.Name = "catalogue display name";
                draft.AvailableModels = ["m", "candidate"];
                draft.Models[0].InputPricePerMillion = 3;
                if (requestChanged) draft.Models[0].MaxInputTokens = 65536;
                await store.SaveProviderCatalogueAsync(live, owner, draft, expected);
                Assert.AreSame(model, owner.Models.Single());
                Assert.AreEqual(epoch + (requestChanged ? 1 : 0), store.ProviderConfigurationVersion(owner));
                mcp.Release();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                var busy = typeof(ChatPanelView).GetProperty("Busy",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
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
    public void SameOrigin_NormalizesIdnaWithoutRelaxingDestinationBoundaries(string? left, string? right, bool expected)
    {
        Assert.AreEqual(expected, AiSettingsStore.SameOrigin(left, right));
    }

    [TestMethod]
    [DataRow("https://bücher.example/tenant-a", "https://xn--bcher-kva.example/tenant-a", false)]
    [DataRow("https://example.test/tenant-a", "https://example.test/tenant-b", false)]
    [DataRow("https://example.test/token?tenant=a", "https://example.test/token?tenant=b", false)]
    [DataRow("https://example.test/token", "https://example.test:443/token", false)]
    [DataRow("https://example.test/tenant-a", "https://example.test/tenant-a", true)]
    public void SameOAuthIssuer_StillRequiresTheCompleteUnchangedString(string left, string right, bool expected)
    {
        var method = typeof(ProviderSetupView).GetMethod("SameOAuthIssuer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.AreEqual(expected, (bool)method.Invoke(null, [left, right])!);
    }

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ProviderSetupViewUiTests).Assembly);

    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task PumpAsync(int rounds = 20)
    {
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T Find<T>(Control root, string name) where T : Control
        => root.GetLogicalDescendants().OfType<T>().First(c => c.Name == name);

    private static T? FindOrNull<T>(Control root, string name) where T : Control
        => root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    /// <summary>展开区里此刻<b>真正露在外面</b>的输入框(收在「高级设置」里的不算)。</summary>
    private static List<string> VisibleBoxes(Control root)
        => [.. root.GetLogicalDescendants().OfType<TextBox>()
                   .Where(box => box.Name is not null && IsShown(box))
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

    /// <summary>照用户的操作展开一行:真点在行上(命中测试、冒泡都真跑)。</summary>
    private static async Task ClickRowAsync(Window window, ProviderSetupView view, string entryId)
    {
        Border card = Find<Border>(view, $"SetupRow.{entryId}");
        // 先滚到可见区:目录一长,靠后的行落在窗口外面,坐标算出来是负的,点了什么也不会发生
        card.BringIntoView();
        await PumpAsync(10);
        // 点行的左上角一带:那儿是字母牌/标题,肯定落在行内
        Point spot = card.TranslatePoint(new Point(20, 18), window)!.Value;
        Assert.IsTrue(spot.Y > 0 && spot.Y < window.Height,
            $"{entryId} 那一行没滚进可见区(y={spot.Y}),这一点会落空");
        window.MouseDown(spot, MouseButton.Left);
        window.MouseUp(spot, MouseButton.Left);
        await PumpAsync(20);
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
        private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _response;
        public string BaseUrl { get; }
        public Task Requested => _requested.Task;

        public HeldModelsEndpoint(string host = "127.0.0.1", string? response = null)
        {
            _response = response ?? """{"data":[{"id":"old-model"}]}""";
            _listener = StartListener(out string baseUrl, host);
            BaseUrl = baseUrl;
            _ = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                HttpListenerContext request = await _listener.GetContextAsync();
                _requested.TrySetResult();
                await _released.Task;
                byte[] payload = Encoding.UTF8.GetBytes(_response);
                request.Response.ContentType = "application/json";
                request.Response.ContentLength64 = payload.Length;
                await request.Response.OutputStream.WriteAsync(payload);
                request.Response.Close();
            }
            catch (Exception)
            {
                // 测试收尾会关闭可能仍在等待的端点。
            }
        }

        public void Release() => _released.TrySetResult();
        public void Dispose()
        {
            _released.TrySetCanceled();
            _listener.Close();
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
        public bool FailOAuth { get; set; }
        public bool Hold { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
            => inner.GetAsync(name, cancellationToken);
        public async Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
        {
            if (FailOAuth && name.StartsWith("oauth:", StringComparison.Ordinal) && value.Contains("at-new", StringComparison.Ordinal))
                throw new IOException("OAuth secret write failed");
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
    private sealed class DeviceLoginEndpoint : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _serving;
        private int _requests;
        private int _stopping;
        private readonly List<string> _bodies = [];
        public string BaseUrl { get; }
        public int Requests => Volatile.Read(ref _requests);
        public IReadOnlyList<string> Bodies => _bodies;

        public DeviceLoginEndpoint()
        {
            _listener = StartListener(out string url);
            BaseUrl = url;
            _serving = ServeAsync();
        }

        private async Task ServeAsync()
        {
            while (Volatile.Read(ref _stopping) == 0 && _listener.IsListening)
            {
                HttpListenerContext request;
                try
                {
                    request = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException ex) when (Volatile.Read(ref _stopping) != 0 && ex.ErrorCode == 995)
                {
                    return; // 仅吸收显式 Close 中止的待接收操作,不捕获处理请求时的故障。
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _stopping) != 0)
                {
                    return;
                }
                Interlocked.Increment(ref _requests);
                using var reader = new StreamReader(request.Request.InputStream, Encoding.UTF8);
                _bodies.Add(await reader.ReadToEndAsync().ConfigureAwait(false));
                string json = request.Request.Url!.AbsolutePath == "/device"
                    ? """{"device_code":"dc","user_code":"AB","verification_uri":"","interval":1}"""
                    : """{"access_token":"at-new","expires_in":3600}""";
                byte[] payload = Encoding.UTF8.GetBytes(json);
                request.Response.ContentType = "application/json";
                request.Response.ContentLength64 = payload.Length;
                await request.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
                request.Response.Close();
            }
        }

        public void Dispose()
        {
            Volatile.Write(ref _stopping, 1);
            _listener.Close();
            _serving.GetAwaiter().GetResult();
        }
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
    public void RowsCarryNoActionButtonAndStartCollapsed()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context);
            try
            {
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
    public void ClickingARow_OnlyExpandsIt_AndNeverOpensTheBrowser()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            // OpenRouter 参数最齐(那一路连 client_id 都不需要)—— 最容易被"自动开浏览器"误伤的一条
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context);
            try
            {
                await ClickRowAsync(window, view, "openrouter");

                // 展开了:登录按钮出现
                Assert.AreEqual("Sign in", (string?)Find<Button>(view, "SetupPrimaryButton").Content);
                // 但什么都没发生 —— 用户还没决定要不要登,浏览器不该已经弹出去了
                Assert.AreEqual("", Find<TextBlock>(view, "SetupProgressText").Text ?? "",
                    "点行只该展开;弹浏览器要等用户点「登录」");
                Assert.IsFalse(Find<Button>(view, "SetupSecondaryButton").IsVisible,
                    "没在登录,就不该有「取消」");
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
                await ClickRowAsync(window, view, "deepseek");
                Assert.IsNotNull(FindOrNull<Button>(view, "SetupPrimaryButton"));

                await ClickRowAsync(window, view, "deepseek");

                Assert.IsNull(FindOrNull<Button>(view, "SetupPrimaryButton"), "再点一次该收起来");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void TheSignInButton_IsWhatOpensTheBrowser()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context);
            try
            {
                await ClickRowAsync(window, view, "openrouter");
                // 参数齐全的那一家,展开区里一个输入框都不该有
                Assert.IsEmpty(VisibleBoxes(view));

                Click(Find<Button>(view, "SetupPrimaryButton"));
                await PumpAsync(40);

                Assert.AreEqual("Browser opened — finish the sign-in there.",
                    Find<TextBlock>(view, "SetupProgressText").Text);
                Assert.AreEqual("Cancel", (string?)Find<Button>(view, "SetupSecondaryButton").Content);

                view.CancelPendingLogin();
                await PumpAsync();
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 登录还挂着时重进这一行必须什么都不做:重建会把自己刚起的那次掐掉,
    /// 而用户那边浏览器已经开着了 —— 回调就落到一个没人等的端口上
    /// (真机验收踩过,表现是"浏览器显示登录成功、程序毫无反应")。
    /// </summary>
    [TestMethod]
    public void ActivatingARowAgainWhileSigningIn_DoesNotRestartTheLogin()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context);
            try
            {
                await ClickRowAsync(window, view, "openrouter");
                Click(Find<Button>(view, "SetupPrimaryButton"));
                await PumpAsync(40);
                TextBlock progress = Find<TextBlock>(view, "SetupProgressText");

                await ClickRowAsync(window, view, "openrouter");

                // 这一行只要被重建,SetupProgressText 就是一个新的空白实例 —— 那就是"又开了一次"的指纹
                Assert.AreSame(progress, Find<TextBlock>(view, "SetupProgressText"));
                Assert.AreEqual("Browser opened — finish the sign-in there.", progress.Text);

                view.CancelPendingLogin();
                await PumpAsync();
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
            using var endpoint = new SseStub("");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, focus: "deepseek");
            try
            {
                Assert.AreSequenceEqual(["SetupKeyBox"], VisibleBoxes(view));
                Assert.AreEqual("https://api.deepseek.com/v1", Find<TextBox>(view, "SetupBaseUrlBox").Text,
                    "地址默认取自目录,不用人填");
                Find<TextBox>(view, "SetupBaseUrlBox").Text = endpoint.BaseUrl + "/v1";

                Find<TextBox>(view, "SetupKeyBox").Text = "sk-deepseek";
                await ClickPrimaryAsync(view);

                Assert.HasCount(1, settings.Providers);
                AiProvider added = settings.Providers[0];
                Assert.AreEqual("deepseek", added.CatalogId);
                Assert.AreEqual(endpoint.BaseUrl + "/v1", added.BaseUrl, "实际保存并拉取的是环回接入");
                Assert.AreEqual(added.Models[0].Id, settings.ActiveModelId);
                Assert.AreEqual("sk-deepseek", await store.GetApiKeyAsync(added.Id));
                Assert.AreEqual("Ready", Find<TextBlock>(view, "SetupPill.deepseek").Text);
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
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context, focus: "anthropic");
            try
            {
                Assert.AreSequenceEqual(["SetupKeyBox"], VisibleBoxes(view));

                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                await PumpAsync();

                List<string> boxes = VisibleBoxes(view);
                Assert.Contains("SetupNameBox", boxes);
                Assert.Contains("SetupModelBox", boxes);
                Assert.Contains("SetupBaseUrlBox", boxes);
                Assert.AreEqual("https://api.anthropic.com", Find<TextBox>(view, "SetupBaseUrlBox").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void APendingRegistration_AsksForTheClientIdOnceAndPointsAtWhereToGetIt()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, AiSettings settings, _) =
                await ShowAsync(context, focus: "huggingface");
            try
            {
                Assert.AreEqual("", ProviderCatalog.Find("huggingface")!.CreateProvider().OAuth!.ClientId,
                    "这条用例的前提是客户端 id 还空着;填上之后请把它挪到一键登录那条用例");
                Assert.AreSequenceEqual(["SetupClientIdBox"], VisibleBoxes(view));
                Assert.Contains(
                    b => (string?)b.Content == "Open the registration page", view.GetLogicalDescendants().OfType<Button>(),
                    "空着的客户端 id 旁边必须有去注册的入口");

                Click(Find<Button>(view, "SetupPrimaryButton"));
                await PumpAsync(40);
                Assert.IsEmpty(settings.Providers);
                Assert.AreEqual("Fill in the client ID and the endpoints first.",
                    Find<TextBlock>(view, "SetupProgressText").Text);
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
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context, focus: "azure-openai");
            try
            {
                List<string> boxes = VisibleBoxes(view);
                Assert.Contains("SetupClientIdBox", boxes);
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

    [TestMethod]
    public void LocalProviders_NeedNoKeyAndSaySo()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new SseStub("");
            (Window window, ProviderSetupView view, AiSettings settings, _) = await ShowAsync(context, focus: "ollama");
            try
            {
                Assert.IsEmpty(VisibleBoxes(view), "本地服务不需要鉴权,一个框都不该问");

                Find<TextBox>(view, "SetupBaseUrlBox").Text = endpoint.BaseUrl;
                await ClickPrimaryAsync(view);

                Assert.HasCount(1, settings.Providers);
                Assert.AreEqual("Ready", Find<TextBlock>(view, "SetupPill.ollama").Text);
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
            AiProvider groq = ProviderCatalog.Find("groq")!.CreateProvider();
            groq.AvailableModels = ["llama-3.3-70b-versatile", "mixtral-8x7b"];
            var seed = new AiSettings { Providers = [groq] };
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context, seed, focus: "groq");
            try
            {
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                await PumpAsync();

                ComboBox picker = Find<ComboBox>(view, "SetupModelPicker");
                Assert.AreSequenceEqual(groq.AvailableModels, (System.Collections.ICollection)picker.ItemsSource!);
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
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context, focus: "groq");
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
    public void ASignedInSubscription_ShowsConnectedAndOffersSigningOut()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider openrouter = ProviderCatalog.Find("openrouter")!.CreateProvider();
            var seed = new AiSettings { Providers = [openrouter] };
            var store = new AiSettingsStore(context);
            await store.SaveTokensAsync(openrouter.Id,
                new OAuthTokens { AccessToken = "sk-or-v1-abc", Account = "ops@example.com" });
            var view = new ProviderSetupView(context, store, seed, new Loc("en"));
            var window = new Window { Width = 720, Height = 720, Content = view };
            window.Show();
            await PumpAsync(40);
            try
            {
                Assert.AreEqual("Connected", Find<TextBlock>(view, "SetupPill.openrouter").Text);

                view.FocusEntry("openrouter");
                await PumpAsync();
                Assert.IsEmpty(VisibleBoxes(view), "已连接的那一条进去也不该有表单");
                Assert.AreEqual("Sign in again", (string?)Find<Button>(view, "SetupPrimaryButton").Content);
                Button signOut = Find<Button>(view, "SetupSecondaryButton");
                Assert.IsTrue(signOut.IsVisible);
                Assert.AreEqual("Sign out", (string?)signOut.Content);

                Click(signOut);
                await PumpAsync(40);

                Assert.IsNull(await store.GetTokensAsync(openrouter.Id));
                Assert.AreEqual("Not connected", Find<TextBlock>(view, "SetupPill.openrouter").Text);
                Assert.IsNotEmpty(seed.Providers, "退出登录不删供应商,只清凭据");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void RemovingAnApiKeyProvider_TakesItsSecretsWithIt()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider groq = ProviderCatalog.Find("groq")!.CreateProvider();
            var seed = new AiSettings
            {
                Providers = [groq],
                ActiveModelId = groq.Models[0].Id,
                // 链里钉着这条将被移除的模型:移除后必须连这条死条目一起剪掉(review⑥#9)
                FailoverChain = [new FailoverEntry { ModelId = groq.Models[0].Id }]
            };
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, seed);
            try
            {
                await store.SetApiKeyAsync(groq.Id, "gsk-1");
                await view.RefreshStatusAsync();
                await PumpAsync();
                Assert.AreEqual("Ready", Find<TextBlock>(view, "SetupPill.groq").Text);

                view.FocusEntry("groq");
                await PumpAsync();
                Button remove = Find<Button>(view, "SetupSecondaryButton");
                Assert.AreEqual("Remove", (string?)remove.Content);
                Click(remove);
                await PumpAsync(40);

                Assert.IsEmpty(settings.Providers);
                Assert.IsNull(await store.GetApiKeyAsync(groq.Id), "移除要连机密一起清,别留孤儿凭据");
                Assert.IsEmpty(settings.FailoverChain,
                    "移除连带剪掉故障转移链里的死条目 —— 否则设置窗口一开就是一行灰掉点不动的幽灵(review⑥#9)");
                Assert.IsNull(settings.ActiveModelId);
                Assert.AreEqual("Not added", Find<TextBlock>(view, "SetupPill.groq").Text);
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
            using var endpoint = new SseStub("");
            (Window window, ProviderSetupView view, AiSettings settings, _) =
                await ShowAsync(context, focus: "openai");
            try
            {
                ComboBox protocol = Find<ComboBox>(view, "SetupProtocolCombo");
                Assert.IsFalse(IsShown(protocol), "「高级设置」没展开,它就不该露在外面");
                Assert.AreEqual((int)ChatProtocol.OpenAiResponses, protocol.SelectedIndex,
                    "目录出厂是 Responses —— 想走 Chat Completions 的人得有地方改它");

                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                await PumpAsync();
                Assert.IsTrue(IsShown(Find<ComboBox>(view, "SetupProtocolCombo")));

                protocol.SelectedIndex = (int)ChatProtocol.OpenAiChatCompletions;
                Find<TextBox>(view, "SetupBaseUrlBox").Text = endpoint.BaseUrl + "/v1";
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-test";
                await ClickPrimaryAsync(view);

                Assert.HasCount(1, settings.Providers);
                Assert.AreEqual(ChatProtocol.OpenAiChatCompletions, settings.Providers[0].DefaultProtocol,
                    "协议要落到新加的这一份上");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>订阅那几家的线协议由目录定死 —— 摆个能改坏它的下拉只会让人选错。</summary>
    [TestMethod]
    public void ASubscriptionEntry_HasNoProtocolPicker()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            // 用「待填客户端 id」这条:订阅项缺件为零时展开区只有提示行、连「高级设置」都不开,
            // 拿它断言协议下拉等于什么都没测
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context, focus: "huggingface");
            try
            {
                Assert.AreEqual("", ProviderCatalog.Find("huggingface")!.CreateProvider().OAuth!.ClientId,
                    "这条用例的前提是客户端 id 还空着 —— 缺东西,展开区里才有「高级设置」可开");
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                await PumpAsync();

                Assert.IsNull(FindOrNull<ComboBox>(view, "SetupProtocolCombo"),
                    "订阅那几家的线协议由目录定死,不该摆个能改坏它的下拉");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void ProviderApiKey_AddsEncryptedSlotWithoutCloningProviderOrModels()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            using var endpoint = new SseStub("");
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
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var gate = (SemaphoreSlim)typeof(AiSettingsStore).GetProperty("_providerKeyGate", flags)!.GetValue(store)!;
            await gate.WaitAsync();
            bool released = false;
            try
            {
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                Find<TextBox>(view, "SetupKeyBox").Text = "sk-never-saved";
                object row = ((System.Collections.IEnumerable)typeof(ProviderSetupView)
                    .GetField("_cards", flags)!.GetValue(view)!).Cast<object>().Single(item =>
                        ((ProviderCatalogEntry)item.GetType().GetProperty("Entry")!.GetValue(item)!).Id == "custom-openai");
                Task saving = (Task)typeof(ProviderSetupView).GetMethod("PrimaryAsync", flags)!.Invoke(view, [row])!;
                Assert.IsFalse(Find<Button>(view, "SetupPrimaryButton").IsEnabled);
                if (action == "cancel") Click(Find<Button>(view, "SetupSecondaryButton"));
                else if (action == "collapse") await ClickRowAsync(window, view, "custom-openai");
                else window.Close();
                await saving.WaitAsync(TimeSpan.FromSeconds(10));
                gate.Release();
                released = true;
                Task<string?> baseline = (Task<string?>)typeof(ProviderSetupView)
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
            using var endpoint = new SseStub("");
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

    /// <summary>订阅已连接时点「再添加一个」:那是去登<b>另一个账号</b>,不是把已连着的那份重登一遍。</summary>
    [TestMethod]
    public void AddingAnother_OnAConnectedSubscription_ReadsAsASeparateAccount()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider openrouter = ProviderCatalog.Find("openrouter")!.CreateProvider();
            var seed = new AiSettings { Providers = [openrouter] };
            var store = new AiSettingsStore(context);
            await store.SaveTokensAsync(openrouter.Id,
                new OAuthTokens { AccessToken = "sk-or-v1-abc", Account = "ops@example.com" });
            var view = new ProviderSetupView(context, store, seed, new Loc("en"));
            var window = new Window { Width = 720, Height = 720, Content = view };
            window.Show();
            await PumpAsync(40);
            try
            {
                view.FocusEntry("openrouter");
                await PumpAsync();
                Assert.AreEqual("Sign in again", (string?)Find<Button>(view, "SetupPrimaryButton").Content);
                Button another = Find<Button>(view, "SetupAddAnotherButton");
                Assert.IsTrue(another.IsVisible);

                Click(another);
                await PumpAsync(20);

                Assert.AreEqual("Sign in", (string?)Find<Button>(view, "SetupPrimaryButton").Content,
                    "新建态是去登另一个账号,不能因为旧那份连着就说「重新登录」");
                Assert.AreEqual("Cancel", (string?)Find<Button>(view, "SetupSecondaryButton").Content);
                Assert.IsFalse(Find<Button>(view, "SetupAddAnotherButton").IsVisible);

                Click(Find<Button>(view, "SetupSecondaryButton"));
                await PumpAsync(20);

                Assert.AreEqual("Sign in again", (string?)Find<Button>(view, "SetupPrimaryButton").Content);
                Assert.AreEqual("Sign out", (string?)Find<Button>(view, "SetupSecondaryButton").Content);
                Assert.IsTrue(Find<Button>(view, "SetupAddAnotherButton").IsVisible);
                Assert.HasCount(1, seed.Providers, "这一轮自始至终没发起登录,不该多出任何实例");
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
            using var stub = new SseStub("data: [DONE]\n\n", jsonContent: "[]", hold: true);
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
            using var original = new SseStub(KeySaveProbeSse);
            using var changed = new SseStub(KeySaveProbeSse);
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
                await ClickPrimaryAsync(view);

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
            using var original = new SseStub(KeySaveProbeSse);
            using var changed = new SseStub(KeySaveProbeSse);
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
            using var endpoint = new SseStub(KeySaveProbeSse);
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
            using var endpoint = new SseStub(KeySaveProbeSse);
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


    [TestMethod]
    public void EarlierPrimaryCompletion_CannotResetANewerDeviceLoginOrEnableThirdLogin()
    {
        OnUi(async () =>
        {
            using var oldModels = new HeldModelsEndpoint();
            using var auth = StartListener(out string authUrl);
            var secondDevice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task serveAuth = Task.Run(async () =>
            {
                try
                {
                    for (int requestNumber = 1; ; requestNumber++)
                    {
                        HttpListenerContext request = await auth.GetContextAsync();
                        string json = requestNumber switch
                        {
                            1 => """{"device_code":"dc-1","user_code":"CODE-ONE","verification_uri":"","interval":1}""",
                            2 => """{"access_token":"at-first","expires_in":3600}""",
                            _ => """{"device_code":"dc-2","user_code":"CODE-TWO","verification_uri":"","interval":30}"""
                        };
                        byte[] payload = Encoding.UTF8.GetBytes(json);
                        request.Response.ContentType = "application/json";
                        request.Response.ContentLength64 = payload.Length;
                        await request.Response.OutputStream.WriteAsync(payload);
                        request.Response.Close();
                        if (requestNumber == 3)
                        {
                            secondDevice.TrySetResult();
                        }
                    }
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995 || !auth.IsListening) { }
                catch (ObjectDisposedException) when (!auth.IsListening) { }
            });
            using var context = new TestPluginContext();
            AiProvider first = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
            first.BaseUrl = oldModels.BaseUrl + "/v1";
            first.OAuth!.Flow = OAuthFlow.DeviceCode;
            first.OAuth.ClientId = "cid";
            first.OAuth.DeviceCodeUrl = authUrl + "/device";
            first.OAuth.TokenUrl = authUrl + "/token";
            (Window window, ProviderSetupView view, AiSettings settings, _) =
                await ShowAsync(context, new AiSettings { Providers = [first] }, focus: "custom-oauth");
            var firstPulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.ModelsChanged += () =>
            {
                if (first.AvailableModels.Contains("old-model"))
                {
                    firstPulled.TrySetResult();
                }
            };
            try
            {
                Click(Find<Button>(view, "SetupPrimaryButton"));
                await oldModels.Requested.WaitAsync(TimeSpan.FromSeconds(15));
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                Button primary = Find<Button>(view, "SetupPrimaryButton");
                Click(primary);
                await secondDevice.Task.WaitAsync(TimeSpan.FromSeconds(10));
                TextBlock? userCode = null;
                for (int i = 0; i < 100; i++)
                {
                    await PumpAsync(1);
                    userCode = view.GetLogicalDescendants().OfType<TextBlock>()
                        .FirstOrDefault(text => text.Text == "CODE-TWO" && IsShown(text));
                    if (userCode is not null) break;
                }
                Assert.IsNotNull(userCode, "第二次设备登录必须先显示自己的用户码");
                string? pendingProgress = Find<TextBlock>(view, "SetupProgressText").Text;

                oldModels.Release();
                await firstPulled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await PumpAsync(2);
                Assert.HasCount(1, settings.Providers, "第二次登录仍在等待用户授权");
                Assert.AreSame(primary, Find<Button>(view, "SetupPrimaryButton"));
                Assert.IsFalse(primary.IsEnabled, "旧主按钮收尾不得重启新主按钮");
                Assert.AreEqual("Cancel", (string?)Find<Button>(view, "SetupSecondaryButton").Content);
                Assert.IsTrue(IsShown(userCode), "旧登录收尾不能藏掉第二次设备码");
                Assert.AreEqual(pendingProgress, Find<TextBlock>(view, "SetupProgressText").Text);

                await ClickVisibleAsync(window, primary);
                Assert.IsFalse(primary.IsEnabled, "真实点击不可并发开启第三次登录");
                Assert.IsTrue(IsShown(userCode), "真实点击不能替换第二次设备码");
                Assert.AreEqual("Cancel", (string?)Find<Button>(view, "SetupSecondaryButton").Content);
                Click(Find<Button>(view, "SetupSecondaryButton"));
            }
            finally
            {
                oldModels.Release();
                view.CancelPendingLogin();
                await firstPulled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await WaitForEnabledAsync(Find<Button>(view, "SetupPrimaryButton"));
                window.Close();
                auth.Close();
                await serveAuth.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [TestMethod]
    public void SecondSubscription_PersistedDuringHeldModelPull_NeverShowsCancelForSignOut()
    {
        OnUi(async () =>
        {
            using var models = new SseStub("", hold: true);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            string authUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            Task auth = Task.Run(async () =>
            {
                string[] replies =
                [
                    """{"device_code":"dc","user_code":"AB","verification_uri":"","interval":1}""",
                    """{"access_token":"at-second","expires_in":3600}"""
                ];
                foreach (string json in replies)
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync();
                    using NetworkStream stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    int length = 0;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            length = int.Parse(line["Content-Length:".Length..].Trim());
                        }
                    }
                    char[] body = new char[length];
                    await reader.ReadBlockAsync(body.AsMemory());
                    byte[] payload = Encoding.UTF8.GetBytes(json);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(payload);
                }
            });
            using var context = new TestPluginContext();
            AiProvider first = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
            first.BaseUrl = models.BaseUrl + "/v1";
            first.OAuth!.Flow = OAuthFlow.DeviceCode;
            first.OAuth.ClientId = "cid";
            first.OAuth.DeviceCodeUrl = authUrl + "/device";
            first.OAuth.TokenUrl = authUrl + "/token";
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [first] }, focus: "custom-oauth");
            try
            {
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                await PumpAsync(20);
                Click(Find<Button>(view, "SetupPrimaryButton"));
                try
                {
                    await auth.WaitAsync(TimeSpan.FromSeconds(30));
                    await models.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (TimeoutException)
                {
                    Assert.Fail($"设备码登录或模型拉取未抵达本地端点: {Find<TextBlock>(view, "SetupProgressText").Text}");
                }
                Assert.HasCount(2, settings.Providers);
                AiProvider second = settings.Providers[1];
                Button secondary = Find<Button>(view, "SetupSecondaryButton");
                Assert.AreEqual("Sign out", (string?)secondary.Content);
                Assert.IsTrue(secondary.IsVisible && secondary.IsEnabled);
                Click(secondary);
                await PumpAsync(20);
                Assert.IsNull(await store.GetTokensAsync(second.Id));
                Assert.HasCount(2, settings.Providers);
            }
            finally
            {
                models.Release();
                view.CancelPendingLogin();
                await WaitForEnabledAsync(Find<Button>(view, "SetupPrimaryButton"));
                window.Close();
            }
        });
    }






    /// <summary>
    /// 四样齐全(BaseUrl / ClientId / 授权与令牌端点)的 OAuth 订阅接入,编辑态没什么可填,
    /// 早退成纯状态展示是对的 —— 但「再添加一个」进的是新建态,同一家的 OAuth 输入框必须摆出来,
    /// 否则新家连 ClientId 都没处填(review⑥#3)。
    /// </summary>
    [TestMethod]
    public void AddAnother_ShowsOAuthInputsEvenWhenEverythingIsConfigured()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider oauth = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
            oauth.BaseUrl = "https://gw.example.com/v1"; // 占位换真地址,四样才算齐
            oauth.OAuth!.ClientId = "cid-1";
            oauth.OAuth.AuthorizationUrl = "https://auth.example.com/authorize";
            oauth.OAuth.TokenUrl = "https://auth.example.com/token";
            (Window window, ProviderSetupView view, _, _) =
                await ShowAsync(context, new AiSettings { Providers = [oauth] }, focus: "custom-oauth");
            try
            {
                Assert.IsEmpty(VisibleBoxes(view),
                    "前提:四样齐全,编辑态没有可填的输入框 —— 早退成纯状态展示");

                Click(Find<Button>(view, "SetupAddAnotherButton"));
                await PumpAsync(20);

                List<string> boxes = VisibleBoxes(view);
                Assert.IsTrue(boxes.Contains("SetupClientIdBox"),
                    "新建态要能填 ClientId(review⑥#3)");
                Assert.IsTrue(boxes.Contains("SetupAuthUrlBox"),
                    "新建态要能填授权端点(review⑥#3)");
                Assert.IsTrue(boxes.Contains("SetupTokenUrlBox"),
                    "新建态要能填令牌端点(review⑥#3)");
            }
            finally
            {
                window.Close();
            }
        });
    }
    [TestMethod]
    public void AddAnother_CustomOAuthRequiresFreshAuthorizationForANewApiOrigin()
    {
        OnUi(async () =>
        {
            using var oldEndpoint = new SseStub("data: [DONE]\n\n");
            using var thirdEndpoint = new SseStub("data: [DONE]\n\n");
            using var newEndpoint = new SseStub("data: [DONE]\n\n");
            using var context = new TestPluginContext();
            AiProvider first = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
            first.BaseUrl = oldEndpoint.BaseUrl + "/v1";
            first.OAuth!.Flow = OAuthFlow.DeviceCode;
            first.OAuth.ClientId = "cid-old";
            first.OAuth.DeviceCodeUrl = oldEndpoint.BaseUrl + "/device";
            first.OAuth.TokenUrl = oldEndpoint.BaseUrl + "/token";
            first.OAuth.Scopes = "legacy-scope";
            (Window window, ProviderSetupView view, AiSettings settings, _) =
                await ShowAsync(context, new AiSettings { Providers = [first] }, focus: "custom-oauth");
            try
            {
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                await PumpAsync(20);
                TextBox address = Find<TextBox>(view, "SetupBaseUrlBox");
                address.Text = oldEndpoint.BaseUrl + "/v1";
                Assert.AreEqual("cid-old", Find<TextBox>(view, "SetupClientIdBox").Text);
                Assert.AreEqual(first.OAuth.DeviceCodeUrl, Find<TextBox>(view, "SetupDeviceUrlBox").Text);
                Assert.AreEqual(first.OAuth.TokenUrl, Find<TextBox>(view, "SetupTokenUrlBox").Text);
                Assert.AreEqual("legacy-scope", Find<TextBox>(view, "SetupScopesBox").Text);

                address.Text = newEndpoint.BaseUrl + "/v1";
                Click(Find<Button>(view, "SetupPrimaryButton")); // 不等 TextChanged,提交路径自己必须挡住
                await PumpAsync(20);
                Assert.AreEqual("", Find<TextBox>(view, "SetupClientIdBox").Text);
                Assert.AreEqual("", Find<TextBox>(view, "SetupDeviceUrlBox").Text);
                Assert.AreEqual("", Find<TextBox>(view, "SetupTokenUrlBox").Text);
                Assert.AreEqual("", Find<TextBox>(view, "SetupScopesBox").Text);
                Assert.HasCount(1, settings.Providers, "旧授权参数不能默默用于新 API origin");
                Assert.IsEmpty(oldEndpoint.Requests);
                Assert.IsEmpty(newEndpoint.Requests, "尚未重新配置时不能发起授权或传递旧 token");

                // 第二台的授权参数填好后再切到第三个 origin,仍不可暗带过去。
                Find<TextBox>(view, "SetupClientIdBox").Text = "cid-second";
                Find<TextBox>(view, "SetupDeviceUrlBox").Text = newEndpoint.BaseUrl + "/device";
                Find<TextBox>(view, "SetupTokenUrlBox").Text = newEndpoint.BaseUrl + "/token";
                address.Text = thirdEndpoint.BaseUrl + "/v1";
                Click(Find<Button>(view, "SetupPrimaryButton"));
                await PumpAsync(20);
                Assert.AreEqual("", Find<TextBox>(view, "SetupClientIdBox").Text);
                Assert.AreEqual("", Find<TextBox>(view, "SetupDeviceUrlBox").Text);
                Assert.AreEqual("", Find<TextBox>(view, "SetupTokenUrlBox").Text);
                Assert.IsEmpty(thirdEndpoint.Requests);
                address.Text = newEndpoint.BaseUrl + "/v1";
                await PumpAsync(20); // 等第三→第二台的异步 TextChanged 清完,再明确录入新参数
                Find<TextBox>(view, "SetupClientIdBox").Text = "cid-new";
                Find<TextBox>(view, "SetupDeviceUrlBox").Text = newEndpoint.BaseUrl + "/device";
                Find<TextBox>(view, "SetupTokenUrlBox").Text = newEndpoint.BaseUrl + "/token";
                await ClickPrimaryAsync(view);
                string request = await newEndpoint.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForEnabledAsync(Find<Button>(view, "SetupPrimaryButton"));
                Assert.IsTrue(request.Contains("client_id=cid-new", StringComparison.Ordinal));
                Assert.IsEmpty(oldEndpoint.Requests, "重新配置后的登录只能访问新填写的授权端点");
                Assert.HasCount(1, settings.Providers, "本地端点未签发 device_code,不能落库假登录");
            }
            finally
            {
                window.Close();
            }
        });
    }
    [TestMethod]
    [DataRow("same", false, false)]
    [DataRow("client-id", false, false)]
    [DataRow("api-path", false, true)]
    [DataRow("token-path", false, true)]
    [DataRow("device-path", false, true)]
    [DataRow("authorization-path", false, true)]
    [DataRow("token-path", true, true)]
    [DataRow("api", false, true)]
    [DataRow("token", false, false)]
    [DataRow("device", false, false)]
    [DataRow("authorization", false, false)]
    [DataRow("token", false, true)]
    [DataRow("device", false, true)]
    [DataRow("authorization", false, true)]
    [DataRow("token", true, true)]
    [DataRow("device", true, true)]
    [DataRow("authorization", true, true)]
    [DataRow("api", true, true)]
    public void AddAnother_OnlySameOriginCanSendClonedHiddenOAuthSecrets(
        string changed, bool newScope, bool waitForChange)
    {
        OnUi(async () =>
        {
            // 保持监听端口占用,不经 HttpListener 的临时端口/URL ACL;两次 OAuth POST 全在环回地址。
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            string localUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            Task<(string Device, string Token)> received = Task.Run(async () =>
            {
                string[] bodies = new string[2];
                for (int i = 0; i < bodies.Length; i++)
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync();
                    using NetworkStream stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    int length = 0;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            length = int.Parse(line["Content-Length:".Length..].Trim());
                        }
                    }
                    char[] body = new char[length];
                    Assert.AreEqual(length, await reader.ReadBlockAsync(body.AsMemory()));
                    bodies[i] = new string(body);
                    string json = i == 0
                        ? """{"device_code":"dc","user_code":"AB","verification_uri":"","interval":1}"""
                        : """{"error":"access_denied"}""";
                    byte[] content = Encoding.UTF8.GetBytes(json);
                    string status = i == 0 ? "200 OK" : "400 Bad Request";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(content);
                }
                return (bodies[0], bodies[1]);
            });

            using var context = new TestPluginContext();
            AiProvider first = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
            first.BaseUrl = (changed == "api" ? "https://old.invalid" : localUrl) + "/v1";
            first.OAuth!.Flow = OAuthFlow.DeviceCode;
            first.OAuth.ClientId = "cid-old";
            first.OAuth.ClientSecret = "secret-old-only";
            first.OAuth.ExtraAuthorizeParams = "secret=old-only";
            first.OAuth.ExtraHeaders = "X-Old-Key: old-only";
            first.OAuth.AuthorizationUrl = (changed == "authorization" ? "https://old.invalid" : localUrl)
                + (changed == "authorization-path" ? "/tenant-old/authorize" : "/authorize");
            first.OAuth.DeviceCodeUrl = (changed is "api" or "device" ? "https://old.invalid" : localUrl)
                + (changed == "device-path" ? "/tenant-old/device" : "/device");
            first.OAuth.TokenUrl = (changed is "api" or "token" ? "https://old.invalid" : localUrl)
                + (changed == "token-path" ? "/tenant-old/token" : "/token");
            first.OAuth.Scopes = "legacy-scope";
            (Window window, ProviderSetupView view, AiSettings settings, _) =
                await ShowAsync(context, new AiSettings { Providers = [first] }, focus: "custom-oauth");
            try
            {
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                await PumpAsync(20);
                Find<TextBox>(view, "SetupBaseUrlBox").Text = localUrl + (changed is "api" or "api-path" ? "/api" : "/v1");
                await PumpAsync(20);
                Find<TextBox>(view, "SetupClientIdBox").Text = changed == "client-id" || changed is "api" ? "cid-new" : "cid-old";
                Find<TextBox>(view, "SetupDeviceUrlBox").Text = localUrl + "/device";
                Find<TextBox>(view, "SetupTokenUrlBox").Text = localUrl + "/token";
                Find<TextBox>(view, "SetupAuthUrlBox").Text = localUrl + "/authorize";
                if (waitForChange && changed is ("token" or "device" or "authorization" or "token-path" or "device-path" or "authorization-path" or "api-path"))
                {
                    await PumpAsync(20); // TextChanged 由 UI 队列调度,只在调度后断言可见框。
                    Assert.AreEqual("", Find<TextBox>(view, "SetupScopesBox").Text,
                        "切换授权来源后应丢掉克隆的旧 scope");
                }
                bool reenteredSameScope = changed == "token" && newScope && waitForChange;
                if (newScope)
                {
                    Find<TextBox>(view, "SetupScopesBox").Text = reenteredSameScope ? "legacy-scope" : "new-scope";
                }
                // 未等待 TextChanged 的行直接点击,依靠提交前同步校验阻止旧 scope 出站。
                Click(Find<Button>(view, "SetupPrimaryButton"));
                (string device, string token) = await received.WaitAsync(TimeSpan.FromSeconds(10));
                await WaitForEnabledAsync(Find<Button>(view, "SetupPrimaryButton"));

                Assert.AreEqual(changed == "same", device.Contains("secret=old-only", StringComparison.Ordinal),
                    "授权请求不得继承旧 issuer 的隐藏查询参数");
                Assert.AreEqual(changed == "same" || reenteredSameScope,
                    device.Contains("scope=legacy-scope", StringComparison.Ordinal),
                    "同源或用户明确重填同名 scope 时才可沿用该权限");
                Assert.AreEqual(newScope && !reenteredSameScope, device.Contains("scope=new-scope", StringComparison.Ordinal),
                    "新 issuer 只能收到用户明确填入的 scope");
                Assert.AreEqual(changed == "same" || newScope,
                    device.Contains("scope=", StringComparison.Ordinal),
                    "未指定新 scope 时,授权请求不得携带旧 scope");
                Assert.AreEqual(changed == "same", token.Contains("client_secret=secret-old-only", StringComparison.Ordinal),
                    "新令牌端点绝不能收到旧 client_secret");
                Assert.HasCount(1, settings.Providers, "本地令牌端点拒绝登录,不会创建假实例");
            }
            finally
            {
                view.CancelPendingLogin();
                await WaitForEnabledAsync(Find<Button>(view, "SetupPrimaryButton"));
                window.Close();
            }
        });
    }
    [TestMethod]
    [DataRow("openrouter")]
    [DataRow("openai-codex")]
    [DataRow("huggingface")]
    public void ClonedBuiltinLogin_RejectsAnotherHostBeforeCredentialsOrModelsAreSent(string catalogId)
    {
        OnUi(async () =>
        {
            using var relay = new SseStub("", hold: true);
            using var context = new TestPluginContext();
            AiProvider official = ProviderCatalog.Find(catalogId)!.CreateProvider();
            await new AiSettingsStore(context).SaveTokensAsync(official.Id,
                new OAuthTokens { AccessToken = "sk-official-secret" });
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [official] }, focus: catalogId);
            try
            {
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                Find<TextBox>(view, "SetupBaseUrlBox").Text = relay.BaseUrl + "/v1";
                Click(Find<Button>(view, "SetupPrimaryButton"));
                await PumpAsync(20);

                Assert.HasCount(1, settings.Providers, "错误主机不能创建登录实例");
                Assert.IsEmpty(relay.Requests, "既不能向中转发送登录 Key,也不能尝试 /models");
                Assert.AreEqual(new Loc("en")["SetupBuiltinOAuthHostMismatch"],
                    Find<TextBlock>(view, "SetupProgressText").Text);
                Assert.AreEqual("sk-official-secret", (await store.GetTokensAsync(official.Id))?.AccessToken);
            }
            finally
            {
                relay.Release();
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
            AiProvider current = ProviderCatalog.Find("deepseek")!.CreateProvider();
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
                view.FocusEntry("deepseek");
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
    public void NewTenantOAuthLogin_DoesNotSendClonedAuthorizationHeadersToItsModelEndpoint()
    {
        OnUi(async () =>
        {
            using HttpListener listener = StartListener(out string url);
            var modelsRequest = new TaskCompletionSource<(string? Key, string? Auth)>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task server = Task.Run(async () =>
            {
                try
                {
                    string[] replies =
                    [
                        """{"device_code":"new-device","user_code":"AB","verification_uri":"","interval":1}""",
                        """{"access_token":"at-new-tenant","expires_in":3600}""",
                        """{"data":[{"id":"new-model"}]}"""
                    ];
                    for (int i = 0; i < replies.Length; i++)
                    {
                        HttpListenerContext request = await listener.GetContextAsync();
                        if (i == 2) modelsRequest.TrySetResult((request.Request.Headers["X-Old-Key"],
                            request.Request.Headers["Authorization"]));
                        byte[] payload = Encoding.UTF8.GetBytes(replies[i]);
                        request.Response.ContentType = "application/json";
                        request.Response.ContentLength64 = payload.Length;
                        await request.Response.OutputStream.WriteAsync(payload);
                        request.Response.Close();
                    }
                }
                catch (HttpListenerException) when (!listener.IsListening) { }
                catch (ObjectDisposedException) when (!listener.IsListening) { }
            });
            using var context = new TestPluginContext();
            AiProvider source = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
            source.BaseUrl = url + "/v1";
            source.OAuth!.Flow = OAuthFlow.DeviceCode;
            source.OAuth.ClientId = "cid";
            source.OAuth.DeviceCodeUrl = url + "/device";
            source.OAuth.TokenUrl = url + "/tenant-a/token";
            source.OAuth.Scopes = "old-scope";
            source.OAuth.ClientSecret = "old-secret";
            source.OAuth.ExtraHeaders = "X-Old-Key: old-only";
            source.OAuth.ExchangeHeaders = "X-Old-Exchange: old-only";
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [source] }, focus: "custom-oauth");
            try
            {
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                Find<TextBox>(view, "SetupTokenUrlBox").Text = url + "/tenant-b/token";
                await PumpAsync(10);
                Assert.AreEqual("", Find<TextBox>(view, "SetupScopesBox").Text);
                Find<TextBox>(view, "SetupScopesBox").Text = "new-scope";
                Click(Find<Button>(view, "SetupPrimaryButton"));
                (string? leakedKey, string? bearer) = await modelsRequest.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await WaitForEnabledAsync(Find<Button>(view, "SetupPrimaryButton"));

                Assert.HasCount(2, settings.Providers);
                AiProvider clone = settings.Providers[1];
                Assert.AreEqual("", clone.OAuth!.ClientSecret);
                Assert.AreEqual("", clone.OAuth.ExtraHeaders);
                Assert.AreEqual("", clone.OAuth.ExchangeHeaders);
                Assert.AreEqual("new-scope", clone.OAuth.Scopes);
                Assert.IsNull(leakedKey, "旧租户附加授权头不得发到新端点");
                Assert.AreEqual("Bearer at-new-tenant", bearer);
                Assert.AreEqual("at-new-tenant", (await store.GetTokensAsync(clone.Id))?.AccessToken);
                Assert.AreEqual("old-secret", source.OAuth.ClientSecret);
            }
            finally
            {
                view.CancelPendingLogin();
                window.Close();
                listener.Close();
                await server.WaitAsync(TimeSpan.FromSeconds(5));
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
            using var endpoint = new SseStub("");
            using var context = new TestPluginContext();
            AiProvider survivor = ProviderCatalog.Find("deepseek")!.CreateProvider();
            survivor.Name = "survivor";
            survivor.BaseUrl = endpoint.BaseUrl + "/v1";
            AiProvider displayed = ProviderCatalog.Find("deepseek")!.CreateProvider();
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
                await ClickRowAsync(window, view, "deepseek");
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

                await ClickRowAsync(window, view, "deepseek"); // 收起失效表单
                await ClickRowAsync(window, view, "deepseek"); // 新展开默认幸存实例
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
            using var endpoint = new SseStub("");
            using var context = new TestPluginContext();
            AiProvider survivor = ProviderCatalog.Find("deepseek")!.CreateProvider();
            survivor.Name = "survivor";
            survivor.BaseUrl = endpoint.BaseUrl + "/v1";
            AiProvider displayed = ProviderCatalog.Find("deepseek")!.CreateProvider();
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
                await ClickRowAsync(window, view, "deepseek");
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

                await ClickRowAsync(window, view, "deepseek");
                Assert.IsNull(FindOrNull<Button>(view, "SetupPrimaryButton"));
                await ClickRowAsync(window, view, "deepseek");
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
    [DataRow(false)]
    [DataRow(true)]
    public void LoginCompletingAfterSharedSourceRemoval_CannotSaveTokensOrCreateClone(bool clone)
    {
        OnUi(async () =>
        {
            using var models = new SseStub("", hold: true);
            using HttpListener auth = StartListener(out string authUrl);
            var exchanging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task exchange = Task.Run(async () =>
            {
                try
                {
                    string[] replies =
                    [
                        """{"device_code":"dc","user_code":"AB","verification_uri":"","interval":1}""",
                        """{"access_token":"at-late","expires_in":3600}"""
                    ];
                    for (int i = 0; i < replies.Length; i++)
                    {
                        HttpListenerContext request = await auth.GetContextAsync();
                        if (i == 1)
                        {
                            exchanging.TrySetResult();
                            await release.Task;
                        }
                        byte[] payload = Encoding.UTF8.GetBytes(replies[i]);
                        request.Response.ContentType = "application/json";
                        request.Response.ContentLength64 = payload.Length;
                        await request.Response.OutputStream.WriteAsync(payload);
                        request.Response.Close();
                    }
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995 || !auth.IsListening) { }
                catch (ObjectDisposedException) when (!auth.IsListening) { }
            });
            using var context = new TestPluginContext();
            AiProvider survivor = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
            AiProvider displayed = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
            displayed.BaseUrl = models.BaseUrl + "/v1";
            displayed.OAuth!.Flow = OAuthFlow.DeviceCode;
            displayed.OAuth.ClientId = "cid";
            displayed.OAuth.DeviceCodeUrl = authUrl + "/device";
            displayed.OAuth.TokenUrl = authUrl + "/token";
            var settings = new AiSettings { Providers = [survivor, displayed] };
            (Window window, ProviderSetupView view, _, AiSettingsStore store) = await ShowAsync(context, settings);
            try
            {
                await store.SaveTokensAsync(survivor.Id, new OAuthTokens { AccessToken = "at-survivor" });
                await view.RefreshStatusAsync();
                await ClickRowAsync(window, view, "custom-oauth");
                if (clone) Click(Find<Button>(view, "SetupAddAnotherButton"));
                Button primary = Find<Button>(view, "SetupPrimaryButton");
                Click(primary);
                await exchanging.Task.WaitAsync(TimeSpan.FromSeconds(15));
                settings.Providers.Remove(displayed);
                await store.SaveAsync(settings);
                release.TrySetResult();
                await exchange.WaitAsync(TimeSpan.FromSeconds(10));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!primary.IsEnabled)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    await PumpAsync(1);
                }
                Assert.HasCount(1, settings.Providers, "晚到的登录不能把已移除的源克隆回设置");
                Assert.AreSame(survivor, settings.Providers[0]);
                Assert.AreEqual("at-survivor", (await store.GetTokensAsync(survivor.Id))?.AccessToken);
                Assert.IsNull(await store.GetTokensAsync(displayed.Id), "已失效实例不能留下晚到的令牌");
                Assert.AreEqual(new Loc("en")["SetupSourceRemoved"],
                    Find<TextBlock>(view, "SetupProgressText").Text);
                Assert.IsEmpty(models.Requests, "失效登录不能再拉模型");
            }
            finally
            {
                release.TrySetResult();
                view.CancelPendingLogin();
                models.Release();
                window.Close();
                auth.Close();
                await exchange.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [TestMethod]
    public void RepeatedRemoveWhileFirstDeleteWaits_CannotDeleteTheRemainingInstance()
    {
        OnUi(async () =>
        {
            var storage = new KeySaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            AiProvider survivor = ProviderCatalog.Find("deepseek")!.CreateProvider();
            AiProvider removed = ProviderCatalog.Find("deepseek")!.CreateProvider();
            var settings = new AiSettings { Providers = [survivor, removed] };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            storage.Hold = true;
            var view = new ProviderSetupView(context, store, settings, new Loc("en"), "deepseek");
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
            using var savedEndpoint = new SseStub(KeySaveProbeSse);
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
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
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
            AiProvider survivor = ProviderCatalog.Find("deepseek")!.CreateProvider();
            survivor.Name = "survivor";
            AiProvider removed = ProviderCatalog.Find("deepseek")!.CreateProvider();
            var settings = new AiSettings { Providers = [survivor, removed] };
            var store = new AiSettingsStore(context);
            await store.SaveAsync(settings);
            await store.SetApiKeyAsync(survivor.Id, "sk-survivor");
            await store.SetApiKeyAsync(removed.Id, "sk-removed");
            storage.Hold = true;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var view = new ProviderSetupView(context, store, settings, new Loc("en"), "deepseek");
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
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                await ((Task)typeof(ProviderSetupView).GetMethod("AddOrSaveKeyAsync", flags)!
                    .Invoke(view, [ProviderCatalog.Find("deepseek")!])!).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual("survivor", survivor.Name);
                Assert.AreEqual("sk-survivor", await store.GetApiKeyAsync(survivor.Id));
                Assert.AreSame(primary, Find<Button>(view, "SetupPrimaryButton"));
                if (reopen)
                {
                    view.FocusEntry("deepseek");
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
                else Assert.AreNotSame(primary, Find<Button>(view, "SetupPrimaryButton"));
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
    public void FocusedEarlierSubscription_SignsOutAndRelogsOnlyThatAccount()
    {
        OnUi(async () =>
        {
            using var models = new SseStub("");
            using HttpListener auth = StartListener(out string authUrl);
            Task exchange = Task.Run(async () =>
            {
                try
                {
                    foreach (string reply in new[]
                    {
                        """{"device_code":"dc","user_code":"AB","verification_uri":"","interval":1}""",
                        """{"access_token":"at-first-new","expires_in":3600}"""
                    })
                    {
                        HttpListenerContext request = await auth.GetContextAsync();
                        byte[] body = Encoding.UTF8.GetBytes(reply);
                        request.Response.ContentType = "application/json";
                        request.Response.ContentLength64 = body.Length;
                        await request.Response.OutputStream.WriteAsync(body);
                        request.Response.Close();
                    }
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995 || !auth.IsListening) { }
                catch (ObjectDisposedException) when (!auth.IsListening) { }
            });
            using var context = new TestPluginContext();
            AiProvider first = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
            first.BaseUrl = models.BaseUrl + "/v1";
            first.OAuth!.Flow = OAuthFlow.DeviceCode;
            first.OAuth.ClientId = "first-client";
            first.OAuth.DeviceCodeUrl = authUrl + "/device";
            first.OAuth.TokenUrl = authUrl + "/token";
            AiProvider last = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
            last.BaseUrl = first.BaseUrl;
            AiSettings settings = new() { Providers = [first, last] };
            var seedStore = new AiSettingsStore(context);
            await seedStore.SaveTokensAsync(first.Id, new OAuthTokens { AccessToken = "at-first-old" });
            await seedStore.SaveTokensAsync(last.Id, new OAuthTokens { AccessToken = "at-last" });
            (Window window, ProviderSetupView view, _, AiSettingsStore store) = await ShowAsync(context, settings);
            try
            {
                view.FocusEntry("custom-oauth", first.Id);
                await PumpAsync();
                Button secondary = Find<Button>(view, "SetupSecondaryButton");
                Assert.AreEqual("Sign out", secondary.Content);
                Assert.IsTrue(secondary.IsEnabled);
                Click(secondary);
                await PumpAsync(20);
                Assert.IsNull(await store.GetTokensAsync(first.Id), "退出早先实例应仅清除它自己的令牌");
                Assert.AreEqual("at-last", (await store.GetTokensAsync(last.Id))?.AccessToken);
                await ClickPrimaryAsync(view);
                await exchange.WaitAsync(TimeSpan.FromSeconds(20));
                await models.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual("at-first-new", (await store.GetTokensAsync(first.Id))?.AccessToken);
                Assert.AreEqual("at-last", (await store.GetTokensAsync(last.Id))?.AccessToken);
                Assert.HasCount(2, settings.Providers, "重新登录不是另建第三份");
            }
            finally
            {
                view.CancelPendingLogin();
                models.Release();
                window.Close();
                auth.Close();
                await exchange.WaitAsync(TimeSpan.FromSeconds(5));
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
            using var endpoint = new SseStub(KeySaveProbeSse);
            using var secretSource = new TestPluginContext();
            var secrets = new KeySaveSecrets(secretSource.Secrets);
            var storage = new KeySaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Secrets = secrets, Storage = storage };
            AiProvider survivor = ProviderCatalog.Find("deepseek")!.CreateProvider();
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
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
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
                var savedJson = await context.Storage.GetAsync<System.Text.Json.JsonElement>("settings");
                Assert.IsFalse(savedJson.GetRawText().Contains("sk-new", StringComparison.Ordinal), "API Key不得进入JSON");
                (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(created, created.Models[0]));
                Assert.IsNull(error, error?.ToString());
                Assert.IsTrue(endpoint.Authorizations.All(header => header == "Bearer sk-new"));
                Assert.IsTrue(endpoint.Requests.Any(body => body.Contains("\"model\":\"my-retry-model\"", StringComparison.Ordinal)));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    [DataRow("new", "secret")]
    [DataRow("new", "json")]
    [DataRow("new", "conflict")]
    [DataRow("another", "secret")]
    [DataRow("another", "json")]
    [DataRow("another", "conflict")]
    [DataRow("relogin", "secret")]
    [DataRow("relogin", "json")]
    [DataRow("relogin", "conflict")]
    public void OAuthCommitFailure_KeepsLiveAccountTokensAndDraftUntilExplicitRetry(string mode, string failure)
    {
        OnUi(async () =>
        {
            using var endpoint = new SseStub(KeySaveProbeSse);
            using var authorization = new DeviceLoginEndpoint();
            using var secretSource = new TestPluginContext();
            var secrets = new KeySaveSecrets(secretSource.Secrets);
            var storage = new KeySaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Secrets = secrets, Storage = storage };
            AiProvider source = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
            source.BaseUrl = endpoint.BaseUrl + "/v1";
            source.Models[0].Model = "old-model";
            source.OAuth!.Flow = OAuthFlow.DeviceCode;
            source.OAuth.ClientId = "cid";
            source.OAuth.DeviceCodeUrl = authorization.BaseUrl + "/device";
            source.OAuth.TokenUrl = authorization.BaseUrl + "/token";
            AiSettings settings = new()
            {
                Providers = mode == "new" ? [] : [source],
                ActiveModelId = mode == "new" ? null : source.Models[0].Id
            };
            if (mode != "new") await new AiSettingsStore(context).SaveTokensAsync(source.Id,
                new OAuthTokens { AccessToken = "at-old", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            (Window window, ProviderSetupView view, _, AiSettingsStore store) = await ShowAsync(context, settings, "custom-oauth");
            int notifications = 0;
            view.ProviderChanged += _ => notifications++;
            try
            {
                if (mode == "another") Click(Find<Button>(view, "SetupAddAnotherButton"));
                if (mode == "new")
                {
                    Find<TextBox>(view, "SetupBaseUrlBox").Text = endpoint.BaseUrl + "/v1";
                    Find<ComboBox>(view, "SetupFlowCombo").SelectedIndex = 1;
                    Find<TextBox>(view, "SetupClientIdBox").Text = "cid";
                    Find<TextBox>(view, "SetupDeviceUrlBox").Text = authorization.BaseUrl + "/device";
                    Find<TextBox>(view, "SetupTokenUrlBox").Text = authorization.BaseUrl + "/token";
                }
                string expectedName = mode == "relogin" ? source.Name : "retry account";
                string expectedModel = mode == "relogin" ? "old-model" : "retry-model";
                TextBox? model = FindOrNull<TextBox>(view, "SetupModelBox");
                if (mode != "relogin")
                {
                    Find<TextBox>(view, "SetupNameBox").Text = expectedName;
                    model!.Text = expectedModel;
                }
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                string draftId = mode == "relogin" ? source.Id
                    : ((AiProvider)typeof(ProviderSetupView).GetField("_newProviderDraft", flags)!.GetValue(view)!).Id;
                if (failure == "conflict")
                {
                    AiSettings external = await store.LoadAsync();
                    external.SystemPrompt = "external prompt";
                    await store.SaveAsync(external);
                }
                secrets.FailOAuth = failure == "secret";
                storage.Failure = failure;
                await ClickPrimaryAsync(view);
                Assert.AreEqual(2, authorization.Requests, "失败不得自动重放授权请求");
                Assert.HasCount(mode == "new" ? 0 : 1, settings.Providers);
                Assert.HasCount(mode == "new" ? 0 : 1, (await store.LoadAsync()).Providers);
                Assert.AreEqual(mode == "relogin" ? "at-old" : null, (await store.GetTokensAsync(draftId))?.AccessToken);
                string? rawTokens = await secretSource.Secrets.GetAsync($"oauth:{draftId}");
                Assert.IsTrue(rawTokens is null || !rawTokens.Contains("at-new", StringComparison.Ordinal),
                    "失败不得只恢复缓存而遗留新机密");
                Assert.AreEqual(mode == "new" ? null : source.Models[0].Id, settings.ActiveModelId);
                if (model is not null)
                {
                    Assert.AreEqual(expectedModel, model.Text);
                    Assert.AreSame(model, Find<TextBox>(view, "SetupModelBox"));
                }
                Assert.AreEqual(0, notifications);
                Assert.IsEmpty(endpoint.Requests);
                if (mode != "new")
                {
                    Assert.AreEqual("old-model", source.Models[0].Model);
                    Assert.AreEqual("at-old", (await store.GetTokensAsync(source.Id))?.AccessToken);
                    (Exception? oldError, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(source, source.Models[0]));
                    Assert.IsNull(oldError, oldError?.ToString());
                    Assert.AreEqual("Bearer at-old", endpoint.Authorizations.Single());
                }
                secrets.FailOAuth = false;
                storage.Failure = null;
                await ClickPrimaryAsync(view);
                Assert.AreEqual(4, authorization.Requests, "只有用户第二次点击才重新授权");
                Assert.HasCount(mode == "another" ? 2 : 1, settings.Providers);
                AiProvider saved = settings.Providers.Single(provider => provider.Id == draftId);
                Assert.AreEqual(expectedName, saved.Name);
                Assert.AreEqual(expectedModel, saved.Models[0].Model);
                Assert.AreEqual("at-new", (await store.GetTokensAsync(saved.Id))?.AccessToken);
                Assert.AreEqual(1, notifications);
                if (failure == "conflict") Assert.AreEqual("external prompt", settings.SystemPrompt);
                var savedJson = await context.Storage.GetAsync<System.Text.Json.JsonElement>("settings");
                Assert.IsFalse(savedJson.GetRawText().Contains("at-new", StringComparison.Ordinal), "OAuth令牌不得进入JSON");
                (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(saved, saved.Models[0]));
                Assert.IsNull(error, error?.ToString());
                Assert.AreEqual("Bearer at-new", endpoint.Authorizations[^1]);
                Assert.Contains($"\"model\":\"{expectedModel}\"", endpoint.Requests[^1]);
            }
            finally { view.CancelPendingLogin(); window.Close(); }
        });
    }

    [TestMethod]
    public void OAuthLogin_ConcurrentLiveIssuerChangeCannotAuthorizeHiddenFormSecrets()
    {
        OnUi(async () =>
        {
            using HttpListener listener = StartListener(out string url);
            var captured = new TaskCompletionSource<(string Body, string? Header)>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task server = Task.Run(async () =>
            {
                HttpListenerContext request = await listener.GetContextAsync();
                using var reader = new StreamReader(request.Request.InputStream);
                string body = await reader.ReadToEndAsync();
                captured.TrySetResult((body, request.Request.Headers["X-Old-Key"]));
                request.Response.StatusCode = 400;
                request.Response.Close();
            });
            using var context = new TestPluginContext();
            AiProvider provider = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
            provider.BaseUrl = url + "/v1";
            provider.OAuth!.Flow = OAuthFlow.DeviceCode;
            provider.OAuth.ClientId = "cid";
            provider.OAuth.DeviceCodeUrl = ""; // 缺失设备端点时,真实目录表单允许补填当前 issuer。
            provider.OAuth.TokenUrl = url + "/tenant-a/token";
            provider.OAuth.ClientSecret = "tenant-a-secret";
            provider.OAuth.ExtraHeaders = "X-Old-Key: tenant-a-secret";
            (Window window, ProviderSetupView view, _, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider] }, "custom-oauth");
            try
            {
                Find<TextBox>(view, "SetupDeviceUrlBox").Text = url + "/tenant-b/device";
                Find<TextBox>(view, "SetupTokenUrlBox").Text = url + "/tenant-b/token";
                provider.OAuth.DeviceCodeUrl = url + "/tenant-b/device";
                provider.OAuth.TokenUrl = url + "/tenant-b/token";
                await PumpAsync();
                Click(Find<Button>(view, "SetupPrimaryButton"));
                var request = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsNull(request.Header, "共享实例的新地址不得授权旧表单隐藏头");
                Assert.DoesNotContain("tenant-a-secret", request.Body);
                await WaitForEnabledAsync(Find<Button>(view, "SetupPrimaryButton"));
                Assert.IsNull(await store.GetTokensAsync(provider.Id));
            }
            finally
            {
                view.CancelPendingLogin(); window.Close(); listener.Close();
                try { await server; }
                catch (ObjectDisposedException) when (!listener.IsListening) { }
                catch (HttpListenerException) when (!listener.IsListening) { }
            }
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
            using var endpoint = new SseStub(KeySaveProbeSse);
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
                    settings.Providers[0] = System.Text.Json.JsonSerializer.Deserialize<AiProvider>(
                        System.Text.Json.JsonSerializer.Serialize(opened))!;
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
                if (mode == "replacement") Assert.AreEqual("opened-model", opened.Models[0].Model,
                    "旧引用不能偷偷重新绑定到另一份live实例");
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
            using var endpoint = new SseStub(KeySaveProbeSse);
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
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
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
                if (field == "matching-model") Assert.HasCount(1, source.Models.Where(item => item.Model == "old-model").ToArray(),
                    "等待目录期间已添加或改名为该型号,不能再生成同型号配置");
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
                await ClickPrimaryAsync(view);
                Assert.AreEqual(2, notifications);
                Assert.AreEqual("my-draft-name", source.Name);
                Assert.AreEqual("my-draft-model", source.Models[0].Model);
                Assert.AreEqual(ChatProtocol.OpenAiChatCompletions, new ResolvedModel(source, source.Models[0]).Protocol);
                Assert.AreSame(source, settings.Providers.Single());
                (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(source, source.Models[0]));
                Assert.IsNull(error, error?.ToString());
                Assert.AreEqual("Bearer sk-original", endpoint.Authorizations[^1]);
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
            using var original = new SseStub(KeySaveProbeSse);
            using var latest = new SseStub(KeySaveProbeSse);
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
    [DataRow(false)]
    [DataRow(true)]
    public void OAuthConflictRecovery_OnlyUntouchedIssuerFieldsFollowExternalBeforeExplicitRetry(bool dirtyIssuer)
    {
        OnUi(async () =>
        {
            using var original = new SseStub(KeySaveProbeSse);
            using var latest = new SseStub(KeySaveProbeSse);
            using var oldIssuer = new DeviceLoginEndpoint();
            using var newIssuer = new DeviceLoginEndpoint();
            using var context = new TestPluginContext();
            AiProvider provider = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
            provider.Name = "opened account";
            provider.BaseUrl = original.BaseUrl + "/v1";
            provider.Models[0].Model = "opened-model";
            provider.OAuth!.Flow = OAuthFlow.DeviceCode;
            provider.OAuth.ClientId = dirtyIssuer ? "" : "cid-old";
            provider.OAuth.DeviceCodeUrl = dirtyIssuer ? "" : oldIssuer.BaseUrl + "/device";
            provider.OAuth.TokenUrl = oldIssuer.BaseUrl + "/token";
            provider.OAuth.Scopes = "old-scope";
            if (!dirtyIssuer) await new AiSettingsStore(context).SaveTokensAsync(provider.Id,
                new OAuthTokens { AccessToken = "at-old", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            (Window window, ProviderSetupView view, _, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [provider] }, "custom-oauth");
            try
            {
                if (dirtyIssuer)
                {
                    Find<TextBox>(view, "SetupClientIdBox").Text = "cid-old";
                    Find<TextBox>(view, "SetupDeviceUrlBox").Text = oldIssuer.BaseUrl + "/device";
                    Find<TextBox>(view, "SetupScopesBox").Text = "my-scope";
                }
                AiSettings external = await store.LoadAsync();
                AiProvider changed = external.Providers.Single();
                changed.Name = "latest account";
                changed.BaseUrl = (dirtyIssuer ? original : latest).BaseUrl + "/v1";
                changed.Models[0].Model = "latest-model";
                changed.OAuth!.ClientId = "cid-new";
                changed.OAuth.DeviceCodeUrl = newIssuer.BaseUrl + "/device";
                changed.OAuth.TokenUrl = newIssuer.BaseUrl + "/token";
                changed.OAuth.Scopes = "fresh-scope";
                await store.SaveAsync(external);
                await ClickPrimaryAsync(view);
                Assert.AreEqual(2, oldIssuer.Requests);
                Assert.AreEqual(0, newIssuer.Requests);
                Assert.AreEqual(dirtyIssuer ? null : "at-old", (await store.GetTokensAsync(provider.Id))?.AccessToken);
                Assert.IsEmpty(original.Requests);
                Assert.IsEmpty(latest.Requests);
                if (dirtyIssuer)
                {
                    Assert.AreEqual("cid-old", Find<TextBox>(view, "SetupClientIdBox").Text);
                    Assert.AreEqual(oldIssuer.BaseUrl + "/device", Find<TextBox>(view, "SetupDeviceUrlBox").Text);
                    Assert.AreEqual("my-scope", Find<TextBox>(view, "SetupScopesBox").Text);
                    TextBox token = Find<TextBox>(view, "SetupTokenUrlBox");
                    Assert.AreEqual(newIssuer.BaseUrl + "/token", token.Text, "未编辑令牌地址仍跟随外部更新");
                    Assert.Contains("cid-new", Find<TextBlock>(view, "SetupConflictValues").Text!);
                    Assert.Contains("fresh-scope", Find<TextBlock>(view, "SetupConflictValues").Text!);
                    token.Text = oldIssuer.BaseUrl + "/token"; // 核对差异后,用户显式选回完整的原 issuer。
                }
                await ClickPrimaryAsync(view);
                Assert.AreEqual(dirtyIssuer ? 4 : 2, oldIssuer.Requests,
                    "只有真正修改的授权字段才能在显式确认后覆盖外部值");
                Assert.AreEqual(dirtyIssuer ? 0 : 2, newIssuer.Requests);
                DeviceLoginEndpoint selectedIssuer = dirtyIssuer ? oldIssuer : newIssuer;
                Assert.Contains(dirtyIssuer ? "client_id=cid-old" : "client_id=cid-new", selectedIssuer.Bodies[^1]);
                Assert.Contains(dirtyIssuer ? "my-scope" : "fresh-scope", selectedIssuer.Bodies[^2]);
                Assert.AreEqual("latest account", provider.Name);
                Assert.AreEqual("latest-model", provider.Models[0].Model);
                Assert.AreEqual(selectedIssuer.BaseUrl + "/token", provider.OAuth.TokenUrl);
                Assert.AreEqual(selectedIssuer.BaseUrl + "/device", provider.OAuth.DeviceCodeUrl);
                Assert.AreEqual(dirtyIssuer ? "my-scope" : "fresh-scope", provider.OAuth.Scopes);
                (Exception? error, _) = await HealthProbe.ProbeAsync(store, new ResolvedModel(provider, provider.Models[0]));
                Assert.IsNull(error, error?.ToString());
                SseStub selectedEndpoint = dirtyIssuer ? original : latest;
                Assert.IsEmpty((dirtyIssuer ? latest : original).Requests);
                Assert.AreEqual("Bearer at-new", selectedEndpoint.Authorizations[^1]);
                Assert.Contains("\"model\":\"latest-model\"", selectedEndpoint.Requests[^1]);
            }
            finally { view.CancelPendingLogin(); window.Close(); }
        });
    }

    [TestMethod]
    public void ConflictRecovery_AfterHeldCommitUsesSubmittedInputBaselineAndKeepsTheLaterKeyDraft()
    {
        OnUi(async () =>
        {
            using var endpoint = new SseStub(KeySaveProbeSse);
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
                Assert.AreEqual(requests, endpoint.Requests.Count, "冲突不能自动提交或继续模型请求");
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
    public void InvalidCatalogFocus_PreservesTheDisplayedInstanceAndItsRemovalTarget(string? catalogId)
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider first = ProviderCatalog.Find("deepseek")!.CreateProvider();
            AiProvider latest = ProviderCatalog.Find("deepseek")!.CreateProvider();
            first.Name = "first";
            latest.Name = "latest";
            await context.Secrets.SetAsync($"apikey:{first.Id}", "sk-first");
            await context.Secrets.SetAsync($"apikey:{latest.Id}", "sk-latest");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [first, latest] });
            try
            {
                view.FocusEntry("deepseek", first.Id);
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
    [DataRow("name")]
    [DataRow("url")]
    [DataRow("model")]
    [DataRow("protocol")]
    [DataRow("flow")]
    [DataRow("client")]
    [DataRow("authorize")]
    [DataRow("token")]
    [DataRow("device")]
    [DataRow("scopes")]
    public void OAuthUnsavedInputs_BlockAnotherAccountWithoutDiscardingTheOriginalForm(string field)
    {
        OnUi(async () =>
        {
            using var issuer = new DeviceLoginEndpoint();
            using var context = new TestPluginContext();
            AiProvider provider = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
            provider.BaseUrl = issuer.BaseUrl + "/v1";
            provider.Models[0].Model = "original-model";
            provider.OAuth!.Flow = OAuthFlow.DeviceCode;
            // 来源尚缺注册信息,真实页面才显示可编辑的原账号表单。
            provider.OAuth.ClientId = provider.OAuth.AuthorizationUrl = provider.OAuth.TokenUrl = provider.OAuth.DeviceCodeUrl = "";
            provider.OAuth.Scopes = "original-scope";
            (Window window, ProviderSetupView view, AiSettings settings, _) =
                await ShowAsync(context, new AiSettings { Providers = [provider] }, "custom-oauth");
            try
            {
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                TextBox name = Find<TextBox>(view, "SetupNameBox");
                Control edited = field switch
                {
                    "name" => name,
                    "url" => Find<TextBox>(view, "SetupBaseUrlBox"),
                    "model" => Find<TextBox>(view, "SetupModelBox"),
                    "protocol" => Find<ComboBox>(view, "SetupProtocolCombo"),
                    "flow" => Find<ComboBox>(view, "SetupFlowCombo"),
                    "client" => Find<TextBox>(view, "SetupClientIdBox"),
                    "authorize" => Find<TextBox>(view, "SetupAuthUrlBox"),
                    "token" => Find<TextBox>(view, "SetupTokenUrlBox"),
                    "device" => Find<TextBox>(view, "SetupDeviceUrlBox"),
                    _ => Find<TextBox>(view, "SetupScopesBox")
                };
                if (edited is TextBox text) text.Text = "unsaved-input";
                else if (edited is ComboBox combo) combo.SelectedIndex = combo.SelectedIndex == 0 ? 1 : 0;
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                Assert.AreSame(name, Find<TextBox>(view, "SetupNameBox"));
                Assert.AreEqual(new Loc("en")["SetupSaveProviderFirst"], Find<TextBlock>(view, "SetupProgressText").Text);
                Assert.HasCount(1, settings.Providers);
                Assert.AreEqual("Sign out", Find<Button>(view, "SetupSecondaryButton").Content);
                Assert.AreEqual(0, issuer.Requests, "阻止切换草稿时不得发起授权");
                if (edited is TextBox draft) Assert.AreEqual("unsaved-input", draft.Text);
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
            using var endpoint = new SseStub("");
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
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
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
            using var endpoint = new SseStub("");
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
            AiProvider displayed = ProviderCatalog.Find("deepseek")!.CreateProvider();
            AiProvider latest = ProviderCatalog.Find("deepseek")!.CreateProvider();
            string additional = Guid.NewGuid().ToString("N");
            displayed.AdditionalApiKeyIds = [additional];
            await context.Secrets.SetAsync($"apikey:{displayed.Id}", "sk-original");
            await context.Secrets.SetAsync($"apikey:{additional}", "sk-additional");
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = [displayed, latest], ActiveModelId = displayed.Models[0].Id });
            try
            {
                view.FocusEntry("deepseek", displayed.Id);
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

    [TestMethod]
    [DataRow("new", false)]
    [DataRow("new", true)]
    [DataRow("another", false)]
    [DataRow("another", true)]
    [DataRow("relogin", false)]
    [DataRow("relogin", true)]
    public void OAuthCommitHeldInRealStorage_KeepsLaterInputsAndUsesSubmittedBaselineForAnother(string mode, bool editLater)
    {
        OnUi(async () =>
        {
            using var issuer = new DeviceLoginEndpoint();
            using var models = new SseStub("");
            var storage = new KeySaveStorage(new InMemoryStorage());
            using var context = new TestPluginContext { Storage = storage };
            AiProvider source = ProviderCatalog.Find("custom-oauth")!.CreateProvider();
            source.BaseUrl = models.BaseUrl + "/v1";
            source.Models[0].Model = "original-model";
            source.OAuth!.Flow = OAuthFlow.DeviceCode;
            source.OAuth.ClientId = source.OAuth.DeviceCodeUrl = source.OAuth.TokenUrl = "";
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, new AiSettings { Providers = mode == "new" ? [] : [source] }, "custom-oauth");
            try
            {
                if (mode == "another") Click(Find<Button>(view, "SetupAddAnotherButton"));
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                TextBox name = Find<TextBox>(view, "SetupNameBox");
                TextBox scopes = Find<TextBox>(view, "SetupScopesBox");
                Find<TextBox>(view, "SetupBaseUrlBox").Text = models.BaseUrl + "/v1";
                Find<TextBox>(view, "SetupClientIdBox").Text = "original-client";
                Find<ComboBox>(view, "SetupFlowCombo").SelectedIndex = 1;
                Find<TextBox>(view, "SetupDeviceUrlBox").Text = issuer.BaseUrl + "/device";
                Find<TextBox>(view, "SetupTokenUrlBox").Text = issuer.BaseUrl + "/token";
                Find<TextBox>(view, "SetupModelBox").Text = "submitted-model";
                await PumpAsync(2);
                name.Text = "  submitted-account  ";
                scopes.Text = "  submitted-scope  ";
                int notifications = 0;
                view.ProviderChanged += _ => notifications++;
                Button primary = Find<Button>(view, "SetupPrimaryButton");
                storage.Hold = true;
                Click(primary);
                await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
                if (editLater) { name.Text = "later-account"; scopes.Text = "later-scope"; }
                storage.Release.TrySetResult();
                await WaitForEnabledAsync(primary);
                AiProvider saved = settings.Providers.Last();
                Assert.AreEqual("submitted-account", saved.Name);
                Assert.AreEqual("submitted-scope", saved.OAuth!.Scopes);
                Assert.AreEqual("at-new", (await store.GetTokensAsync(saved.Id))?.AccessToken);
                Assert.AreSame(name, Find<TextBox>(view, "SetupNameBox"));
                Assert.AreEqual(editLater ? "later-account" : "  submitted-account  ", name.Text);
                Assert.AreEqual(editLater ? "later-scope" : "  submitted-scope  ", scopes.Text);
                Assert.AreEqual(1, notifications);
                Assert.AreEqual(2, issuer.Requests);
                Click(Find<Button>(view, "SetupAddAnotherButton"));
                if (editLater)
                {
                    Assert.AreSame(name, Find<TextBox>(view, "SetupNameBox"));
                    Assert.AreEqual(new Loc("en")["SetupSaveProviderFirst"], Find<TextBlock>(view, "SetupProgressText").Text);
                    Assert.AreEqual("later-scope", scopes.Text);
                }
                else
                {
                    Assert.AreNotSame(name, Find<TextBox>(view, "SetupNameBox"), "已提交的空格输入不应阻止下一账号草稿");
                    Assert.AreEqual("Cancel", Find<Button>(view, "SetupSecondaryButton").Content);
                }
                Assert.AreEqual(2, issuer.Requests, "主提交成功不应重放授权或自动提交后续草稿");
                Assert.AreEqual(1, notifications);
                Assert.HasCount(mode == "another" ? 2 : 1, settings.Providers);
            }
            finally { storage.Release.TrySetResult(); view.CancelPendingLogin(); window.Close(); }
        });
    }

}
