using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 环回错误端点:每个请求都按给定状态码拒绝。
/// 用来通过真实 HTTP 触发瞬时故障或 401/403 鉴权失败的有限重试与故障转移,
/// 不替代发生在发送请求之前的 OAuth 地址护栏。
/// </summary>
public sealed class ErrorStub : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<bool> _readingBody = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _requestFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _server;
    private readonly Lock _gate = new();
    private int _requests;
    private int _disposed;

    /// <summary>接入配置里填的基地址。</summary>
    public string BaseUrl { get; }

    /// <summary>首个请求已接受并启动完整 body 读取;结果表示发布信号时读取尚未完成,不要求收到首字符。</summary>
    public Task<bool> RequestBodyStartedAsync => _readingBody.Task;

    /// <summary>实际请求的读取或响应发生连接异常;服务端随后仍须继续接收下一请求。</summary>
    public Task<Exception> RequestFailureAsync => _requestFailure.Task;

    /// <summary>到目前为止收到的请求数。</summary>
    public int Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests;
            }
        }
    }

    /// <param name="status">回给每个请求的状态码;默认 401(鉴权失败)。</param>
    public ErrorStub(HttpStatusCode status = HttpStatusCode.Unauthorized)
    {
        // TCP 空闲端口在 Windows 上可能被 HTTP.sys 保留;与 SseStub 一样只重取注册失败的端口。
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

        byte[] body = Encoding.UTF8.GetBytes(
            """{"error":{"message":"invalid api key","type":"invalid_request_error","code":"invalid_api_key"}}""");
        _server = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    try
                    {
                        // 请求体照读再回:不读直接关,客户端那头可能拿到连接层错误而不是 401
                        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                        {
                            // HTTP.sys/StreamReader 可以等待缓冲区或请求体结束,不能等首字符完成才允许 RST。
                            // 先启动真实异步读取再发布阶段信号;GetContext 已成功,读取也已进入 pending。
                            Task<string> readBody = reader.ReadToEndAsync(_shutdown.Token);
                            _readingBody.TrySetResult(!readBody.IsCompleted);
                            await readBody;
                        }
                        lock (_gate)
                        {
                            _requests++;
                        }
                        context.Response.StatusCode = (int)status;
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = body.Length;
                        await context.Response.OutputStream.WriteAsync(body);
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException
                        || ex is OperationCanceledException && _shutdown.IsCancellationRequested)
                    {
                        if (!_shutdown.IsCancellationRequested) _requestFailure.TrySetResult(ex);
                        try { context.Response.Abort(); } catch (ObjectDisposedException) { }
                    }
                }
            }
            catch (Exception) when (_shutdown.IsCancellationRequested)
            {
                // 监听器被 Dispose 掉即收摊。
            }
            catch (Exception ex)
            {
                _readingBody.TrySetException(ex);
                _requestFailure.TrySetException(ex);
                throw;
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _readingBody.TrySetCanceled();
        _requestFailure.TrySetCanceled();
        _listener.Close();
        _server.GetAwaiter().GetResult();
        _shutdown.Dispose();
    }
}
