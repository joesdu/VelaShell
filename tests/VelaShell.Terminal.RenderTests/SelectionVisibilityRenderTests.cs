using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.RenderTests;

/// <summary>
/// 选区高亮的<b>像素级</b>回归:拖出一段之后,屏幕上那条带子到底看不看得见。
/// <para>
/// 回归的故障:选区色此前按半透明叠加绘制(暗 35% / 亮 25%),而各家方案的选区色本就是照
/// 不透明填充设计的、与自家背景只差一线 —— 乘上不透明度后压在背景上只剩 1.05:1 ~ 1.21:1,
/// 拖完一段跟没拖一样。这条只有真读像素才验得到:调色板层看着一切正常,选区色明明设了。
/// </para>
/// <para>
/// 判据(CIE L* 感知明度差)在本文件里独立实现一份,不复用渲染侧的 <c>SelectionContrast</c>:
/// 测试要当独立的度量仪,而不是把被测代码的算术抄一遍再和自己对答案。
/// </para>
/// </summary>
[TestClass]
[TestCategory("GlyphRendering")]
public class SelectionVisibilityRenderTests
{
    /// <summary>
    /// 选区带与终端底之间必须让人一眼看出的感知明度差(CIE L*,0–100 标度)。
    /// <para>
    /// 刻意<b>低于两档中较低的那一档</b>(暗底 20 / 亮底 16):这条是屏幕级的独立度量仪,
    /// 量的是"眼睛能不能看出来",不是"实现有没有算到它自己那个数"。把它钉到和实现一样的值,
    /// 这个测试就退化成把被测代码的算术抄一遍再和自己对答案。
    /// </para>
    /// <para>
    /// 余量也是必需的:整定用二分停在**刚好够**的那一步,填充色再量化到 8bit,
    /// 屏幕上量到的总比算出来的低那么零点几(16.0 → 15.87,当年 14.0 → 13.9 是同一回事)。
    /// 地板贴着实现值走,这个测试会因为一个色阶的舍入而红。
    /// </para>
    /// </summary>
    private const double MinLightnessDelta = 14.0;

    private static HeadlessUnitTestSession Session => SkiaTestSession.Current;

    private static void OnUi(Action body) =>
        Session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    [TestMethod]
    public void DraggedSelection_PaintsABandTheEyeCanActuallySee()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl
            {
                CopyOnSelect = false, // headless 下不去碰剪贴板
                ShowLineNumber = false,
                ShowLineTimestamp = false,
                ShowFoldMarker = false,
                CursorBlink = false,
            };
            control.Feed(Encoding.ASCII.GetBytes("aaaaaaaaaaaaaaaa\r\nbbbbbbbbbbbbbbbb"));

            var window = new Window { Width = 640, Height = 360, Content = control };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame(); // 填充屏幕行映射与单元格度量

            uint[] before = RenderFrame(window);
            Drag(window, control, (0, 0), (0, 16));
            uint[] after = RenderFrame(window);
            window.Close();

            // 选区带的颜色 = 变了色的那些像素里最常见的那个(字形本身只占少数)。
            uint band = MostCommonChanged(before, after);
            uint background = MostCommon(before);

            double delta = Math.Abs(Lightness(band) - Lightness(background));
            Assert.IsGreaterThan(
                MinLightnessDelta,
                delta,
                $"选区带 #{band & 0xFFFFFF:X6} 与终端底 #{background & 0xFFFFFF:X6} 只差 L* {delta:F1} —— "
                    + "这个差在屏幕上等于没画,用户会以为压根没选中。");
        });
    }

    private static void Drag(Window window, VelaTerminalControl control, (int Row, int Col) from, (int Row, int Col) to)
    {
        window.MouseDown(CellPoint(control, from.Row, from.Col), MouseButton.Left);
        window.MouseMove(CellPoint(control, to.Row, to.Col));
        window.MouseUp(CellPoint(control, to.Row, to.Col), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>屏幕行/列的左上角坐标(略微内缩,避免落到相邻单元格)。</summary>
    private static Point CellPoint(VelaTerminalControl control, int row, int col) =>
        new(
            control.GutterForTest.TotalWidth + (col * control.CellWidthForTest) + 1,
            (row * control.CellHeightForTest) + 1);

    private static uint MostCommon(uint[] pixels) => MostCommon(pixels, _ => true);

    private static uint MostCommonChanged(uint[] before, uint[] after)
    {
        Dictionary<uint, int> histogram = [];
        for (int i = 0; i < after.Length; i++)
        {
            if (before[i] != after[i])
            {
                histogram[after[i]] = histogram.GetValueOrDefault(after[i]) + 1;
            }
        }
        Assert.IsGreaterThan(0, histogram.Count, "拖拽之后一个像素都没变 —— 选区根本没画。");
        return histogram.MaxBy(pair => pair.Value).Key;
    }

    private static uint MostCommon(uint[] pixels, Func<uint, bool> keep)
    {
        Dictionary<uint, int> histogram = [];
        foreach (uint p in pixels)
        {
            if (keep(p))
            {
                histogram[p] = histogram.GetValueOrDefault(p) + 1;
            }
        }
        return histogram.MaxBy(pair => pair.Value).Key;
    }

    /// <summary>BGRA 像素的 CIE L*(0–100)。</summary>
    private static double Lightness(uint bgra)
    {
        double b = Linear((byte)bgra);
        double g = Linear((byte)(bgra >> 8));
        double r = Linear((byte)(bgra >> 16));
        double y = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        return y > 0.008856 ? (116 * Math.Cbrt(y)) - 16 : 903.3 * y;
    }

    private static double Linear(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>渲染一帧并返回 BGRA 像素。</summary>
    private static uint[] RenderFrame(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using WriteableBitmap bitmap = window.CaptureRenderedFrame()
            ?? throw new AssertFailedException(
                "没有拿到渲染帧。若 Skia 后端未生效(UseHeadlessDrawing 仍为 true),这里恒为 null。");

        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        const int bytesPerPixel = 4;
        int bufferSize = checked(width * height * bytesPerPixel);
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, bufferSize, width * bytesPerPixel);
            uint[] pixels = new uint[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (uint)Marshal.ReadInt32(buffer, i * bytesPerPixel);
            }
            return pixels;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
