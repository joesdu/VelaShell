using System.Net;
using VelaShell.Plugin.Ai.Auth;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 两段式登录(GitHub Copilot):设备码换长期 token,再换一枚短命会话令牌,
/// 而后者过期时靠<b>重做一次交换</b>续期,不是标准的 refresh_token 授权。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class CopilotExchangeTests
{
    private static readonly LoginPrompts Prompts =
        new("Signed in", "Close this tab.", "waiting", "exchanging", "code {0}");

    private static OAuthConfig Copilot() => ProviderCatalog.Find("github-copilot")!.CreateProvider().OAuth!;

    private const string ExchangeBody = """
        {"token":"copilot-session-abc","expires_at":4102444800,
         "endpoints":{"api":"https://api.enterprise.githubcopilot.com"}}
        """;

    [TestMethod]
    public void Catalog_CopilotIsATwoStepDeviceFlow()
    {
        ProviderCatalogEntry entry = ProviderCatalog.Find("github-copilot")!;
        AiProvider provider = entry.CreateProvider();

        Assert.IsTrue(entry.Experimental, "借的是 GitHub 官方插件的客户端身份,得如实标出来");
        Assert.AreEqual(OAuthFlow.GitHubCopilotDevice, provider.OAuth!.Flow);
        Assert.IsNotEmpty(provider.OAuth.DeviceCodeUrl);
        Assert.IsNotEmpty(provider.OAuth.ExchangeUrl);
        Assert.IsTrue(provider.CanSignIn);
    }

    [TestMethod]
    public async Task SignIn_DoesTheSecondExchangeAndKeepsTheLongLivedToken()
    {
        OAuthStub stub = new OAuthStub()
            .Json("""{"device_code":"dc","user_code":"AB12-CD34","verification_uri":"https://github.com/login/device","interval":1}""")
            .Json("""{"access_token":"gho_longlived","token_type":"bearer","scope":"read:user"}""")
            .Json(ExchangeBody);
        using var http = new HttpClient(stub);
        var login = new ProviderLogin(
            new OAuthClient(http) { Delay = (_, _) => Task.CompletedTask },
            (_, _) => Task.CompletedTask);

        OAuthTokens tokens = await login.SignInAsync(Copilot(), Prompts);

        // 发请求用的是换来的那枚,而不是 GitHub 那个
        Assert.AreEqual("copilot-session-abc", tokens.AccessToken);
        // 长期那个留着 —— 会话令牌过期后还要靠它再换一次
        Assert.AreEqual("gho_longlived", tokens.RefreshToken);
        Assert.IsNotNull(tokens.ExpiresAt);
        // 端点由服务端下发:企业账户与个人账户不是同一个
        Assert.AreEqual("https://api.enterprise.githubcopilot.com", tokens.BaseUrl);
        // 第二段用的是 GitHub 的 token 方案,不是 Bearer
        Assert.AreEqual("https://api.github.com/copilot_internal/v2/token", stub.Requests[2].Url);
    }

    [TestMethod]
    public async Task Refresh_RedoesTheExchangeInsteadOfARefreshTokenGrant()
    {
        OAuthStub stub = new OAuthStub().Json(ExchangeBody);
        using var http = new HttpClient(stub);
        var stale = new OAuthTokens
        {
            AccessToken = "copilot-session-expired",
            RefreshToken = "gho_longlived",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        OAuthTokens fresh = await new OAuthClient(http).RefreshAsync(Copilot(), stale);

        Assert.AreEqual("copilot-session-abc", fresh.AccessToken);
        Assert.AreEqual("gho_longlived", fresh.RefreshToken);
        // 打的是交换端点,不是 GitHub 的 token 端点
        Assert.AreEqual("https://api.github.com/copilot_internal/v2/token", stub.Requests[0].Url);
        Assert.IsEmpty(stub.Requests[0].Body, "GET,没有请求体");
    }

    /// <summary>
    /// 交换端点会校验调用方是不是一个编辑器:只带 Authorization 会被 403,
    /// 而报错正文里一个字都不提缺了什么(真机上就撞过)。
    /// </summary>
    [TestMethod]
    public async Task Exchange_IdentifiesItselfAsAnEditor()
    {
        OAuthStub stub = new OAuthStub().Json(ExchangeBody);
        using var http = new HttpClient(stub);

        await new OAuthClient(http).ExchangeForSessionAsync(Copilot(), "gho_x");

        string headers = string.Join("\n", stub.RequestHeaders);
        Assert.Contains("Editor-Version", headers, $"实际头:{headers}");
        Assert.Contains("User-Agent", headers, $"实际头:{headers}");
    }

    [TestMethod]
    public void Catalog_CopilotDeclaresItsExchangeHeaders()
    {
        // 目录里没配就等于 403,而那个 403 什么线索都不给 —— 上个棘轮
        OAuthConfig oauth = ProviderCatalog.Find("github-copilot")!.CreateProvider().OAuth!;

        Assert.IsNotEmpty(oauth.ExchangeHeaders);
        Assert.Contains("Editor-Version", oauth.ExchangeHeaders);
    }

    [TestMethod]
    public async Task Exchange_WithoutATokenInTheResponse_Fails()
    {
        OAuthStub stub = new OAuthStub().Json("""{"message":"no subscription"}""", HttpStatusCode.Forbidden);
        using var http = new HttpClient(stub);

        await Assert.ThrowsExactlyAsync<OAuthException>(() =>
            new OAuthClient(http).ExchangeForSessionAsync(Copilot(), "gho_x"));
    }

    [TestMethod]
    public async Task Exchange_WithoutAnEndpointFallsBackToTheConfiguredBaseUrl()
    {
        OAuthStub stub = new OAuthStub().Json("""{"token":"t","expires_at":4102444800}""");
        using var http = new HttpClient(stub);

        OAuthTokens tokens = await new OAuthClient(http).ExchangeForSessionAsync(Copilot(), "gho_x");

        Assert.IsNull(tokens.BaseUrl, "没下发端点就别覆盖,用目录里配的那个");
    }

    // ---- 端点覆盖真的生效 ----

    [TestMethod]
    public async Task Credential_CarriesTheServerAssignedEndpoint()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        AiProvider provider = ProviderCatalog.Find("github-copilot")!.CreateProvider();
        await store.SaveTokensAsync(provider.Id, new OAuthTokens
        {
            AccessToken = "copilot-session",
            RefreshToken = "gho_longlived",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            BaseUrl = "https://api.enterprise.githubcopilot.com"
        });

        ProviderCredential credential =
            await store.ResolveCredentialAsync(new ResolvedModel(provider, provider.Models[0]));

        Assert.AreEqual("copilot-session", credential.Value);
        Assert.IsTrue(credential.IsBearerToken);
        Assert.AreEqual("https://api.enterprise.githubcopilot.com", credential.BaseUrl);
        Assert.IsNotNull(credential.Headers);
    }

    [TestMethod]
    public async Task Credential_WithoutAnAssignedEndpointLeavesItAlone()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        AiProvider provider = ProviderCatalog.Find("openrouter")!.CreateProvider();
        await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "sk-or" });

        ProviderCredential credential =
            await store.ResolveCredentialAsync(new ResolvedModel(provider, provider.Models[0]));

        Assert.IsNull(credential.BaseUrl);
    }

    [TestMethod]
    [DataRow("openrouter")]
    [DataRow("openai-codex")]
    [DataRow("anthropic-claude")]
    [DataRow("huggingface")]
    [DataRow("github-copilot")]
    public async Task BuiltInOAuthCredentialCannotBeUsedAtAnotherHost(string catalog)
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        AiProvider provider = ProviderCatalog.Find(catalog)!.CreateProvider();
        await store.SaveTokensAsync(provider.Id, new OAuthTokens { AccessToken = "sk-or-secret" });

        provider.BaseUrl = "https://relay.example/v1";
        AiSettingsStore.BuiltinOAuthHostMismatchException providerError =
            await Assert.ThrowsExactlyAsync<AiSettingsStore.BuiltinOAuthHostMismatchException>(() =>
                store.ResolveProviderCredentialAsync(provider));
        AiSettingsStore.BuiltinOAuthHostMismatchException clientError =
            Assert.ThrowsExactly<AiSettingsStore.BuiltinOAuthHostMismatchException>(() =>
            {
                _ = store.CreateClient(new ResolvedModel(provider, provider.Models[0]), ProviderCredential.Key("sk-or-secret"));
            });

        provider.BaseUrl = ProviderCatalog.Find(catalog)!.CreateProvider().BaseUrl;
        provider.Models[0].BaseUrlOverride = "https://relay.example/v1";
        AiSettingsStore.BuiltinOAuthHostMismatchException modelError =
            await Assert.ThrowsExactlyAsync<AiSettingsStore.BuiltinOAuthHostMismatchException>(() =>
                store.ResolveCredentialAsync(new ResolvedModel(provider, provider.Models[0])));
        foreach (Exception error in new Exception[] { providerError, clientError, modelError })
        {
            Assert.IsTrue(TransientFailure.IsWorthSwitching(error));
            Assert.IsFalse(TransientFailure.IsTransient(error));
        }
    }

    // ---- Claude 订阅 ----

    [TestMethod]
    public void Catalog_ClaudeSubscriptionUsesItsOwnCallbackAndBetaHeader()
    {
        ProviderCatalogEntry entry = ProviderCatalog.Find("anthropic-claude")!;
        AiProvider provider = entry.CreateProvider();
        OAuthConfig oauth = provider.OAuth!;

        Assert.IsTrue(entry.Experimental);
        Assert.AreEqual(ChatProtocol.AnthropicMessages, provider.DefaultProtocol);
        // 回调要与 Claude Code 注册的那条逐字节一致:固定端口,主机名是 localhost 不是 127.0.0.1
        Assert.AreEqual(53692, oauth.RedirectPort);
        Assert.AreEqual("localhost", oauth.RedirectHost);
        Assert.AreEqual("https://claude.ai/oauth/authorize", oauth.AuthorizationUrl);
        Assert.Contains("oauth-2025-04-20", oauth.ExtraHeaders);
        Assert.IsTrue(provider.CanSignIn);
    }

    [TestMethod]
    public async Task ClaudeSubscription_SendsTheTokenAsBearerNotAsAnApiKey()
    {
        // Anthropic 协议下,手填的 Key 走 x-api-key,而订阅令牌必须走 Authorization: Bearer —— 两者不能混
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        AiProvider provider = ProviderCatalog.Find("anthropic-claude")!.CreateProvider();
        await store.SaveTokensAsync(provider.Id, new OAuthTokens
        {
            AccessToken = "sk-ant-oat-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });

        ProviderCredential credential =
            await store.ResolveCredentialAsync(new ResolvedModel(provider, provider.Models[0]));

        Assert.AreEqual("sk-ant-oat-1", credential.Value);
        Assert.IsTrue(credential.IsBearerToken);
        Assert.Contains(
            new KeyValuePair<string, string>("anthropic-beta", "oauth-2025-04-20"), credential.Headers!);
    }
    [TestMethod]
    [DataRow(400, true)]
    [DataRow(401, true)]
    [DataRow(403, true)]
    [DataRow(404, false)]
    [DataRow(405, false)]
    [DataRow(408, false)]
    [DataRow(429, false)]
    [DataRow(500, false)]
    [DataRow(503, false)]
    public async Task StoreRefresh_HttpStatusDeterminesFallbackBeforeOAuthPayload(int status, bool fallback)
    {
        foreach (bool copilot in new[] { false, true })
        foreach (string body in new[] { "{\"error\":\"invalid_grant\"}", "{}", "not-json" })
        {
            using var context = new TestPluginContext();
            OAuthStub stub = new OAuthStub().Json(body, (HttpStatusCode)status);
            using var http = new HttpClient(stub);
            var store = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
            AiProvider provider = ProviderCatalog.Find(copilot ? "github-copilot" : "anthropic-claude")!.CreateProvider();
            // handler 截获全部请求,使用环回占位地址,不接触供应商或国际域名。
            provider.OAuth!.TokenUrl = provider.OAuth.ExchangeUrl = "http://127.0.0.1/oauth";
            await store.SaveTokensAsync(provider.Id, new OAuthTokens
            {
                AccessToken = "old-access", RefreshToken = "old-refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            if (fallback)
            {
                ProviderCredential credential = await store.ResolveCredentialAsync(new ResolvedModel(provider, provider.Models[0]));
                Assert.AreEqual("old-access", credential.Value);
            }
            else
            {
                HttpRequestException error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
                    store.ResolveCredentialAsync(new ResolvedModel(provider, provider.Models[0])));
                Assert.AreEqual((HttpStatusCode)status, error.StatusCode);
            }
            OAuthTokens saved = (await store.GetTokensAsync(provider.Id))!;
            Assert.AreEqual("old-access", saved.AccessToken);
            Assert.AreEqual("old-refresh", saved.RefreshToken);
            Assert.IsTrue(saved.NeedsRefresh, "失败的刷新不可保存新令牌或过期时间");
            Assert.HasCount(1, stub.Requests);
        }
    }

    private sealed class RefreshFailureHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(error);
    }

    [TestMethod]
    public async Task StoreRefresh_TransportFailuresAndCancellationPropagateWithoutSaving()
    {
        foreach (Exception failure in new Exception[]
        {
            new HttpRequestException("network"), new IOException("read"), new TimeoutException("timeout"),
            new OperationCanceledException("cancel"), new TaskCanceledException("HTTP timeout")
        })
        {
            using var context = new TestPluginContext();
            using var http = new HttpClient(new RefreshFailureHandler(failure));
            var store = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
            AiProvider provider = ProviderCatalog.Find("anthropic-claude")!.CreateProvider();
            provider.OAuth!.TokenUrl = "http://127.0.0.1/oauth";
            await store.SaveTokensAsync(provider.Id, new OAuthTokens
            {
                AccessToken = "old-access", RefreshToken = "old-refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            Exception? caught = null;
            try { await store.ResolveCredentialAsync(new ResolvedModel(provider, provider.Models[0])); }
            catch (Exception error) { caught = error; }
            Assert.IsNotNull(caught);
            if (failure is OperationCanceledException) Assert.IsInstanceOfType<OperationCanceledException>(caught);
            else Assert.AreSame(failure, caught);
            Assert.AreEqual("old-access", (await store.GetTokensAsync(provider.Id))!.AccessToken);
        }
    }

    [TestMethod]
    [DataRow(400)]
    [DataRow(401)]
    [DataRow(403)]
    public async Task StoreRefresh_HttpAuthorizationExceptionsAlsoKeepOldToken(int status)
    {
        using var context = new TestPluginContext();
        using var http = new HttpClient(new RefreshFailureHandler(new HttpRequestException("denied", null, (HttpStatusCode)status)));
        var store = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
        AiProvider provider = ProviderCatalog.Find("anthropic-claude")!.CreateProvider();
        provider.OAuth!.TokenUrl = "http://127.0.0.1/oauth";
        await store.SaveTokensAsync(provider.Id, new OAuthTokens
        {
            AccessToken = "old-access", RefreshToken = "old-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        Assert.AreEqual("old-access", (await store.ResolveCredentialAsync(new ResolvedModel(provider, provider.Models[0]))).Value);
        Assert.AreEqual("old-access", (await store.GetTokensAsync(provider.Id))!.AccessToken);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"error\":\"invalid_grant\"}")]
    public async Task StoreRefresh_NonHttpOAuthFailuresStillKeepOldTokens(string body)
    {
        using var context = new TestPluginContext();
        using var http = new HttpClient(new OAuthStub().Json(body));
        var store = new AiSettingsStore(context) { TokenClient = new OAuthClient(http) };
        AiProvider provider = ProviderCatalog.Find("anthropic-claude")!.CreateProvider();
        provider.OAuth!.TokenUrl = "http://127.0.0.1/oauth";
        await store.SaveTokensAsync(provider.Id, new OAuthTokens
        {
            AccessToken = "old-access", RefreshToken = "old-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        Assert.AreEqual("old-access", (await store.ResolveCredentialAsync(new ResolvedModel(provider, provider.Models[0]))).Value);
        Assert.AreEqual("old-refresh", (await store.GetTokensAsync(provider.Id))!.RefreshToken);
    }

}
