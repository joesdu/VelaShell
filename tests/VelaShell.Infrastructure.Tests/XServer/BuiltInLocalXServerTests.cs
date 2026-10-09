using System.Net;
using System.Net.Sockets;
using System.Text;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.XServer;
using VelaShell.Infrastructure.XServer;
using VelaShell.Ssh.Forwarding;
using VelaShell.XServer;

namespace VelaShell.Infrastructure.Tests.XServer;

/// <summary>
/// 内置 X 服务端:生命周期(宿主的附着 / 脱离)、SSH 转发拿到的连接器真能接进服务端、什么情况下不接管。
/// 显示号探测注入,监听的是 :10 起的号,避开本机常见的 :0。
/// </summary>
[TestClass]
[TestCategory("XServer")]
public class BuiltInLocalXServerTests
{
    private static ISettingsService Settings(XServerOptions options)
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { XServer = options });
        return settings;
    }

    /// <summary>读一份 .Xauthority(必须每一条都认得全)。</summary>
    private static IReadOnlyList<XAuthorityEntry> Decode(string path) =>
        XAuthority.TryDecode(File.ReadAllBytes(path), out IReadOnlyList<XAuthorityEntry>? entries) ? entries : throw new AssertFailedException("解不全");

    /// <summary>0–9 当作被占用,自动模式挑到 :10。</summary>
    private static Task<bool> LowDisplaysBusy(int display, CancellationToken _) => Task.FromResult(display < 10);

    private static BuiltInLocalXServer Create(XServerOptions options, RecordingHost? host, bool otherDisplay = false) =>
        new(Settings(options), () => host, LowDisplaysBusy, _ => Task.FromResult(otherDisplay));

    [TestMethod]
    public async Task Start_AttachesHost_ThenStop_DetachesIt()
    {
        RecordingHost host = new();
        await using BuiltInLocalXServer server = Create(new XServerOptions(), host);
        List<XServerState> states = [];
        server.StateChanged += (_, _) => states.Add(server.State);

        XServerStartResult result = await server.StartAsync();

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(XServerState.Running, server.State);
        Assert.AreEqual(10, server.DisplayNumber);
        Assert.AreEqual("localhost:10.0", server.Display);
        Assert.IsNotNull(host.Attached, "启动时先把服务端交给宿主");

        await server.StopAsync();

        Assert.AreEqual(XServerState.Stopped, server.State);
        Assert.AreEqual(1, host.Detaches);
        Assert.AreSequenceEqual([XServerState.Starting, XServerState.Running, XServerState.Stopped], states);
    }

    /// <summary>设置里选的键盘布局在附着之前交给宿主(空串 = 跟随系统)。</summary>
    [TestMethod]
    [DataRow("de")]
    [DataRow("")]
    public async Task Start_HandsTheChosenKeyboardLayoutToTheHostBeforeAttaching(string layout)
    {
        RecordingHost host = new();
        await using BuiltInLocalXServer server = Create(new XServerOptions { KeyboardLayout = layout }, host);

        Assert.IsTrue((await server.StartAsync()).Success);

        Assert.AreEqual(layout, host.LayoutAtAttach);
    }

    [TestMethod]
    public async Task Start_WithoutHost_FailsWithoutListening()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions(), host: null);

        XServerStartResult result = await server.StartAsync();

        Assert.IsFalse(result.Success);
        Assert.IsFalse(string.IsNullOrEmpty(result.Error));
        Assert.AreEqual(XServerState.Stopped, server.State);
    }

    [TestMethod]
    public async Task Start_ConfiguredDisplayInUse_Fails()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions { DisplayNumber = 3 }, new RecordingHost());

        XServerStartResult result = await server.StartAsync();

        Assert.IsFalse(result.Success);
        Assert.Contains("3", result.Error);
    }

    /// <summary>SSH 拿到的连接器接进的是服务端本身:走完 X 的连接建立,拿到 Success。</summary>
    [TestMethod]
    public async Task ResolveForwarding_AutoStarts_AndConnectorReachesTheServer()
    {
        RecordingHost host = new();
        await using BuiltInLocalXServer server = Create(new XServerOptions(), host);

        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

        Assert.AreEqual("localhost:10.0", resolution.Display);
        Assert.IsNotNull(resolution.Connector);
        await using Stream stream = await resolution.Connector(new XServerChannelSource("user@host:22"), CancellationToken.None);
        await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        await stream.FlushAsync();
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, head[0], "Success —— 经连接器来的连接按本机连接放行");
    }

    /// <summary>
    /// 没勾「受信任」的会话(ssh -X)经连接器连进来是非受信客户端:服务端按 SECURITY 的语义对它藏起 XTEST;
    /// 受信的会话照常看得见。原先非受信的会话根本开不起转发。
    /// </summary>
    [TestMethod]
    public async Task Connector_UntrustedSource_ConnectsAsAnUntrustedClient()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions(), new RecordingHost());
        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

        await using Stream untrusted = await resolution.Connector!(new XServerChannelSource("user@host:22", Trusted: false), CancellationToken.None);
        await using Stream trusted = await resolution.Connector!(new XServerChannelSource("user@host:22"), CancellationToken.None);

        CollectionAssert.DoesNotContain(await ListExtensionsAsync(untrusted), "XTEST");
        CollectionAssert.Contains(await ListExtensionsAsync(trusted), "XTEST");

        static async Task<List<string>> ListExtensionsAsync(Stream stream)
        {
            await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            await stream.FlushAsync();
            byte[] head = new byte[8];
            await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, head[0]);
            await stream.ReadExactlyAsync(new byte[BitConverter.ToUInt16(head, 6) * 4]);
            await stream.WriteAsync(new byte[] { 99, 0, 1, 0 });   // ListExtensions
            await stream.FlushAsync();
            byte[] reply = new byte[32];
            await stream.ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            byte[] body = new byte[BitConverter.ToUInt32(reply, 4) * 4];
            await stream.ReadExactlyAsync(body);
            List<string> names = [];
            for (int i = 0, at = 0; i < reply[1]; i++, at += 1 + body[at])
            {
                names.Add(Encoding.Latin1.GetString(body, at + 1, body[at]));
            }
            return names;
        }
    }

    /// <summary>
    /// 「每个 SSH 会话一个显示」开着:同一个会话的通道进同一个服务端,不同会话各进各的,没带会话的(本机)进共用的那个;
    /// 每个服务端各配一个宿主;会话断开时它的服务端收掉、宿主脱离;停服时全部收掉。
    /// </summary>
    [TestMethod]
    public async Task DisplayPerSession_EachSessionGetsItsOwnServer_ClosedWhenTheSessionEnds()
    {
        List<RecordingHost> hosts = [];
        await using BuiltInLocalXServer server = new(Settings(new XServerOptions { DisplayPerSession = true }), () =>
        {
            RecordingHost created = new();
            lock (hosts)
            {
                hosts.Add(created);
            }
            return created;
        }, LowDisplaysBusy, _ => Task.FromResult(false));
        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();
        using CancellationTokenSource aliceEnded = new(), bobEnded = new();
        object alice = new(), bob = new();

        await using Stream a1 = await resolution.Connector!(new("alice@a:22", Session: alice, SessionEnded: aliceEnded.Token), CancellationToken.None);
        await using Stream a2 = await resolution.Connector!(new("alice@a:22", Session: alice, SessionEnded: aliceEnded.Token), CancellationToken.None);
        await using Stream b1 = await resolution.Connector!(new("bob@b:22", Session: bob, SessionEnded: bobEnded.Token), CancellationToken.None);
        await using Stream shared = await resolution.Connector!(new("local"), CancellationToken.None);
        foreach (Stream stream in (Stream[])[a1, a2, b1, shared])
        {
            Assert.AreEqual(1, await HandshakeAsync(stream));
        }

        IReadOnlyList<BuiltInLocalXServer.XServerInstance> instances = server.Instances;
        Assert.HasCount(3, instances, "共用的一个 + 两个会话各一个");
        Assert.IsNull(instances[0].Label);
        X11Server aliceServer = instances.Single(i => i.Label == "alice@a:22").Server;
        Assert.HasCount(2, await aliceServer.GetClientsAsync(), "同一个会话的两条通道进同一个服务端");
        Assert.HasCount(1, await instances.Single(i => i.Label == "bob@b:22").Server.GetClientsAsync());
        Assert.HasCount(1, await instances[0].Server.GetClientsAsync(), "没带会话的进共用的那个");
        Assert.HasCount(3, hosts, "每个服务端一个宿主");
        Assert.AreEqual(4, await server.CountConnectedClientsAsync(), "按会话分出来的显示上的一并数上");

        await aliceEnded.CancelAsync();
        for (int i = 0; i < 100 && server.Instances.Count != 2; i++)
        {
            await Task.Delay(20);
        }
        Assert.HasCount(2, server.Instances, "会话断开:它的显示收掉");
        Assert.AreEqual(1, hosts.Single(h => ReferenceEquals(h.Attached, aliceServer)).Detaches);

        await server.StopAsync();
        Assert.IsTrue(hosts.All(h => h.Detaches == 1), "停服时全部收掉");
        Assert.IsEmpty(server.Instances);
    }

    /// <summary>设置没开(默认):带着会话的通道照旧进共用的显示,不另建服务端。</summary>
    [TestMethod]
    public async Task DisplayPerSession_Off_SessionsShareTheDisplay()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions(), new RecordingHost());
        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();
        await using Stream a = await resolution.Connector!(new("alice@a:22", Session: new object()), CancellationToken.None);
        await using Stream b = await resolution.Connector!(new("bob@b:22", Session: new object()), CancellationToken.None);
        Assert.AreEqual(1, await HandshakeAsync(a));
        Assert.AreEqual(1, await HandshakeAsync(b));

        Assert.HasCount(1, server.Instances);
        Assert.HasCount(2, await server.Instances[0].Server.GetClientsAsync());
    }

    /// <summary>停之前数得出连着几个 X 程序(标题栏按钮据此确认「会断开 N 个程序」);没在运行时为 0。</summary>
    [TestMethod]
    public async Task CountConnectedClients_CountsConnections()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions(), new RecordingHost());
        Assert.AreEqual(0, await server.CountConnectedClientsAsync(), "没在运行");
        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();
        await using Stream first = await resolution.Connector!(new XServerChannelSource("user@host:22"), CancellationToken.None);
        await using Stream second = await resolution.Connector!(new XServerChannelSource("user@host:22"), CancellationToken.None);
        Assert.AreEqual(1, await HandshakeAsync(first));
        Assert.AreEqual(1, await HandshakeAsync(second));
        Assert.AreEqual(2, await server.CountConnectedClientsAsync());

        await server.StopAsync();
        Assert.AreEqual(0, await server.CountConnectedClientsAsync());
    }

    /// <summary>
    /// 标题栏 X Server 浮层的程序清单(F3):经 SSH 连接器来的程序带着会话的来历;按键断开它;停了再开之后旧键不碰新服务端上同编号的程序。
    /// </summary>
    [TestMethod]
    public async Task Clients_CarryTheSessionSource_DisconnectByKey_AndOldKeysExpireAfterRestart()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions(), new RecordingHost());
        Assert.IsTrue(server.CanManageClients);
        Assert.IsEmpty(await server.GetClientsAsync(), "没在运行");
        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();
        await using Stream stream = await resolution.Connector!(new XServerChannelSource("user@host:22"), CancellationToken.None);
        Assert.AreEqual(1, await HandshakeAsync(stream));

        XServerClient client = (await server.GetClientsAsync()).Single();
        Assert.AreEqual(("user@host:22", 0, false, false, ""), (client.Source, client.Windows, client.Retained, client.HoldsServerGrab, client.Name));

        server.DisconnectClient("not-a-key");
        Assert.HasCount(1, await server.GetClientsAsync(), "认不出的键什么也不做");
        server.DisconnectClient(client.Key);
        Assert.IsEmpty(await server.GetClientsAsync());

        // 停了再开:新服务端上第一个程序的编号与刚才那个相同,旧键不能把它断开。
        await server.StopAsync();
        Assert.IsTrue((await server.StartAsync()).Success);
        await using Stream again = await resolution.Connector!(new XServerChannelSource("user@host:22"), CancellationToken.None);
        Assert.AreEqual(1, await HandshakeAsync(again));
        XServerClient fresh = (await server.GetClientsAsync()).Single();
        Assert.AreEqual(client.Id, fresh.Id, "同一个编号");
        server.DisconnectClient(client.Key);
        Assert.HasCount(1, await server.GetClientsAsync(), "上一次运行的键过期了");
    }

    /// <summary>窗口模式里多窗口与「无根」以外的几种,内置引擎以单窗口模式(F13)起,模式在附着之前交给宿主。</summary>
    [TestMethod]
    public async Task WindowModes_OtherThanMultiWindowAndRootless_StartTheEngineInOneWindowMode()
    {
        foreach ((string mode, bool rootful) in new[]
        {
            (XServerWindowModes.MultiWindow, false), (XServerWindowModes.Rootless, false),
            (XServerWindowModes.Windowed, true), (XServerWindowModes.NoDecoration, true), (XServerWindowModes.Fullscreen, true),
        })
        {
            RecordingHost host = new();
            await using BuiltInLocalXServer server = Create(new XServerOptions { WindowMode = mode }, host);
            await server.ResolveForwardingDisplayAsync();
            Assert.AreEqual(mode, host.WindowMode, "窗口模式在附着之前交给宿主");
            Assert.AreEqual(rootful, host.Attached!.Screen is not null, mode);
        }
    }

    /// <summary>
    /// 宿主转来「GrabServer 抓得太久」:查出是哪个程序、带着断开它用的键转给界面(F3);查的时候已经放开了就不报。
    /// </summary>
    [TestMethod]
    public async Task GrabStall_IsReportedWithTheHoldersKey_OnlyWhileItStillHoldsTheGrab()
    {
        RecordingHost host = new();
        await using BuiltInLocalXServer server = Create(new XServerOptions(), host);
        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();
        await using Stream stream = await resolution.Connector!(new XServerChannelSource("user@stuck:22"), CancellationToken.None);
        Assert.AreEqual(1, await HandshakeAsync(stream));
        XServerClient client = (await server.GetClientsAsync()).Single();
        TaskCompletionSource<XServerGrabStallNotice> reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ServerGrabStalled += (_, notice) => reported.TrySetResult(notice);

        host.RaiseGrabStall(new XServerGrabStall(client.Id, "user@stuck:22", TimeSpan.FromSeconds(10), 3));
        await Task.Delay(200);
        Assert.IsFalse(reported.Task.IsCompleted, "它没抓着:不报");

        await stream.WriteAsync(new byte[] { 36, 0, 1, 0 });   // GrabServer
        await stream.FlushAsync();
        for (int i = 0; i < 100 && !(await server.GetClientsAsync()).Single().HoldsServerGrab; i++)
        {
            await Task.Delay(20);
        }
        host.RaiseGrabStall(new XServerGrabStall(client.Id, "user@stuck:22", TimeSpan.FromSeconds(10), 3));
        XServerGrabStallNotice notice = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual((client.Key, client.Id, "user@stuck:22", TimeSpan.FromSeconds(10)), (notice.ClientKey, notice.Id, notice.Source, notice.Held));

        // 停下之后宿主的事件不再接到这里。
        await server.StopAsync();
        Assert.AreEqual(0, host.GrabStallSubscribers);
    }

    /// <summary>
    /// 回归:SSH 会话比服务端活得久。标题栏上停掉再开之后,会话早先拿到的连接器要接进新的服务端 ——
    /// 以前它记住的是旧实例,每条 x11 通道都接进已释放的服务端,远端只看到 Failed to open display。
    /// </summary>
    [TestMethod]
    public async Task Connector_AfterRestart_ReachesTheNewServer_AndWhileStopped_Throws()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions(), new RecordingHost());
        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();
        Assert.IsNotNull(resolution.Connector);

        await server.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await resolution.Connector(new XServerChannelSource("user@host:22"), CancellationToken.None));

        Assert.IsTrue((await server.StartAsync()).Success);
        await using Stream stream = await resolution.Connector(new XServerChannelSource("user@host:22"), CancellationToken.None);
        await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        await stream.FlushAsync();
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, head[0], "重启之后旧连接器接进的是新服务端");
    }

    /// <summary>X 连接建立:可以带授权(MIT-MAGIC-COOKIE-1),返回回复的第一个字节(1 = Success,0 = Failed)。</summary>
    private static async Task<byte> HandshakeAsync(Stream stream, byte[]? cookie = null)
    {
        byte[] name = cookie is null ? [] : Encoding.ASCII.GetBytes("MIT-MAGIC-COOKIE-1");
        byte[] data = cookie ?? [];
        List<byte> hello = [(byte)'l', 0, 11, 0, 0, 0, (byte)name.Length, 0, (byte)data.Length, 0, 0, 0];
        hello.AddRange(name);
        hello.AddRange(new byte[((name.Length + 3) & ~3) - name.Length]);
        hello.AddRange(data);
        hello.AddRange(new byte[((data.Length + 3) & ~3) - data.Length]);
        await stream.WriteAsync(hello.ToArray());
        await stream.FlushAsync();
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return head[0];
    }

    /// <summary>
    /// 每次启动生成 cookie:环回 TCP 不带就拒(本机别的进程、别的用户都连得到那个端口),带上就放行;
    /// cookie 写进 .Xauthority 给本机 X 程序用,停下时撤出;SSH 的连接器不要 cookie。
    /// </summary>
    [TestMethod]
    public async Task Start_RequiresTheCookieOnTcp_PublishesItToXauthority_AndRetractsItOnStop()
    {
        string xauthority = Path.Combine(Path.GetTempPath(), $"vx-xauth-{Guid.NewGuid():N}");
        try
        {
            await using BuiltInLocalXServer server = new(Settings(new XServerOptions()), () => new RecordingHost(), LowDisplaysBusy,
                _ => Task.FromResult(false), xauthority);
            Assert.IsTrue((await server.StartAsync()).Success);

            XAuthorityEntry entry = Decode(xauthority).Single();
            Assert.AreEqual(XAuthority.FamilyLocal, entry.Family);
            Assert.AreEqual(Dns.GetHostName(), Encoding.ASCII.GetString(entry.Address.Span));
            Assert.AreEqual("10", entry.DisplayNumber);
            Assert.AreEqual(16, entry.Data.Length);

            using (TcpClient anonymous = new())
            {
                await anonymous.ConnectAsync(IPAddress.Loopback, 6010);
                Assert.AreEqual(0, await HandshakeAsync(anonymous.GetStream()), "环回 TCP 不带 cookie:Failed");
            }
            using (TcpClient authorized = new())
            {
                await authorized.ConnectAsync(IPAddress.Loopback, 6010);
                Assert.AreEqual(1, await HandshakeAsync(authorized.GetStream(), entry.Data.ToArray()), "带上 .Xauthority 里的 cookie:Success");
            }
            XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();
            await using (Stream channel = await resolution.Connector!(new XServerChannelSource("user@host:22"), CancellationToken.None))
            {
                Assert.AreEqual(1, await HandshakeAsync(channel), "SSH 的连接器:转发层核对过假 cookie,不再要");
            }

            await server.StopAsync();
            Assert.IsEmpty(Decode(xauthority), "停下时撤出");
        }
        finally
        {
            File.Delete(xauthority);
        }
    }

    /// <summary>
    /// 启动途中任何异常都收尾:原先只接 SocketException、取消与 InvalidOperationException,别的(宿主附着时抛的、库的参数校验)
    /// 一路抛出去,服务端不释放、状态卡在「启动中」。
    /// </summary>
    [TestMethod]
    public async Task Start_AnyFailure_DetachesTheHost_StopsCleanly_AndCanStartAgain()
    {
        RecordingHost host = new() { FailAttachWith = new NotSupportedException("the host is broken") };
        await using BuiltInLocalXServer server = Create(new XServerOptions(), host);

        XServerStartResult failed = await server.StartAsync();

        Assert.IsFalse(failed.Success);
        Assert.Contains("the host is broken", failed.Error ?? "");
        Assert.AreEqual(XServerState.Stopped, server.State, "不卡在 Starting");
        Assert.AreEqual(1, host.Detaches);

        host.FailAttachWith = null;
        Assert.IsTrue((await server.StartAsync()).Success, "再点一次就开得起来");
        await server.StopAsync();
    }

    /// <summary>
    /// 自动选号时,探测说空着的号在开起来那一刻被占了(探测与绑定之间别的程序抢先了,或者别的服务端持着 /tmp/.X{N}-lock):
    /// 换下一个空闲的号再试,原先直接报「显示号被占用」。
    /// </summary>
    [TestMethod]
    public async Task Start_AutomaticDisplayTakenBetweenProbeAndBind_TriesTheNextOne()
    {
        using TcpListener squatter = new(IPAddress.Loopback, 6010);   // 探测(注入的)看不见它
        squatter.Start();
        RecordingHost host = new();
        await using BuiltInLocalXServer server = Create(new XServerOptions(), host);
        List<XServerState> states = [];
        server.StateChanged += (_, _) => states.Add(server.State);

        XServerStartResult result = await server.StartAsync();

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(11, server.DisplayNumber, ":10 开不起来,换到 :11");
        Assert.AreEqual(XServerState.Running, states[^1]);
        Assert.DoesNotContain(XServerState.Stopped, states, "换号期间一直是 Starting");
        await server.StopAsync();
    }

    /// <summary>
    /// macOS 换了网络主机名常跟着变,Xlib 按连接那一刻的主机名在 .Xauthority 里找:主机名变了就按新名字重登、撤掉旧的那一条
    /// (原先一直是启动时的名字,之后本机 X 程序一律被拒)。
    /// </summary>
    [TestMethod]
    public async Task HostNameChange_RegistersTheCookieUnderTheNewName_AndStopRetractsIt()
    {
        string xauthority = Path.Combine(Path.GetTempPath(), $"vx-xauth-{Guid.NewGuid():N}");
        string hostName = "box-a";
        try
        {
            await using BuiltInLocalXServer server = new(Settings(new XServerOptions()), () => new RecordingHost(), LowDisplaysBusy,
                _ => Task.FromResult(false), xauthority, () => hostName);
            Assert.IsTrue((await server.StartAsync()).Success);
            Assert.AreEqual("box-a", Encoding.ASCII.GetString(Decode(xauthority).Single().Address.Span));

            await server.RepublishCookieAsync();   // 主机名没变:什么也不做
            hostName = "box-b";
            await server.RepublishCookieAsync();
            XAuthorityEntry entry = Decode(xauthority).Single();
            Assert.AreEqual("box-b", Encoding.ASCII.GetString(entry.Address.Span), "按新名字登记,旧的那条撤掉");
            Assert.AreEqual("10", entry.DisplayNumber);

            await server.StopAsync();
            Assert.IsEmpty(Decode(xauthority), "停下时撤出的是新名字的那一条");
        }
        finally
        {
            File.Delete(xauthority);
        }
    }

    [TestMethod]
    public async Task ResolveForwarding_AutoStartOff_DoesNotTakeOver()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions { AutoStartForX11Forwarding = false }, new RecordingHost());

        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

        Assert.IsNull(resolution.Display);
        Assert.IsNull(resolution.Connector);
        Assert.AreEqual(XServerState.Stopped, server.State);
    }

    /// <summary>本机已经有别的 X 显示在用(Windows 上 :0 有人听,其它平台设了 DISPLAY):不插手。</summary>
    [TestMethod]
    public async Task ResolveForwarding_OtherDisplayInUse_DoesNotTakeOver()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions(), new RecordingHost(), otherDisplay: true);

        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

        Assert.IsNull(resolution.Display);
        Assert.AreEqual(XServerState.Stopped, server.State);
    }

    [TestMethod]
    public async Task ResolveForwarding_WhenRunning_ReturnsDisplayAndConnectorEvenIfAutoStartOff()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions { AutoStartForX11Forwarding = false }, new RecordingHost());
        Assert.IsTrue((await server.StartAsync()).Success);

        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

        Assert.AreEqual("localhost:10.0", resolution.Display);
        Assert.IsNotNull(resolution.Connector);
    }

    /// <summary>记录附着 / 脱离的宿主;窗口回调一概不管。</summary>
    internal sealed class RecordingHost : IEmbeddedXServerHost
    {
        public X11Server? Attached { get; private set; }

        public int Detaches { get; private set; }

        /// <summary>设了就在附着时抛它(模拟宿主出错)。</summary>
        public Exception? FailAttachWith { get; set; }

        public Task AttachAsync(X11Server server, CancellationToken cancellationToken)
        {
            if (FailAttachWith is { } failure)
            {
                throw failure;
            }
            Attached = server;
            LayoutAtAttach = KeyboardLayout;
            return Task.CompletedTask;
        }

        public string? KeyboardLayout { get; private set; }

        /// <summary>附着那一刻宿主手里的键盘布局(要在附着之前就交给宿主)。</summary>
        public string? LayoutAtAttach { get; private set; }

        public void UseKeyboardLayout(string layout) => KeyboardLayout = layout;

        /// <summary>附着之前交来的窗口模式。</summary>
        public string? WindowMode { get; private set; }

        public void UseWindowMode(string mode) => WindowMode = mode;

        public void Detach() => Detaches++;

        private EventHandler<XServerGrabStall>? _grabStallReported;

        public event EventHandler<XServerGrabStall>? GrabStallReported
        {
            add => _grabStallReported += value;
            remove => _grabStallReported -= value;
        }

        /// <summary>挂在 <see cref="GrabStallReported" /> 上的处理器数。</summary>
        public int GrabStallSubscribers => _grabStallReported?.GetInvocationList().Length ?? 0;

        /// <summary>模拟服务端报来「GrabServer 抓得太久」。</summary>
        public void RaiseGrabStall(XServerGrabStall stall) => _grabStallReported?.Invoke(this, stall);

        public void TopLevelMapped(XTopLevelWindow window)
        {
        }

        public void TopLevelUnmapped(XTopLevelWindow window)
        {
        }

        public void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes)
        {
        }

        public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage)
        {
        }

        public void CursorChanged(XTopLevelWindow? window, XCursor cursor)
        {
        }

        public void BellRequested(int volume)
        {
        }

        public void ClipboardChanged(string text)
        {
        }
    }
}
