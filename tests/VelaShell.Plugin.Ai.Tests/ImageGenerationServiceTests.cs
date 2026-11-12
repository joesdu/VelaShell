using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class ImageGenerationServiceTests
{
    private static (HttpListener Listener, ResolvedModel Model) Listen()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var provider = new AiProvider
        {
            BaseUrl = $"http://127.0.0.1:{port}/v1",
            DefaultProtocol = ChatProtocol.OpenAiImages,
            Models = [new AiModelConfig { Model = "image-2" }]
        };
        return (listener, new ResolvedModel(provider, provider.Models[0]));
    }

    [TestMethod]
    public async Task GenerateUsesGenerationsEndpoint()
    {
        (HttpListener? listener, ResolvedModel? model) = Listen();
        using (listener)
        using (var context = new TestPluginContext())
        {
            var store = new AiSettingsStore(context);
            Task<DataContent> task = store.GenerateImageAsync(model, "draw a cat", null, CancellationToken.None);
            HttpListenerContext request = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("/v1/images/generations", request.Request.Url!.AbsolutePath);
            using var reader = new StreamReader(request.Request.InputStream);
            string body = await reader.ReadToEndAsync();
            Assert.Contains("draw a cat", body);
            Assert.Contains("image-2", body);
            byte[] reply = Encoding.UTF8.GetBytes("{\"created\":1,\"data\":[{\"b64_json\":\"AQID\"}]}");
            request.Response.ContentLength64 = reply.Length;
            request.Response.ContentType = "application/json";
            await request.Response.OutputStream.WriteAsync(reply);
            request.Response.Close();
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, (await task).Data.ToArray());
        }
    }

    [TestMethod]
    public async Task EditSendsMultipartAndReturnsImage()
    {
        (HttpListener? listener, ResolvedModel? model) = Listen();
        using (listener)
        using (var context = new TestPluginContext())
        {
            var store = new AiSettingsStore(context);
            await store.SetApiKeyAsync(model.Provider.Id, "secret");
            Task<DataContent> task = store.GenerateImageAsync(model, "change color",
                [new DataContent(new byte[] { 1, 2, 3 }, "image/png"), new DataContent(new byte[] { 4, 5 }, "image/jpeg")], CancellationToken.None);
            HttpListenerContext request = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("/v1/images/edits", request.Request.Url!.AbsolutePath);
            Assert.AreEqual("Bearer secret", request.Request.Headers["Authorization"]);
            Assert.StartsWith("multipart/form-data;", request.Request.ContentType!);
            using var reader = new StreamReader(request.Request.InputStream, Encoding.Latin1);
            string body = await reader.ReadToEndAsync();
            Assert.Contains("image-2", body);
            Assert.Contains("change color", body);
            Assert.Contains("image[]", body);
            Assert.Contains("image/png", body);
            Assert.Contains("image/jpeg", body);
            byte[] reply = Encoding.UTF8.GetBytes("{\"created\":1,\"data\":[{\"b64_json\":\"AQID\"}]}");
            request.Response.ContentType = "application/json";
            request.Response.ContentLength64 = reply.Length;
            await request.Response.OutputStream.WriteAsync(reply);
            request.Response.Close();
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, (await task).Data.ToArray());
        }
    }

    [TestMethod]
    public async Task EditDoesNotRetryAndCancellationPropagates()
    {
        (HttpListener? listener, ResolvedModel? model) = Listen();
        using (listener)
        using (var context = new TestPluginContext())
        {
            var store = new AiSettingsStore(context);
            DataContent[] inputs = [new DataContent(new byte[] { 1 }, "image/png")];
            Task<DataContent> task = store.GenerateImageAsync(model, "edit", inputs, CancellationToken.None);
            HttpListenerContext request = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
            request.Response.StatusCode = 429;
            request.Response.Close();
            await Assert.ThrowsAsync<Exception>(async () => await task.WaitAsync(TimeSpan.FromSeconds(10)));
            Task<HttpListenerContext> second = listener.GetContextAsync();
            Assert.AreNotSame(second, await Task.WhenAny(second, Task.Delay(1200)));
            using var cts = new CancellationTokenSource();
            Task<DataContent> cancelled = store.GenerateImageAsync(model, "edit", inputs, cts.Token);
            request = await second.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelled.WaitAsync(TimeSpan.FromSeconds(10)));
            request.Response.Close();
        }
    }

    [TestMethod]
    public async Task RejectsNonImageAndInvalidInputs()
    {
        (HttpListener? listener, ResolvedModel? model) = Listen();
        using (listener)
        using (var context = new TestPluginContext())
        {
            var store = new AiSettingsStore(context);
            var textProvider = new AiProvider
            {
                BaseUrl = model.BaseUrl,
                DefaultProtocol = ChatProtocol.OpenAiResponses,
                Models = [new AiModelConfig { Model = "text-model" }]
            };
            var textModel = new ResolvedModel(textProvider, textProvider.Models[0]);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.GenerateImageAsync(textModel, "draw", null, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.GenerateImageAsync(model, "edit", [new DataContent(new byte[] { 1 }, "image/gif")], CancellationToken.None));
        }
    }
}
