using System.Globalization;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Controls;
using VelaShell.Core.Diagnostics;
using VelaShell.Core.Localization;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>链路追踪窗口的布局回归:左地图右列表,地图只落有归属地的跃点。</summary>
[TestClass]
[TestCategory("TraceUI")]
public sealed class TraceRouteUiTests
{
    private static HeadlessUnitTestSession _session = null!;
    private static LocalizationService _localization = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TraceRouteUiTests).Assembly);
        _localization = new();
        LocalizedStrings.Instance.Attach(_localization);
    }

    [TestMethod]
    public void Window_PutsTheMapOnTheLeftAndTheHopListOnTheRight()
    {
        OnUi(() =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            _localization.SetLanguage("zh-CN");

            var vm = new TraceRouteViewModel(null);
            vm.PointAt("154.12.41.59", "洛杉矶节点");
            Populate(vm);

            var window = new TraceRouteWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            TraceWorldMap map = window.GetVisualDescendants().OfType<TraceWorldMap>().Single();
            ListBox list = window.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.IsGreaterThan(0, map.Bounds.Width, "地图没有拿到宽度。");
            // 左地图右列表:地图整体在列表左侧。
            double mapRight = map.TranslatePoint(new(map.Bounds.Width, 0), window)!.Value.X;
            double listLeft = list.TranslatePoint(new(0, 0), window)!.Value.X;
            Assert.IsLessThanOrEqualTo(listLeft, mapRight, "地图应完全位于列表左侧。");
            Assert.HasCount(5, vm.Hops);

            SaveFrame(window, "trace-route.png");
            window.Close();
        });
    }

    [TestMethod]
    public void AutoRotateToggle_PausesAndResumesGlobeRotation()
    {
        OnUi(() =>
        {
            var window = new TraceRouteWindow { DataContext = new TraceRouteViewModel(null) };
            window.Show();
            try
            {
                var toggle = window.FindControl<CheckBox>("AutoRotateToggle")!;
                var map = window.GetVisualDescendants().OfType<TraceWorldMap>().Single();
                Assert.IsTrue(toggle.IsChecked);
                toggle.IsChecked = false;
                Assert.IsFalse(map.AutoRotate);
                toggle.IsChecked = true;
                Assert.IsTrue(map.AutoRotate);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void RegionalRoute_ShowsLocatedHopsOnTheGlobe()
    {
        OnUi(() =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            _localization.SetLanguage("zh-CN");

            var vm = new TraceRouteViewModel(null);
            vm.PointAt("202.97.43.42", "华南节点");
            AddHop(vm, 1, "192.168.1.1", null, null, null);
            AddHop(vm, 2, "202.97.94.114", 39.9042, 116.4074, "中国/北京");
            AddHop(vm, 3, "202.97.43.42", 23.1291, 113.2644, "中国/广州");
            AddHop(vm, 4, "14.147.205.98", 30.5728, 104.0668, "中国/成都");

            var window = new TraceRouteWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.AreEqual(3, vm.Hops.Count(h => h.HasLocation));
            SaveFrame(window, "trace-route-regional.png");
            window.Close();
        });
    }

    [TestMethod]
    public void AddingHop_PreservesDraggedViewAndExistingFlight()
    {
        OnUi(() =>
        {
            var clock = new GlobeClock();
            var vm = new TraceRouteViewModel(null);
            AddHop(vm, 1, "192.0.2.1", 10, 40, "A/A");
            AddHop(vm, 2, "192.0.2.2", 10, 115, "B/B");
            var map = new TraceWorldMap(clock) { Hops = vm.Hops };
            var window = new Window { Width = 400, Height = 400, Content = map };
            window.Show();
            try
            {
                var center = new Point(200, 200);
                window.MouseDown(center, MouseButton.Left);
                byte[] initial = FramePixels(window);
                window.MouseMove(new Point(218, 214));
                byte[] dragged = FramePixels(window);
                CollectionAssert.AreNotEqual(initial, dragged);
                window.MouseWheel(center, new Vector(0, 1));
                byte[] rotated = FramePixels(window);
                CollectionAssert.AreNotEqual(dragged, rotated);

                clock.Advance(TimeSpan.FromMilliseconds(700));
                map.InvalidateVisual();
                byte[] airborne = FramePixels(window);
                CollectionAssert.AreNotEqual(rotated, airborne);
                AddHop(vm, 3, "192.0.2.3", -10, 150, "C/C");
                map.Revision++;
                byte[] updated = FramePixels(window);
                CollectionAssert.AreNotEqual(airborne, updated, "新跃点应出现在右半球。");
                for (int y = 0; y < 400; y++)
                {
                    int start = y * 400 * 4;
                    Assert.IsTrue(airborne.AsSpan(start, 200 * 4).SequenceEqual(updated.AsSpan(start, 200 * 4)),
                        "左半球的地貌和飞行中的既有弧线应保持原位。");
                }
                window.MouseUp(center, MouseButton.Left);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void CoLocatedHops_DoNotAnimateAnEndpointStub()
    {
        OnUi(() =>
        {
            var vm = new TraceRouteViewModel(null);
            AddHop(vm, 1, "192.0.2.1", 23, 113, "中国/广州");
            AddHop(vm, 2, "192.0.2.2", 23, 113, "中国/广州");
            var clock = new GlobeClock();
            var map = new TraceWorldMap(clock) { Hops = vm.Hops };
            var window = new Window { Width = 400, Height = 400, Content = map };
            window.Show();
            try
            {
                window.MouseDown(new Point(200, 200), MouseButton.Left);
                byte[] start = FramePixels(window);
                clock.Advance(TimeSpan.FromMilliseconds(500));
                map.InvalidateVisual();
                CollectionAssert.AreEqual(start, FramePixels(window));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void AirborneRoute_RecolorsWhenOnlyTheAccentChanges()
    {
        OnUi(() =>
        {
            var vm = new TraceRouteViewModel(null);
            AddHop(vm, 1, "192.0.2.1", 10, 40, "A/A");
            AddHop(vm, 2, "192.0.2.2", 10, 115, "B/B");
            var clock = new GlobeClock();
            var map = new TraceWorldMap(clock) { Hops = vm.Hops };
            var window = new Window { Width = 400, Height = 400, Content = map };
            window.Resources["VelaAccent"] = Avalonia.Media.Brushes.Red;
            window.Show();
            try
            {
                FramePixels(window);
                clock.Advance(TimeSpan.FromSeconds(1));
                map.InvalidateVisual();
                byte[] red = FramePixels(window);
                window.Resources["VelaAccent"] = Avalonia.Media.Brushes.Lime;
                byte[] green = FramePixels(window);

                // 只取两个跃点之间的像素，排除节点和标签本身的变色。
                int recolored = 0;
                for (int y = 0; y < 400; y++)
                {
                    for (int x = 145; x < 175; x++)
                    {
                        int channel = (y * 400 + x) * 4 + 1;
                        if (green[channel] - red[channel] > 100)
                        {
                            recolored++;
                        }
                    }
                }
                Assert.IsGreaterThan(20, recolored);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void GlobeBackground_FollowsTheWindowsThemeDictionary()
    {
        OnUi(() =>
        {
            var window = new Window { Width = 400, Height = 400, Content = new TraceWorldMap() };
            window.Resources.ThemeDictionaries[Avalonia.Styling.ThemeVariant.Light] = new ResourceDictionary
            {
                ["VelaBgPage"] = Avalonia.Media.Brushes.Wheat
            };
            window.Resources.ThemeDictionaries[Avalonia.Styling.ThemeVariant.Dark] = new ResourceDictionary
            {
                ["VelaBgPage"] = Avalonia.Media.Brushes.MidnightBlue
            };
            window.Show();
            try
            {
                foreach (var (variant, color) in new[]
                {
                    (Avalonia.Styling.ThemeVariant.Light, Avalonia.Media.Colors.Wheat),
                    (Avalonia.Styling.ThemeVariant.Dark, Avalonia.Media.Colors.MidnightBlue)
                })
                {
                    window.RequestedThemeVariant = variant;
                    Dispatcher.UIThread.RunJobs();
                    byte[] frame = FramePixels(window);
                    Assert.AreEqual(color.G, frame[4 * (400 * 12 + 12) + 1]);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    private sealed class GlobeClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    private static byte[] FramePixels(Window window)
    {
        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.IsNotNull(frame);
        using var pixels = frame.Lock();
        Assert.AreEqual(new PixelSize(400, 400), pixels.Size);
        int rowBytes = pixels.Size.Width * 4;
        byte[] result = new byte[rowBytes * pixels.Size.Height];
        for (int y = 0; y < pixels.Size.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels.Address + y * pixels.RowBytes, result, y * rowBytes, rowBytes);
        }
        return result;
    }

    [TestMethod]
    public void HopsWithoutLocation_AreNotPlottedOnTheMap()
    {
        OnUi(() =>
        {
            var vm = new TraceRouteViewModel(null);
            Populate(vm);
            // 5 跳里只有 3 跳查到了归属地,其余不该被塞到国家中心装作知道。
            Assert.AreEqual(3, vm.Hops.Count(h => h.HasLocation));
        });
    }

    /// <summary>灌一条有真实经纬度的链路:北京 → 广州 → 洛杉矶,中间夹两跳查不到位置的。</summary>
    private static void Populate(TraceRouteViewModel vm)
    {
        (int Ttl, string Ip, double? Lat, double? Lon, string? Place)[] samples =
        [
            (1, "36.110.203.205", 39.9042, 116.4074, "中国/北京"),
            (2, "10.0.0.1", null, null, null),
            (3, "202.97.43.42", 23.1291, 113.2644, "中国/广州"),
            (4, "23.225.225.28", null, null, null),
            (5, "154.12.41.59", 34.0522, -118.2437, "美国/洛杉矶")
        ];
        foreach ((int ttl, string ip, double? lat, double? lon, string? place) in samples)
        {
            var hop = new TraceHop(ttl);
            hop.Add(new(ttl, IPAddress.Parse(ip), TimeSpan.FromMilliseconds(10 * ttl), ttl == 5, false));
            var row = new TraceHopViewModel(hop);
            row.Update(hop);
            if (lat is { } latitude && lon is { } longitude)
            {
                string[] parts = place!.Split('/');
                row.SetLocation(new(latitude, longitude, parts[1], parts[0], null));
            }
            vm.Hops.Add(row);
        }
    }

    /// <summary>往面板里塞一跳(可带归属地)。</summary>
    private static void AddHop(TraceRouteViewModel vm, int ttl, string ip, double? lat, double? lon, string? place)
    {
        var hop = new TraceHop(ttl);
        hop.Add(new(ttl, IPAddress.Parse(ip), TimeSpan.FromMilliseconds(8 * ttl), false, false));
        var row = new TraceHopViewModel(hop);
        row.Update(hop);
        if (lat is { } latitude && lon is { } longitude)
        {
            string[] parts = place!.Split('/');
            row.SetLocation(new(latitude, longitude, parts[1], parts[0], null));
        }
        vm.Hops.Add(row);
    }

    private static void SaveFrame(TopLevel topLevel, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using WriteableBitmap? frame = topLevel.CaptureRenderedFrame();
        Assert.IsNotNull(frame);
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, PngBitmapEncoderOptions.Default);
    }

    private static void OnUi(Action action) => _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}
