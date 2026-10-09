using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 一次性的本地 SSE 端点:回一段写死的流式报文就收摊。
/// 用它把"报文长这样 → 界面该显示什么"整条链路真跑一遍(真 SDK、真适配器、真解析),
/// 比拿构造好的 <c>ChatResponseUpdate</c> 断言可信得多 —— 各家协议的坑都在解析这一段。
/// </summary>
public sealed class SseStub : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _server;
    private readonly List<Task> _heldReplies = [];
    private readonly TaskCompletionSource _firstChunkFlushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private readonly List<string> _bodies = [];
    private readonly List<string?> _authorizations = [];
    private readonly List<string?> _accountIds = [];
    private int _disposed;

    /// <summary>接入配置里填的基地址。</summary>
    public string BaseUrl { get; }

    /// <summary>收到的请求体(等到真有请求打进来为止)—— 用来断言"发出去的到底长什么样"。</summary>
    public Task<string> RequestBodyAsync => _request.Task;

    /// <summary>首个分块 SSE 事件已写出并 flush;不代表 SDK 已解析,测试仍须观察真实消费者。</summary>
    public Task FirstChunkFlushedAsync => _firstChunkFlushed.Task;

    /// <summary>
    /// 到目前为止收到的<b>每一次</b>请求体(按先后)。一轮里不止一次请求时要看的是这一份 ——
    /// 比如"排队的那句有没有真的进到下一次请求里"。
    /// </summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _bodies];
            }
        }
    }

    /// <summary>每次请求的 Authorization 头,与 <see cref="Requests" /> 同序。</summary>
    public IReadOnlyList<string?> Authorizations
    {
        get
        {
            lock (_gate)
            {
                return [.. _authorizations];
            }
        }
    }

    /// <summary>每次请求的订阅账号头,与 <see cref="Requests" /> 同序。</summary>
    public IReadOnlyList<string?> AccountIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _accountIds];
            }
        }
    }

    /// <param name="sse">流式请求(<c>"stream":true</c>)的回应。</param>
    /// <param name="delay">回应前先等一会儿,用来观察"处理中"的界面状态。</param>
    /// <param name="jsonContent">
    /// 非流式请求的回复正文。插件一轮里不止一种请求 —— 聊天是流式的,
    /// 而"给几条后续提问"那一问是<b>非流式</b>的,拿 SSE 去回它会解析失败。
    /// </param>
    /// <param name="chunkDelay">
    /// 逐事件下发的间隔(&gt;0 时按空行切开、分块发送)。整段一次性写出去的话,
    /// 界面会在同一拍里收到全部增量,"流式渲染到底有没有在流"就测不出来了。
    /// </param>
    /// <param name="hold">
    /// 收下请求后<b>挂住不回</b>,直到测试调用 <see cref="Release" />。要观察"这一轮还在跑"
    /// 时的界面,用它而不是 <paramref name="delay" />:延时是在赌"我这几句断言跑得比它快",
    /// CI 上机器一慢就赌输 —— 轮次早收尾了,排队芯片也就跟着没了(macOS runner 上
    /// <c>ClickingAQueuedChip_TakesTheMessageBack</c> 正是这么挂的)。挂住则与机器快慢无关。
    /// </param>
    /// <param name="abortAfterFirstChunk">flush 首事件后等 Release 再断开;消费者确实看到首事件后才可放行。</param>
    /// <param name="holdOnlyFirst">只挂住首次非流式请求,继续接收并立即回应后续请求。</param>
    /// <param name="firstJsonContent">首次挂住的非流式请求放行后回的内容;其余请求用 jsonContent。</param>
    /// <param name="holdFirstNonStream">只挂住首次非流式摘要请求,此前的流式消息正常回复。</param>
    /// <param name="nonStreamingReply">按非流式请求体选择响应正文,用于模拟额度耗尽。</param>
    /// <param name="holdAfterFirstChunk">flush 首事件后等待 Release;发送信号不等于客户端已消费。</param>
    /// <param name="holdFirstStream">只挂住首次流式请求,继续接收并立即回应后续请求。</param>
    /// <param name="firstStreamContent">若非 null,首次流式请求改回标准文本 SSE;不影响后续流及非流式请求。</param>
    /// <param name="skipFirstStreams">首流覆写或挂起前跳过的流式请求数；用于同一端点先完成聊天、后等待探活。</param>
    public SseStub(
        string sse,
        TimeSpan delay = default,
        string? jsonContent = null,
        TimeSpan chunkDelay = default,
        bool hold = false,
        bool abortAfterFirstChunk = false,
        bool holdOnlyFirst = false,
        string? firstJsonContent = null,
        bool holdFirstNonStream = false,
        Func<string, string>? nonStreamingReply = null,
        bool holdAfterFirstChunk = false,
        bool holdFirstStream = false,
        string? firstStreamContent = null,
        int skipFirstStreams = 0)
    {
        // TCP 选出的空闲端口可能落在 Windows HTTP.sys 保留范围:对 HttpListener 却是 Access denied。
        // 只在端口注册失败时重新取一次,其它故障照常冒出;全套并行测试不能随机红。
        for (int attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                BaseUrl = $"http://127.0.0.1:{port}";
                break;
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

        string normalised = sse.ReplaceLineEndings("\n");
        byte[] streamed = Encoding.UTF8.GetBytes(normalised);
        byte[] plain = Encoding.UTF8.GetBytes(CompletionJson(jsonContent ?? ""));
        byte[] firstStreamed = firstStreamContent is null ? streamed : Encoding.UTF8.GetBytes(
            "data: " + JsonSerializer.Serialize(new
            {
                id = "1", @object = "chat.completion.chunk", created = 1, model = "m",
                choices = new[] { new { index = 0, delta = new { content = firstStreamContent }, finish_reason = "stop" } }
            }) + "\n\ndata: [DONE]\n\n");
        byte[]? firstPlain = holdOnlyFirst ? Encoding.UTF8.GetBytes(CompletionJson(firstJsonContent ?? "")) : null;
        byte[][] chunks =
        [
            .. normalised.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                         .Select(part => Encoding.UTF8.GetBytes(part + "\n\n"))
        ];
        bool heldNonStream = false;
        int receivedStreams = 0;
        _server = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    try
                    {
                    string body;
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        body = await reader.ReadToEndAsync(_shutdown.Token);
                    }
                    lock (_gate)
                    {
                        _bodies.Add(body);
                        _authorizations.Add(context.Request.Headers["Authorization"]);
                        _accountIds.Add(context.Request.Headers["chatgpt-account-id"]);
                    }
                    _request.TrySetResult(body);
                    bool wantsStream = body.Contains("\"stream\":true", StringComparison.Ordinal);
                    bool firstStream = wantsStream && receivedStreams++ == skipFirstStreams;
                    if (firstStream && holdFirstStream)
                    {
                        Task reply = Task.Run(async () =>
                        {
                            try
                            {
                                await _released.Task;
                                context.Response.ContentType = "text/event-stream";
                                context.Response.ContentLength64 = firstStreamed.Length;
                                await context.Response.OutputStream.WriteAsync(firstStreamed);
                                context.Response.Close();
                            }
                            catch (Exception)
                            {
                                // 监听器被 Dispose 时挂起的首个流式请求无需再回复
                            }
                        });
                        lock (_gate) { _heldReplies.Add(reply); }
                        continue;
                    }
                    if (!wantsStream && !heldNonStream && (holdOnlyFirst || holdFirstNonStream))
                    {
                        heldNonStream = true;
                        Task reply = Task.Run(async () =>
                        {
                            try
                            {
                                await _released.Task;
                                context.Response.ContentType = "application/json";
                                byte[] heldReply = firstPlain ?? plain;
                                context.Response.ContentLength64 = heldReply.Length;
                                await context.Response.OutputStream.WriteAsync(heldReply);
                                context.Response.Close();
                            }
                            catch (Exception)
                            {
                                // 监听器被 Dispose 时挂起的首个请求无需再回复
                            }
                        });
                        lock (_gate) { _heldReplies.Add(reply); }
                        continue;
                    }
                    if (hold)
                    {
                        // 放行是一次性的:放开之后本轮与后续请求都照常走
                        // (排队那句作为下一轮发出去时,不该再被挡一次)。
                        await _released.Task;
                    }
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _shutdown.Token);
                    }
                    if (!wantsStream)
                    {
                        byte[] reply = nonStreamingReply is null
                            ? plain
                            : Encoding.UTF8.GetBytes(CompletionJson(nonStreamingReply(body)));
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = reply.Length;
                        await context.Response.OutputStream.WriteAsync(reply);
                        context.Response.Close();
                        continue;
                    }
                    context.Response.ContentType = "text/event-stream";
                    if ((firstStream && firstStreamContent is not null) || (chunkDelay <= TimeSpan.Zero && !holdAfterFirstChunk && !abortAfterFirstChunk))
                    {
                        byte[] reply = firstStream ? firstStreamed : streamed;
                        context.Response.ContentLength64 = reply.Length;
                        await context.Response.OutputStream.WriteAsync(reply);
                        context.Response.Close();
                        continue;
                    }
                    // 逐事件下发:不能给 ContentLength,得走分块传输,而且每块都要 Flush,
                    // 否则全被缓冲到最后一起吐出去,等于没有分块。
                    context.Response.SendChunked = true;
                    bool aborted = false;
                    bool firstChunk = true;
                    foreach (byte[] chunk in chunks)
                    {
                        await context.Response.OutputStream.WriteAsync(chunk);
                        await context.Response.OutputStream.FlushAsync();
                        if (firstChunk)
                        {
                            firstChunk = false;
                            _firstChunkFlushed.TrySetResult();
                            if (holdAfterFirstChunk || abortAfterFirstChunk)
                            {
                                await _released.Task;
                            }
                            if (abortAfterFirstChunk)
                            {
                                context.Response.Abort();
                                aborted = true;
                                break;
                            }
                        }
                        if (chunkDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(chunkDelay, _shutdown.Token);
                        }
                    }
                    if (!aborted)
                    {
                        context.Response.Close();
                    }
                    }
                    catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException
                        || ex is OperationCanceledException && _shutdown.IsCancellationRequested)
                    {
                        // 单个客户端取消或断开不能让后续会话的端点永久停止接收。
                        try { context.Response.Abort(); } catch (ObjectDisposedException) { }
                    }
                }
            }
            catch (Exception) when (_shutdown.IsCancellationRequested)
            {
                // 所有挂起与监听任务在 Dispose 时一起收摊。
            }
            catch (Exception ex)
            {
                _request.TrySetException(ex);
                _firstChunkFlushed.TrySetException(ex);
                throw;
            }
        });
    }

    /// <summary>
    /// 放行挂住的回应;若设置首事件断流,则在此放行后中断传输。
    /// 多次调用无害;没挂住时调用也无害。
    /// </summary>
    public void Release() => _released.TrySetResult();

    /// <summary>一份最小的非流式 Chat Completions 回应。</summary>
    private static string CompletionJson(string content)
        => "{\"id\":\"1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"m\","
           + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
           + JsonSerializer.Serialize(content)
           + "},\"finish_reason\":\"stop\"}],"
           + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _request.TrySetCanceled();
        _firstChunkFlushed.TrySetCanceled();
        _released.TrySetCanceled();
        _listener.Close();
        _server.GetAwaiter().GetResult();
        Task[] replies;
        lock (_gate) { replies = [.. _heldReplies]; }
        Task.WhenAll(replies).GetAwaiter().GetResult();
        _shutdown.Dispose();
    }
}

/// <summary>用 FIN 截断 chunked SSE:完整首事件先到达,随后缺失终结块触发真实 HTTP 断流。</summary>
internal sealed class TruncatedSseStub : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _server;
    private int _requests;
    private int _disposed;

    public string BaseUrl { get; }
    public int Requests => Volatile.Read(ref _requests);

    public TruncatedSseStub(string sse)
    {
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        string first = sse.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)[0] + "\n\n";
        byte[] chunk = Encoding.UTF8.GetBytes(first);
        byte[] responseHeader = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
            + chunk.Length.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "\r\n");
        byte[] chunkEnd = "\r\n"u8.ToArray();
        _server = Task.Run(async () =>
        {
            try
            {
                byte[] buffer = new byte[4096];
                while (true)
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                    try
                    {
                        NetworkStream stream = client.GetStream();
                        using var headers = new MemoryStream();
                        while (true)
                        {
                            await stream.ReadExactlyAsync(buffer.AsMemory(0, 1), _shutdown.Token);
                            headers.WriteByte(buffer[0]);
                            if (headers.Length >= 4 && headers.GetBuffer().AsSpan((int)headers.Length - 4, 4).SequenceEqual("\r\n\r\n"u8))
                            {
                                break;
                            }
                        }
                        string header = Encoding.ASCII.GetString(headers.GetBuffer(), 0, (int)headers.Length);
                        string length = header.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                        int remaining = int.Parse(length["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                        while (remaining > 0)
                        {
                            int count = Math.Min(remaining, buffer.Length);
                            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), _shutdown.Token);
                            remaining -= count;
                        }
                        Interlocked.Increment(ref _requests);
                        await stream.WriteAsync(responseHeader, _shutdown.Token);
                        await stream.WriteAsync(chunk, _shutdown.Token);
                        await stream.WriteAsync(chunkEnd, _shutdown.Token);
                        // 不发送 0\r\n\r\n。FIN 位于完整首 HTTP chunk 之后,不撤销已发送的数据。
                        client.Client.Shutdown(SocketShutdown.Send);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                    {
                        // 单个客户端断连不能使其它真实请求永远等不到回应。
                    }
                }
            }
            catch (Exception) when (_shutdown.IsCancellationRequested)
            {
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _listener.Stop();
        _server.GetAwaiter().GetResult();
        _shutdown.Dispose();
    }
}
