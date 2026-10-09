using System.ClientModel;
using System.ClientModel.Primitives;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 聊天工具条上的短格式,以及"这次失败值不值得重来"的判定。
/// </summary>
/// <remarks>
/// 这几件原先住在一个两千八百行、要整套 Avalonia 与插件上下文才构造得起来的代码隐藏里 ——
/// 于是连"1000 该显示成 1k 还是 1.0k"这种一眼能验的事,都一条用例都没有。
/// </remarks>
[TestClass]
[TestCategory("ChatFormatting")]
public sealed class ChatFormattingTests
{
    [TestMethod]
    public void SmallCountsAreShownInFull()
    {
        Assert.AreEqual("0", ChatFormatting.Compact(0));
        Assert.AreEqual("999", ChatFormatting.Compact(999));
    }

    [TestMethod]
    public void ThousandsKeepOneDecimal()
    {
        Assert.AreEqual("1k", ChatFormatting.Compact(1_000));
        Assert.AreEqual("1.2k", ChatFormatting.Compact(1_234));
    }

    /// <summary>一万以上只留整数位。</summary>
    /// <remarks>
    /// 工具条按字符宽度计价:那个量级上的小数位没有信息量,只把旁边的模型名往外挤。
    /// </remarks>
    [TestMethod]
    public void TenThousandsDropTheDecimal()
    {
        Assert.AreEqual("12k", ChatFormatting.Compact(12_345));
        Assert.AreEqual("999k", ChatFormatting.Compact(999_000));
    }

    [TestMethod]
    public void MillionsSwitchUnit()
    {
        Assert.AreEqual("1M", ChatFormatting.Compact(1_000_000));
        Assert.AreEqual("1.2M", ChatFormatting.Compact(1_234_567));
    }

    [TestMethod]
    public void ShortDurationsAreSeconds()
    {
        Assert.AreEqual("0.8s", ChatFormatting.Duration(TimeSpan.FromSeconds(0.8)));
        Assert.AreEqual("12.3s", ChatFormatting.Duration(TimeSpan.FromSeconds(12.34)));
        Assert.AreEqual("59.9s", ChatFormatting.Duration(TimeSpan.FromSeconds(59.9)));
    }

    [TestMethod]
    public void AMinuteSwitchesToMinutesAndSeconds()
    {
        Assert.AreEqual("1m 0s", ChatFormatting.Duration(TimeSpan.FromSeconds(60)));
        Assert.AreEqual("1m 5s", ChatFormatting.Duration(TimeSpan.FromSeconds(65)));
        Assert.AreEqual("2m 3s", ChatFormatting.Duration(TimeSpan.FromSeconds(123)));
    }

    [TestMethod]
    public void OneLineFlattensNewlines()
    {
        // 带换行的文本直接放进标签,高度会突然涨到几行,把整条工具条顶变形。
        Assert.DoesNotContain("\n", ChatFormatting.OneLine("a\nb\nc", 100));
        Assert.AreEqual("a b c", ChatFormatting.OneLine("a\nb\nc", 100));
    }

    [TestMethod]
    public void OneLineTruncatesWithAnEllipsis()
    {
        Assert.AreEqual("abcde", ChatFormatting.OneLine("abcde", 5));
        Assert.AreEqual("abcde…", ChatFormatting.OneLine("abcdefgh", 5));
    }

    [TestMethod]
    public void OneLineHandlesEmptyInput()
    {
        Assert.AreEqual(string.Empty, ChatFormatting.OneLine(null, 10));
        Assert.AreEqual(string.Empty, ChatFormatting.OneLine("", 10));
    }
}

/// <summary>
/// 一次失败值不值得自动重来。
/// </summary>
/// <remarks>
/// 判错了的两种后果都不轻:把参数错当成瞬时故障会白白多打一次(还多花一次钱),
/// 把网络抖动当成永久失败则让用户在明明能成的时候看到一条红字。
/// </remarks>
[TestClass]
[TestCategory("ChatFormatting")]
public sealed class TransientFailureTests
{
    [TestMethod]
    [DataRow(400, 401)]
    [DataRow(400, 403)]
    [DataRow(404, 401)]
    [DataRow(404, 403)]
    public void Review_OuterHttpFailureOverridesInnerAuthentication(int status, int innerStatus)
    {
        var nested = new HttpRequestException("inner-auth", null, (System.Net.HttpStatusCode)innerStatus);
        var failure = new InvalidOperationException("SDK wrapper",
            new HttpRequestException("outer-permanent", nested, (System.Net.HttpStatusCode)status));
        Assert.IsFalse(TransientFailure.IsTransient(failure));
        Assert.IsFalse(TransientFailure.IsApiKeyFailure(failure));
        Assert.IsFalse(TransientFailure.IsWorthSwitching(failure));
    }

    [TestMethod]
    public void NetworkAndTimeoutFailuresAreRetryable()
    {
        Assert.IsTrue(TransientFailure.IsTransient(new HttpRequestException("boom")));
        Assert.IsTrue(TransientFailure.IsTransient(new IOException("reset")));
        Assert.IsTrue(TransientFailure.IsTransient(new TimeoutException()));
    }

    [TestMethod]
    public void EmptyCompletedResponseIsRetryableAndSwitchable()
    {
        var empty = new HealthProbe.EmptyReplyException();
        Assert.IsTrue(TransientFailure.IsTransient(empty));
        Assert.IsTrue(TransientFailure.IsWorthSwitching(empty));
    }

    [TestMethod]
    public void ServerSideThrottlingAndOutagesAreRetryable()
    {
        Assert.IsTrue(TransientFailure.IsTransient(new ClientResultException("slow down", Response(429))));
        Assert.IsTrue(TransientFailure.IsTransient(new ClientResultException("timeout", Response(408))));
        Assert.IsTrue(TransientFailure.IsTransient(new ClientResultException("oops", Response(500))));
        Assert.IsTrue(TransientFailure.IsTransient(new ClientResultException("gateway", Response(503))));
    }

    [TestMethod]
    public void ClientMistakesAreNotRetryable()
    {
        // 参数错、鉴权失败重试一万次也一样 —— 只是把钱和时间花掉。
        Assert.IsFalse(TransientFailure.IsTransient(new ClientResultException("bad request", Response(400))));
        Assert.IsFalse(TransientFailure.IsTransient(new ClientResultException("unauthorized", Response(401))));
        Assert.IsFalse(TransientFailure.IsTransient(new ClientResultException("not found", Response(404))));
        Assert.IsFalse(TransientFailure.IsTransient(new InvalidOperationException("bug")));
    }

    /// <summary>
    /// 带状态码的 <see cref="HttpRequestException" /> 是服务端答复过了 —— 按状态码判,
    /// 不能因为类型是 HttpRequestException 就一律当网络抖动:400/404 那类换到哪儿都一样,
    /// 放进故障转移只会白烧整条链的探测钱。
    /// </summary>
    [TestMethod]
    public void StatusBearingHttpRequestsAreJudgedByStatus()
    {
        // 瞬时码照旧;没状态码(压根没连上)也照旧
        Assert.IsTrue(TransientFailure.IsTransient(new HttpRequestException("bad gateway", null, System.Net.HttpStatusCode.BadGateway)));
        Assert.IsTrue(TransientFailure.IsTransient(new HttpRequestException("timeout", null, System.Net.HttpStatusCode.RequestTimeout)));
        Assert.IsTrue(TransientFailure.IsTransient(new HttpRequestException("offline")));

        // 永久错:不重来、也不换家
        Assert.IsFalse(TransientFailure.IsTransient(new HttpRequestException("bad request", null, System.Net.HttpStatusCode.BadRequest)));
        Assert.IsFalse(TransientFailure.IsTransient(new HttpRequestException("not found", null, System.Net.HttpStatusCode.NotFound)));
        Assert.IsFalse(TransientFailure.IsWorthSwitching(new HttpRequestException("bad request", null, System.Net.HttpStatusCode.BadRequest)));
        Assert.IsFalse(TransientFailure.IsWorthSwitching(new HttpRequestException("not found", null, System.Net.HttpStatusCode.NotFound)));

        // 401/403 经这条形态也得认出来:不瞬时(原地重试没用),但值得换一家
        Assert.IsFalse(TransientFailure.IsTransient(new HttpRequestException("unauthorized", null, System.Net.HttpStatusCode.Unauthorized)));
        Assert.IsTrue(TransientFailure.IsWorthSwitching(new HttpRequestException("unauthorized", null, System.Net.HttpStatusCode.Unauthorized)));
        Assert.IsTrue(TransientFailure.IsWorthSwitching(new HttpRequestException("forbidden", null, System.Net.HttpStatusCode.Forbidden)));
    }

    /// <summary>
    /// Anthropic SDK 自己的 <c>AnthropicApiException</c> 也得按状态码判(review⑥#1):
    /// 只认 <c>HttpRequestException</c> 时,这家 401 鉴权失败既不瞬时、也不值得换一家,
    /// 整条链原地不动;5xx 则被判成永久失败,白白不重试。
    /// </summary>
    [TestMethod]
    public void AnthropicSdkExceptionsAreJudgedByStatusToo()
    {
        // 带状态码 = 服务端答复过了:瞬时码照旧,永久错照旧不重来
        Assert.IsTrue(TransientFailure.IsTransient(Api(500)));
        Assert.IsTrue(TransientFailure.IsTransient(Api(429)));
        Assert.IsFalse(TransientFailure.IsTransient(Api(400)));
        Assert.IsFalse(TransientFailure.IsTransient(Api(404)));

        // 401/403:不瞬时(原地重试没用),但值得换一家 —— 故障转移要接得住它
        Assert.IsFalse(TransientFailure.IsTransient(Api(401)));
        Assert.IsTrue(TransientFailure.IsWorthSwitching(Api(401)));
        Assert.IsTrue(TransientFailure.IsWorthSwitching(Api(403)));
        Assert.IsFalse(TransientFailure.IsWorthSwitching(Api(400)));

        // 码为 0(网络层失败被它包了进来):真实原因在 InnerException 那层,要走得到
        Assert.IsTrue(TransientFailure.IsTransient(
            new Anthropic.Exceptions.AnthropicApiException("wrapped", new HttpRequestException("offline"))
            {
                StatusCode = 0,
                ResponseBody = ""
            }));
    }

    /// <summary>造一个 Anthropic SDK 形态的异常(状态码用对象初始化器摆上)。</summary>
    private static Anthropic.Exceptions.AnthropicApiException Api(int status)
        => new("api error", new HttpRequestException("inner"))
        {
            StatusCode = (System.Net.HttpStatusCode)status,
            ResponseBody = ""
        };

    /// <summary>包了几层的真实原因照样认得出来。</summary>
    /// <remarks>
    /// HTTP 客户端与 SDK 会把真实原因包上一两层。只看最外层那个的话,
    /// <b>绝大多数可重试的失败都会被判成永久失败</b> —— 自动重试形同虚设。
    /// </remarks>
    [TestMethod]
    public void TheRealCauseIsFoundThroughWrappers()
    {
        Exception wrapped = new InvalidOperationException(
            "streaming failed",
            new AggregateException(new HttpRequestException("connection reset")));

        Assert.IsTrue(TransientFailure.IsTransient(wrapped));
    }

    [TestMethod]
    public void NullIsNotRetryable() => Assert.IsFalse(TransientFailure.IsTransient(null));

    /// <summary>
    /// 故障转移只接两类:瞬时故障与鉴权失败(401/403)。
    /// 参数错换到哪儿都还是那个错 —— 那时该把真原因端给用户,而不是拖着链子挨个撞。
    /// </summary>
    [TestMethod]
    public void FailoverSwitchesForTransientAndAuthFailuresOnly()
    {
        Assert.IsTrue(TransientFailure.IsWorthSwitching(new HttpRequestException("boom")));
        Assert.IsTrue(TransientFailure.IsWorthSwitching(new ClientResultException("unauthorized", Response(401))));
        Assert.IsTrue(TransientFailure.IsWorthSwitching(new ClientResultException("forbidden", Response(403))));

        Assert.IsFalse(TransientFailure.IsWorthSwitching(new ClientResultException("bad request", Response(400))));
        Assert.IsFalse(TransientFailure.IsWorthSwitching(new ClientResultException("not found", Response(404))));
        Assert.IsFalse(TransientFailure.IsWorthSwitching(new InvalidOperationException("bug")));
        Assert.IsFalse(TransientFailure.IsWorthSwitching(null));

        // 真实失败几乎都被 SDK 包过一两层,包着也得认出来
        Assert.IsTrue(TransientFailure.IsWorthSwitching(new InvalidOperationException(
            "streaming failed", new ClientResultException("unauthorized", Response(401)))));
    }

    [TestMethod]
    public void BuiltinOAuthHostMismatchCanSwitchButCannotRetry()
    {
        Exception mismatch = new AiSettingsStore.BuiltinOAuthHostMismatchException();
        Assert.IsTrue(TransientFailure.IsWorthSwitching(mismatch));
        Assert.IsFalse(TransientFailure.IsTransient(mismatch));

        Exception wrapped = new InvalidOperationException("client creation failed", new AggregateException(mismatch));
        Assert.IsTrue(TransientFailure.IsWorthSwitching(wrapped));
        Assert.IsFalse(TransientFailure.IsTransient(wrapped));

        Exception configurationError = new InvalidOperationException("configuration failed",
            new InvalidOperationException("invalid endpoint"));
        Assert.IsFalse(TransientFailure.IsWorthSwitching(configurationError));
        Assert.IsFalse(TransientFailure.IsTransient(configurationError));
    }

    [TestMethod]
    public void ChangedProviderConfigurationCanSwitchButCannotRetry()
    {
        Exception changed = new ProviderConfigurationChangedException();
        Assert.IsTrue(TransientFailure.IsWorthSwitching(changed));
        Assert.IsFalse(TransientFailure.IsTransient(changed));
        Exception wrapped = new InvalidOperationException("client preparation failed", new AggregateException(changed));
        Assert.IsTrue(TransientFailure.IsWorthSwitching(wrapped));
        Assert.IsFalse(TransientFailure.IsTransient(wrapped));
        Exception ordinary = new InvalidOperationException("Provider changed before sending");
        Assert.IsFalse(TransientFailure.IsWorthSwitching(ordinary), "不得按英文消息将普通配置错误放行");
        Assert.IsFalse(TransientFailure.IsTransient(ordinary));
        Assert.IsFalse(TransientFailure.IsWorthSwitching(new OperationCanceledException()));
        Assert.IsFalse(TransientFailure.IsTransient(new OperationCanceledException()));
    }

    [TestMethod]
    [DataRow(401, true)]
    [DataRow(403, true)]
    [DataRow(429, true)]
    [DataRow(400, false)]
    [DataRow(404, false)]
    [DataRow(408, false)]
    [DataRow(500, false)]
    public void KeyFailover_OnlyAuthAndQuotaFailuresRotateKeys(int status, bool expected)
    {
        Exception[] errors =
        [
            new HttpRequestException("http", null, (System.Net.HttpStatusCode)status),
            new ClientResultException("sdk", Response(status)),
            Api(status)
        ];
        foreach (Exception error in errors)
        {
            Assert.AreEqual(expected, TransientFailure.IsApiKeyFailure(error));
            Assert.AreEqual(expected, TransientFailure.IsApiKeyFailure(new InvalidOperationException("wrapped", error)));
        }
    }

    [TestMethod]
    public void KeyFailover_OuterStatusAndConfigurationChangesNeverMasqueradeAsBadKeys()
    {
        var inner = new HttpRequestException("auth", null, System.Net.HttpStatusCode.Unauthorized);
        Assert.IsFalse(TransientFailure.IsApiKeyFailure(new HttpRequestException("parameters", inner, System.Net.HttpStatusCode.BadRequest)));
        Assert.IsFalse(TransientFailure.IsApiKeyFailure(new Anthropic.Exceptions.AnthropicApiException("model", inner)
            { StatusCode = System.Net.HttpStatusCode.NotFound, ResponseBody = "" }));
        Assert.IsTrue(TransientFailure.IsApiKeyFailure(new Anthropic.Exceptions.AnthropicApiException("transport", inner)
            { StatusCode = 0, ResponseBody = "" }));
        Assert.IsFalse(TransientFailure.IsApiKeyFailure(new HttpRequestException("offline")));
        Assert.IsFalse(TransientFailure.IsApiKeyFailure(new TimeoutException()));
        Assert.IsFalse(TransientFailure.IsApiKeyFailure(new HealthProbe.EmptyReplyException()));
        Assert.IsFalse(TransientFailure.IsApiKeyFailure(null));
        Exception changed = new AiSettingsStore.ApiKeySlotChangedException();
        Assert.IsFalse(TransientFailure.IsApiKeyFailure(changed));
        Assert.IsFalse(TransientFailure.IsTransient(changed));
        Assert.IsTrue(TransientFailure.IsWorthSwitching(changed));
        Assert.IsTrue(TransientFailure.IsWorthSwitching(new InvalidOperationException("wrapped", changed)));
    }

    /// <summary>造一个只带状态码的响应。</summary>
    private static StubResponse Response(int status) => new(status);

    private sealed class StubResponse(int status) : PipelineResponse
    {
        public override int Status { get; } = status;

        public override string ReasonPhrase => string.Empty;

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content => BinaryData.FromString("");

        protected override PipelineResponseHeaders HeadersCore { get; } = new StubHeaders();

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Content);

        public override void Dispose() { }

        private sealed class StubHeaders : PipelineResponseHeaders
        {
            public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
                Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();

            public override bool TryGetValue(string name, out string? value)
            {
                value = null;
                return false;
            }

            public override bool TryGetValues(string name, out IEnumerable<string>? values)
            {
                values = null;
                return false;
            }
        }
    }
}
