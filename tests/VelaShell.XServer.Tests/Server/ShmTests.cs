using System.Runtime.InteropServices;
using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>MIT-SHM:只对经 Unix 套接字连进来的客户端可见;段附加、ShmPutImage / ShmGetImage 经共享内存搬像素。只在 Linux 上跑。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed partial class ShmTests
{
    private static async Task<(byte Major, byte Event)?> ShmAsync(XTestClient c)
    {
        byte[] name = Encoding.Latin1.GetBytes("MIT-SHM");
        XMessage q = await c.RequestAsync(98, 0, b => b.U16((ushort)name.Length).U16(0).Bytes(name).Pad());
        return q.Bytes[8] == 1 ? (q.Bytes[9], q.Bytes[10]) : null;
    }

    [TestMethod]
    public async Task 经Unix套接字的客户端用共享内存搬像素_别的客户端看不见这个扩展()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("MIT-SHM 只在 Linux 上提供");
            return;
        }
        string path = Path.Combine(Path.GetTempPath(), $"vx-shm-{Guid.NewGuid():N}.sock");
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = path });
        await server.StartAsync();

        await using (XTestClient remote = await XTestClient.ConnectAsync(server))
        {
            Assert.IsNull(await ShmAsync(remote), "不经 Unix 套接字(比如 SSH 转发来的)看不见 MIT-SHM");
        }

        await using XTestClient c = await XTestClient.ConnectUnixAsync(path);
        (byte shm, byte completion) = await ShmAsync(c) ?? throw new AssertFailedException("Unix 套接字上应当有 MIT-SHM");
        XMessage version = await c.RequestAsync(shm, 0);
        Assert.AreEqual(1, version.U16(8), "major 1");
        Assert.AreEqual(0, version.Bytes[1], "shared-pixmaps = False");

        const int size = 16 * 16 * 4;
        int shmid = ShmGet(0, size, 0x380);   // IPC_PRIVATE,IPC_CREAT | 0600
        Assert.IsGreaterThanOrEqualTo(0, shmid);
        nint address = ShmAt(shmid, 0, 0);
        try
        {
            _ = ShmCtl(shmid, 0, 0);   // IPC_RMID:最后一个摘下时释放
            byte[] red = new byte[size];
            for (int i = 0; i < size; i += 4)
            {
                red[i + 2] = 0xFF;   // 0x00FF0000 小端
            }
            Marshal.Copy(red, 0, address, size);

            uint pixmap = c.NewId(), gc = c.NewId(), segment = c.NewId();
            await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(16).U16(16));
            await c.SendAsync(55, 0, b => b.U32(gc).U32(pixmap).U32(0x4).U32(0x00FF00));
            await c.SendAsync(shm, 1, b => b.U32(segment).U32((uint)shmid).U8(0).U8(0).U16(0));   // Attach
            // ShmPutImage:整幅 16×16,ZPixmap、深度 24,要 Completion 事件。
            await c.SendAsync(shm, 3, b => b.U32(pixmap).U32(gc).U16(16).U16(16).U16(0).U16(0).U16(16).U16(16)
                .I16(0).I16(0).U8(24).U8(2).U8(1).U8(0).U32(segment).U32(0));
            XMessage done = await c.NextEventAsync(completion);
            Assert.AreEqual(segment, done.U32(12), "Completion 带着段");

            XMessage image = await c.RequestAsync(73, 2, b => b.U32(pixmap).I16(5).I16(5).U16(1).U16(1).U32(0xFFFFFFFF));
            Assert.AreEqual(0xFF0000u, image.U32(32) & 0xFFFFFF, "共享内存里的红色画进了像素图");

            // 像素图填绿,ShmGetImage 写回段里。
            await c.SendAsync(70, 0, b => b.U32(pixmap).U32(gc).I16(0).I16(0).U16(16).U16(16));
            XMessage got = await c.RequestAsync(shm, 4, b => b.U32(pixmap).I16(0).I16(0).U16(16).U16(16).U32(0xFFFFFFFF)
                .U8(2).U8(0).U16(0).U32(segment).U32(0));
            Assert.AreEqual((uint)size, got.U32(12), "size");
            byte[] back = new byte[4];
            Marshal.Copy(address, back, 0, 4);
            Assert.AreEqual(0xFF, back[1], "绿色写回了共享内存");
            await c.SendAsync(shm, 2, b => b.U32(segment));   // Detach
            await c.SyncAsync();
        }
        finally
        {
            _ = ShmDt(address);
        }
    }

    [LibraryImport("libc", EntryPoint = "shmget")]
    private static partial int ShmGet(int key, nint size, int flags);

    [LibraryImport("libc", EntryPoint = "shmat")]
    private static partial nint ShmAt(int shmid, nint address, int flags);

    [LibraryImport("libc", EntryPoint = "shmdt")]
    private static partial int ShmDt(nint address);

    [LibraryImport("libc", EntryPoint = "shmctl")]
    private static partial int ShmCtl(int shmid, int command, nint buffer);
}
