#:project ../../../src/VelaShell.XServer/VelaShell.XServer.csproj
#:property TreatWarningsAsErrors=false

// 互操作靶场:起一个 VelaShell.XServer,让 Docker 里的真实 X 客户端连进来。
//
//   dotnet run scripts/xserver/interop/run-server.cs -- [显示号=42] [输出目录=./xshots] [秒数=0 一直跑]
//
// 起来后打印 DISPLAY 与 cookie;容器里这样连(见同目录 Run-Client.ps1):
//   xauth add host.docker.internal:42 MIT-MAGIC-COOKIE-1 <cookie>
//   DISPLAY=host.docker.internal:42 xterm
//
// 每个顶层窗口在每批损伤之后存成 <输出目录>/<窗口ID>.png,协议错误逐条打印 —— 用来看「画出来对不对」
// 与「哪条请求没实现 / 实现错了」。监听 0.0.0.0 且强制 cookie:Docker 的连接不是从 127.0.0.1 来的。

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using VelaShell.XServer.Drawing;
using VelaShell.XServer.Host;
using VelaShell.XServer.Server;

int display = args.Length > 0 ? int.Parse(args[0]) : 42;
string outDir = Path.GetFullPath(args.Length > 1 ? args[1] : "xshots");
int seconds = args.Length > 2 ? int.Parse(args[2]) : 0;
Directory.CreateDirectory(outDir);

byte[] cookie = RandomNumberGenerator.GetBytes(16);
ShotHost host = new(outDir);
await using X11Server server = new(new XServerOptions
{
    DisplayNumber = display,
    ListenAddress = IPAddress.Any,
    AuthorizationCookie = cookie,
    ScreenWidth = 1920,
    ScreenHeight = 1080,
    Log = line => Console.WriteLine($"[x] {line}"),
}, host);
await server.StartAsync();
Console.WriteLine($"DISPLAY=host.docker.internal:{display}");
Console.WriteLine($"COOKIE={Convert.ToHexStringLower(cookie)}");
File.WriteAllText(Path.Combine(outDir, "cookie.txt"), Convert.ToHexStringLower(cookie));
Console.Out.Flush();

// 注入输入:往 <输出目录>/cmd.txt 写命令,每行一条,读完即删。目标是最近映射的那个普通顶层窗口。
//   type <文字>     逐字符按键(US 布局;\n 表示回车)
//   click <x> <y>   左键点击(窗口内坐标)
//   resize <w> <h>  像用户拖边框一样改尺寸
//   close           点关闭按钮
string cmdFile = Path.Combine(outDir, "cmd.txt");
DateTime deadline = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
while (DateTime.UtcNow < deadline)
{
    await Task.Delay(200);
    if (!File.Exists(cmdFile) || host.Target is not { } target)
    {
        continue;
    }
    string[] lines = File.ReadAllLines(cmdFile);
    File.Delete(cmdFile);
    server.FocusTopLevel(target);
    foreach (string line in lines)
    {
        string[] parts = line.Split(' ', 2);
        switch (parts[0])
        {
            case "type":
                foreach ((byte code, bool shift) in Keys.For(parts[1].Replace("\\n", "\n", StringComparison.Ordinal)))
                {
                    if (shift) server.Key(XKeycodes.ShiftLeft, true);
                    server.Key(code, true);
                    server.Key(code, false);
                    if (shift) server.Key(XKeycodes.ShiftLeft, false);
                }
                break;
            case "click":
                int[] xy = [.. parts[1].Split(' ').Select(int.Parse)];
                server.PointerMotion(target, xy[0], xy[1]);
                server.PointerButton(target, xy[0], xy[1], 1, true);
                server.PointerButton(target, xy[0], xy[1], 1, false);
                break;
            case "resize":
                int[] wh = [.. parts[1].Split(' ').Select(int.Parse)];
                server.ResizeTopLevel(target, wh[0], wh[1]);
                break;
            case "close":
                server.CloseTopLevel(target);
                break;
        }
        Console.WriteLine($"[cmd] {line} -> 0x{target:x}");
    }
}

static class Keys
{
    private const string Lower = "`1234567890-=qwertyuiop[]\\asdfghjkl;'zxcvbnm,./ ";
    private const string Upper = "~!@#$%^&*()_+QWERTYUIOP{}|ASDFGHJKL:\"ZXCVBNM<>? ";
    private static readonly byte[] Codes =
    [
        49, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21,
        24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 51,
        38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
        52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 65,
    ];

    public static IEnumerable<(byte Code, bool Shift)> For(string text)
    {
        foreach (char ch in text)
        {
            if (ch == '\n')
            {
                yield return (XKeycodes.Return, false);
                continue;
            }
            int i = Lower.IndexOf(ch, StringComparison.Ordinal);
            if (i >= 0)
            {
                yield return (Codes[i], false);
                continue;
            }
            i = Upper.IndexOf(ch, StringComparison.Ordinal);
            if (i >= 0)
            {
                yield return (Codes[i], true);
            }
        }
    }
}

sealed class ShotHost(string outDir) : IXServerHost
{
    /// <summary>最近映射的普通(非 override-redirect)顶层窗口 —— 注入命令的目标。</summary>
    public uint? Target { get; private set; }

    public void TopLevelMapped(XTopLevelWindow w)
    {
        if (!w.OverrideRedirect)
        {
            Target = w.Id;
        }
        Console.WriteLine($"[host] mapped 0x{w.Id:x} {w.Width}x{w.Height}+{w.X}+{w.Y} '{w.Title}' override={w.OverrideRedirect}");
    }
    public void TopLevelUnmapped(XTopLevelWindow w) => Console.WriteLine($"[host] unmapped 0x{w.Id:x}");
    public void TopLevelChanged(XTopLevelWindow w) => Console.WriteLine($"[host] changed 0x{w.Id:x} {w.Width}x{w.Height}+{w.X}+{w.Y} '{w.Title}' class='{w.ClassName}' shape={(w.Shape is null ? "none" : w.Shape.Count + " rects")}");
    public void CursorChanged(XTopLevelWindow? w, int glyph) { }
    public void Bell(int percent) => Console.WriteLine("[host] bell");

    public void TopLevelDamaged(XTopLevelWindow w, IReadOnlyList<XRect> damage)
    {
        uint[] pixels = new uint[Math.Max(1, w.Width * w.Height)];
        (int width, int height) = w.CopyPixels(pixels);
        if (width > 0 && height > 0)
        {
            Png.Write(Path.Combine(outDir, $"{w.Id:x}.png"), pixels, width, height);
        }
    }
}

static class Png
{
    private static readonly uint[] Crc = BuildCrc();

    public static void Write(string path, uint[] pixels, int width, int height)
    {
        using MemoryStream raw = new();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);   // 行滤波:None
            for (int x = 0; x < width; x++)
            {
                uint p = pixels[(y * width) + x];
                raw.WriteByte((byte)(p >> 16));
                raw.WriteByte((byte)(p >> 8));
                raw.WriteByte((byte)p);
            }
        }
        using MemoryStream compressed = new();
        using (ZLibStream z = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(z);
        }
        using FileStream f = File.Create(path + ".tmp");
        f.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10]);
        byte[] ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)width);
        WriteBE(ihdr, 4, (uint)height);
        ihdr[8] = 8;   // 位深
        ihdr[9] = 2;   // RGB
        Chunk(f, "IHDR", ihdr);
        Chunk(f, "IDAT", compressed.ToArray());
        Chunk(f, "IEND", []);
        f.Close();
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        byte[] len = new byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in typeBytes.Concat(data))
        {
            crc = Crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        byte[] c = new byte[4];
        WriteBE(c, 0, crc ^ 0xFFFFFFFF);
        s.Write(c);
    }

    private static void WriteBE(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }

    private static uint[] BuildCrc()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}
