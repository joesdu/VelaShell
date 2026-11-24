using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using VelaShell.ViewModels;

namespace VelaShell.Controls;

/// <summary>
/// 链路追踪的点阵地球仪。球体、陆地点阵和路线弧线都在控件内绘制，避免引入外部地图运行时。
/// </summary>
/// <remarks>
/// 地理位置来自离线归属地库，只绘制确实拿到经纬度的跃点。球体的旋转和路线头部动画用于表达
/// 路由仍在持续采样，同时让推断位置与确定的网络拓扑保持视觉上的区别。
/// </remarks>
public sealed class TraceWorldMap : Control
{
    /// <summary>跃点集合属性。</summary>
    public static readonly StyledProperty<IEnumerable?> HopsProperty = AvaloniaProperty.Register<TraceWorldMap, IEnumerable?>(nameof(Hops));

    /// <summary>数据版本属性。</summary>
    public static readonly StyledProperty<int> RevisionProperty = AvaloniaProperty.Register<TraceWorldMap, int>(nameof(Revision));

    /// <summary>控制地球仪是否自动旋转。</summary>
    public static readonly StyledProperty<bool> AutoRotateProperty = AvaloniaProperty.Register<TraceWorldMap, bool>(nameof(AutoRotate), true);

    private static readonly Lazy<IReadOnlyList<double[]>> CountryRings = new(() => LoadRings("avares://VelaShell/Assets/world-countries.txt"));

    private static readonly Lazy<SurfaceDot[]> Surface = new(() => BuildSurface(CountryRings.Value));
    private readonly List<Point>[] _dotBuckets = [.. Enumerable.Range(0, 16).Select(_ => new List<Point>())];
    private readonly Dictionary<(int From, int To), RouteArc> _arcs = [];
    private readonly DispatcherTimer _animationTimer;
    private readonly TimeProvider _clock;
    private long _lastFrameTicks;
    private double _yaw;
    private double _pitch = 0.28;
    private double _yawVelocity;
    private double _pitchVelocity;
    private IPointer? _dragPointer;
    private Point _lastPointer;
    private long _lastMoveTicks;

    static TraceWorldMap()
    {
        AffectsRender<TraceWorldMap>(HopsProperty, RevisionProperty);
        ClipToBoundsProperty.OverrideDefaultValue<TraceWorldMap>(true);
    }

    /// <summary>创建带有连续动画刷新的点阵地球仪。</summary>
    public TraceWorldMap() : this(TimeProvider.System) { }

    internal TraceWorldMap(TimeProvider clock)
    {
        _clock = clock;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ResourcesChanged += (_, _) => InvalidateVisual();
        _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _animationTimer.Tick += AnimationTimer_Tick;
    }

    /// <summary>当前链路中可以带有或不带归属地的跃点集合。</summary>
    public IEnumerable? Hops
    {
        get => GetValue(HopsProperty);
        set => SetValue(HopsProperty, value);
    }

    /// <summary>是否自动旋转地球仪。</summary>
    public bool AutoRotate
    {
        get => GetValue(AutoRotateProperty);
        set => SetValue(AutoRotateProperty, value);
    }

    /// <summary>每轮追踪结果的版本号。</summary>
    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _lastFrameTicks = _clock.GetTimestamp();
        _animationTimer.Start();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _dragPointer?.Capture(null);
        _animationTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        long now = _clock.GetTimestamp();
        double delta = Math.Min(0.12, _clock.GetElapsedTime(_lastFrameTicks, now).TotalSeconds);
        _lastFrameTicks = now;
        if (!AutoRotate)
        {
            _yawVelocity = _pitchVelocity = 0;
        }
        else if (_dragPointer is null)
        {
            Rotate(delta * (0.12 + _yawVelocity), delta * _pitchVelocity);
            double damping = Math.Exp(-delta * 5);
            _yawVelocity *= damping;
            _pitchVelocity *= damping;
        }
        InvalidateVisual();
    }

    private void Rotate(double yaw, double pitch)
    {
        _yaw = Math.IEEERemainder(_yaw + yaw, Math.Tau);
        _pitch = Math.Clamp(_pitch + pitch, -Math.PI / 2, Math.PI / 2);
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_dragPointer is not null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        _lastPointer = e.GetPosition(this);
        _lastMoveTicks = _clock.GetTimestamp();
        _yawVelocity = _pitchVelocity = 0;
        _dragPointer = e.Pointer;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer != _dragPointer)
        {
            return;
        }
        Point position = e.GetPosition(this);
        Vector delta = position - _lastPointer;
        double radius = GlobeRadius(Bounds.Size);
        long now = _clock.GetTimestamp();
        double seconds = Math.Max(0.016, _clock.GetElapsedTime(_lastMoveTicks, now).TotalSeconds);
        Rotate(-delta.X / radius, delta.Y / radius);
        _yawVelocity = Math.Clamp(-delta.X / radius / seconds, -4, 4);
        _pitchVelocity = Math.Clamp(delta.Y / radius / seconds, -4, 4);
        _lastPointer = position;
        _lastMoveTicks = now;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer == _dragPointer)
        {
            if (_clock.GetElapsedTime(_lastMoveTicks).TotalSeconds > 0.12)
            {
                _yawVelocity = _pitchVelocity = 0;
            }
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (e.Pointer == _dragPointer)
        {
            _dragPointer = null;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Rotate(-0.14 * e.Delta.X, -0.14 * e.Delta.Y);
        _yawVelocity = _pitchVelocity = 0;
        InvalidateVisual();
        e.Handled = true;
    }

    private static double GlobeRadius(Size size) => Math.Max(1, Math.Min(size.Width, size.Height) * 0.445);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        Rect full = new(Bounds.Size);
        if (full.Width <= 0 || full.Height <= 0)
        {
            return;
        }

        IBrush background = Brush("VelaBgPage");
        IBrush land = Brush("VelaTextSecondary");
        IBrush border = Brush("VelaTraceBorder");
        IBrush muted = Brush("VelaTextMuted");
        IBrush route = Brush("VelaAccent");
        IBrush error = Brush("VelaError");
        IBrush labelBrush = Brush("VelaTextSecondary");
        IBrush labelBg = Brush("VelaTraceLabelBg");

        context.FillRectangle(background, full);

        double radius = GlobeRadius(full.Size);
        var camera = new Projection(_yaw, _pitch, full.Center, radius);
        using (context.PushOpacity(0.04))
        {
            context.DrawEllipse(land, null, full.Center, radius, radius);
        }
        using (context.PushOpacity(0.48))
        {
            context.DrawEllipse(null, new Pen(border, 1), full.Center, radius, radius);
        }
        DrawSurface(context, camera, radius, land, muted);

        List<TraceHopViewModel> located = LocatedHops();
        if (located.Count == 0)
        {
            _arcs.Clear();
            DrawEmptyHint(context, full, muted, Core.Resources.Strings.Get("Trace_MapNoData"));
            return;
        }

        List<ProjectedHop> projected = [];
        for (int i = 0; i < located.Count; i++)
        {
            TraceHopViewModel hop = located[i];
            SphereVector location = ToVector(new(hop.Latitude, hop.Longitude));
            projected.Add(new(camera.Project(location), hop, location));
        }

        DrawRoutes(context, projected, camera, route);
        DrawHops(context, projected, route, background, error);
        DrawLabels(context, full, projected, labelBrush, labelBg);
    }

    private void DrawSurface(DrawingContext context, Projection camera, double radius, IBrush land, IBrush sea)
    {
        foreach (List<Point> bucket in _dotBuckets)
        {
            bucket.Clear();
        }
        foreach (SurfaceDot dot in Surface.Value)
        {
            SpherePoint point = camera.Project(dot.Location);
            if (point.Depth > 0)
            {
                int level = Math.Min(7, (int)(point.Depth * 8));
                _dotBuckets[level * 2 + (dot.IsLand ? 1 : 0)].Add(point.Position);
            }
        }
        double scale = radius / 195;
        for (int level = 0; level < 8; level++)
        {
            double half = 0.62 * scale;
            using (context.PushOpacity(0.025 + level * 0.026))
            {
                foreach (Point point in _dotBuckets[level * 2])
                {
                    context.FillRectangle(sea, new Rect(point.X - half, point.Y - half, 2 * half, 2 * half));
                }
            }
            double size = (0.76 + level * 0.045) * scale;
            using (context.PushOpacity(0.06 + level * 0.075))
            {
                foreach (Point point in _dotBuckets[level * 2 + 1])
                {
                    context.DrawEllipse(land, null, point, size, size);
                }
            }
        }
    }

    private void DrawRoutes(DrawingContext context, List<ProjectedHop> hops, Projection camera, IBrush bright)
    {
        var active = new HashSet<(int, int)>();
        var flightPen = new Pen(bright, 1.65);
        long now = _clock.GetTimestamp();
        for (int i = 1; i < hops.Count; i++)
        {
            ProjectedHop from = hops[i - 1], to = hops[i];
            if (SphereVector.Dot(from.Location, to.Location) >= 1 - 1e-12)
            {
                continue;
            }
            (int, int) key = (from.Hop.Ttl, to.Hop.Ttl);
            active.Add(key);
            if (!_arcs.TryGetValue(key, out RouteArc? arc) || arc.From != from.Location || arc.To != to.Location)
            {
                _arcs[key] = arc = new RouteArc(from.Location, to.Location, now);
            }
            double time = _clock.GetElapsedTime(arc.StartTicks, now).TotalSeconds % (arc.FlightSeconds + 1.5);
            double head = Math.Clamp(time / arc.FlightSeconds, 0, 1);
            head *= head * (3 - 2 * head);
            double tail = Math.Clamp((time - arc.FlightSeconds - 0.28) / 0.55, 0, 1);
            SpherePoint lifted = from.Sphere;
            int segments = arc.Lifted.Length - 1;
            double headIndex = head * segments, tailIndex = tail * segments;
            for (int j = 1; j <= segments; j++)
            {
                SpherePoint nextLifted = camera.Project(arc.Lifted[j]);
                double start = Math.Max(0, tailIndex - j + 1);
                double end = Math.Min(1, headIndex - j + 1);
                if (start < end)
                {
                    Vector segment = nextLifted.Position - lifted.Position;
                    using (context.PushOpacity(FlightOpacity(nextLifted.Depth)))
                    {
                        context.DrawLine(flightPen, lifted.Position + segment * start, lifted.Position + segment * end);
                    }
                }
                lifted = nextLifted;
            }
            if (time < arc.FlightSeconds)
            {
                int j = Math.Min((int)headIndex, segments - 1);
                SphereVector a = arc.Lifted[j], b = arc.Lifted[j + 1];
                SpherePoint point = camera.Project(a + (b - a) * (headIndex - j));
                using (context.PushOpacity(FlightOpacity(point.Depth) * 0.18))
                {
                    context.DrawEllipse(bright, null, point.Position, 4.5, 4.5);
                }
                using (context.PushOpacity(FlightOpacity(point.Depth)))
                {
                    context.DrawEllipse(bright, null, point.Position, 2.3, 2.3);
                }
            }
            DrawRing(context, from.Sphere, flightPen, time / 0.72);
            DrawRing(context, to.Sphere, flightPen, (time - arc.FlightSeconds) / 0.95);
        }
        foreach ((int From, int To) key in _arcs.Keys.Where(key => !active.Contains(key)).ToArray())
        {
            _arcs.Remove(key);
        }
    }

    private static double FlightOpacity(double depth) => depth >= 0 ? 0.92 : depth >= -0.15 ? 0.48 : 0.16;

    private static void DrawRing(DrawingContext context, SpherePoint point, Pen pen, double phase)
    {
        if (point.Depth <= 0 || phase is < 0 or >= 1)
        {
            return;
        }
        double radius = 3 + 8 * (1 - Math.Pow(1 - phase, 3));
        using (context.PushOpacity((1 - phase) * (0.3 + 0.55 * point.Depth)))
        {
            context.DrawEllipse(null, pen, point.Position, radius, radius);
        }
    }

    private static void DrawHops(DrawingContext context, IReadOnlyList<ProjectedHop> hops, IBrush route, IBrush background, IBrush error)
    {
        for (int i = 0; i < hops.Count; i++)
        {
            ProjectedHop hop = hops[i];
            if (hop.Sphere.Depth <= 0)
            {
                continue;
            }
            IBrush color = hop.Hop.IsSuspect ? error : route;
            bool endpoint = i == 0 || hop.Hop.IsTarget;
            using (context.PushOpacity(0.38 + 0.62 * hop.Sphere.Depth))
            {
                if (endpoint)
                {
                    context.DrawEllipse(background, new Pen(color, 1), hop.Sphere.Position, 4.8, 4.8);
                }
                context.DrawEllipse(color, null, hop.Sphere.Position, 2.6, 2.6);
            }
        }
    }

    private void DrawLabels(
        DrawingContext context,
        Rect panel,
        IReadOnlyList<ProjectedHop> hops,
        IBrush textBrush,
        IBrush background)
    {
        List<Rect> taken = [];
        for (int index = 0; index < hops.Count; index++)
        {
            ProjectedHop projected = hops[index];
            if (projected.Sphere.Depth < 0.04)
            {
                continue;
            }

            int last = index;
            while (last + 1 < hops.Count
                && hops[last + 1].Sphere.Depth >= 0.04
                && Distance(hops[last + 1].Sphere.Position, projected.Sphere.Position) < 10)
            {
                last++;
            }

            string place = projected.Hop.LocationText.Length > 0 ? projected.Hop.LocationText : projected.Hop.Host;
            string number = last > index
                ? string.Create(CultureInfo.CurrentCulture, $"{projected.Hop.Ttl}-{hops[last].Hop.Ttl}")
                : projected.Hop.Ttl.ToString(CultureInfo.CurrentCulture);
            string latency = hops[last].Hop.Average is { Length: > 0 } and not "-"
                ? $"  {hops[last].Hop.Average}ms"
                : string.Empty;
            DrawLabel(
                context,
                panel,
                taken,
                projected.Sphere.Position,
                $"{number}  {place}{latency}",
                textBrush,
                background,
                projected.Hop.IsTarget || index == 0 ? 4.8 : 3.2);
            index = last;
        }
    }

    private void DrawLabel(
        DrawingContext context,
        Rect panel,
        List<Rect> taken,
        Point anchor,
        string text,
        IBrush brush,
        IBrush background,
        double radius)
    {
        var label = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            UiTypeface(),
            10,
            brush);
        double gap = radius + 5;
        Point[] candidates =
        [
            new(anchor.X + gap, anchor.Y - (label.Height / 2)),
            new(anchor.X - gap - label.Width, anchor.Y - (label.Height / 2)),
            new(anchor.X - (label.Width / 2), anchor.Y - gap - label.Height),
            new(anchor.X - (label.Width / 2), anchor.Y + gap)
        ];
        foreach (Point candidate in candidates)
        {
            Rect box = new(candidate.X, candidate.Y, label.Width, label.Height);
            if (box.X < panel.X || box.Right > panel.Right || box.Y < panel.Y || box.Bottom > panel.Bottom)
            {
                continue;
            }
            if (taken.Any(existing => existing.Intersects(box)))
            {
                continue;
            }
            if (label.BuildGeometry(candidate) is { } outline)
            {
                context.DrawGeometry(null, new Pen(background, 2.5), outline);
            }
            context.DrawText(label, candidate);
            taken.Add(box.Inflate(2));
            return;
        }
    }

    private List<TraceHopViewModel> LocatedHops()
    {
        List<TraceHopViewModel> result = [];
        if (Hops is null)
        {
            return result;
        }
        foreach (object? item in Hops)
        {
            if (item is TraceHopViewModel { HasLocation: true } hop)
            {
                result.Add(hop);
            }
        }
        return result;
    }

    private sealed class RouteArc
    {
        public RouteArc(SphereVector from, SphereVector to, long now)
        {
            From = from;
            To = to;
            StartTicks = now;
            double angle = Math.Acos(Math.Clamp(SphereVector.Dot(from, to), -1, 1));
            FlightSeconds = 1 + angle * 0.34;
            double height = Math.Min(0.04 + angle * 0.08, angle * 0.24);
            for (int i = 0; i < Lifted.Length; i++)
            {
                double t = i / (double)(Lifted.Length - 1);
                Lifted[i] = Slerp(from, to, t) * (1 + height * Math.Sin(Math.PI * t));
            }
        }

        public SphereVector From { get; }
        public SphereVector To { get; }
        public long StartTicks { get; }
        public double FlightSeconds { get; }
        public SphereVector[] Lifted { get; } = new SphereVector[57];
    }

    private readonly struct Projection(double yaw, double pitch, Point center, double radius)
    {
        private readonly (double Sin, double Cos) _yaw = Math.SinCos(yaw);
        private readonly (double Sin, double Cos) _pitch = Math.SinCos(pitch);

        public SpherePoint Project(SphereVector v)
        {
            double x = v.X * _yaw.Cos - v.Z * _yaw.Sin;
            double z = v.X * _yaw.Sin + v.Z * _yaw.Cos;
            double y = v.Y * _pitch.Cos - z * _pitch.Sin;
            return new(new(center.X + radius * x, center.Y - radius * y), z * _pitch.Cos + v.Y * _pitch.Sin);
        }
    }

    private static SphereVector ToVector(GeoPoint point)
    {
        double latitude = point.Latitude * Math.PI / 180;
        double longitude = point.Longitude * Math.PI / 180;
        double cosLatitude = Math.Cos(latitude);
        return new(-cosLatitude * Math.Cos(longitude), Math.Sin(latitude), cosLatitude * Math.Sin(longitude));
    }

    private static SphereVector Slerp(SphereVector start, SphereVector end, double amount)
    {
        double dot = Math.Clamp(SphereVector.Dot(start, end), -1, 1);
        if (dot > 0.9995)
        {
            return SphereVector.Normalize(start + ((end - start) * amount));
        }
        double angle = Math.Acos(dot);
        double sine = Math.Sin(angle);
        if (Math.Abs(sine) < 0.0001)
        {
            return SphereVector.Normalize(start + ((end - start) * amount));
        }
        double first = Math.Sin((1 - amount) * angle) / sine;
        double second = Math.Sin(amount * angle) / sine;
        return SphereVector.Normalize((start * first) + (end * second));
    }

    private static SurfaceDot[] BuildSurface(IReadOnlyList<double[]> rings)
    {
        List<SurfaceDot> points = [];
        const double step = 2;
        for (double latitude = -90 + step / 2; latitude < 90; latitude += step)
        {
            List<double> intersections = [];
            foreach (double[] ring in rings)
            {
                int count = ring.Length / 2;
                for (int index = 0; index < count; index++)
                {
                    int next = (index + 1) % count;
                    double lon1 = ring[index * 2];
                    double lat1 = ring[(index * 2) + 1];
                    double lon2 = ring[next * 2];
                    double lat2 = ring[(next * 2) + 1];
                    if (Math.Abs(lat2 - lat1) < 0.000001 || latitude < Math.Min(lat1, lat2) || latitude >= Math.Max(lat1, lat2))
                    {
                        continue;
                    }
                    double delta = NormalizeLongitude(lon2 - lon1);
                    double crossing = lon1 + (delta * ((latitude - lat1) / (lat2 - lat1)));
                    intersections.Add(crossing);
                }
            }

            intersections.Sort();
            int columns = Math.Max(1, (int)Math.Round(360 / step * Math.Cos(latitude * Math.PI / 180)));
            int crossingIndex = 0;
            for (int column = 0; column < columns; column++)
            {
                double longitude = -180 + (column + 0.5) * 360 / columns;
                while (crossingIndex < intersections.Count && intersections[crossingIndex] <= longitude)
                {
                    crossingIndex++;
                }
                points.Add(new(ToVector(new(latitude, longitude)), (crossingIndex & 1) != 0));
            }
        }
        return [.. points];
    }

    private static double NormalizeLongitude(double longitude)
    {
        double value = (longitude + 180) % 360;
        return (value < 0 ? value + 360 : value) - 180;
    }

    private static List<double[]> LoadRings(string uri)
    {
        try
        {
            using Stream stream = Avalonia.Platform.AssetLoader.Open(new(uri));
            using StreamReader reader = new(stream);
            List<double[]> rings = [];
            while (reader.ReadLine() is { } line)
            {
                string[] pairs = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (pairs.Length < 4)
                {
                    continue;
                }
                double[] flat = new double[pairs.Length * 2];
                int count = 0;
                foreach (string pair in pairs)
                {
                    int comma = pair.IndexOf(',', StringComparison.Ordinal);
                    if (comma <= 0
                        || !double.TryParse(pair[..comma], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)
                        || !double.TryParse(pair[(comma + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat))
                    {
                        continue;
                    }
                    flat[count++] = lon;
                    flat[count++] = lat;
                }
                if (count >= 8)
                {
                    Array.Resize(ref flat, count);
                    rings.Add(flat);
                }
            }
            return rings;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            return [];
        }
    }

    private Typeface UiTypeface()
    {
        return this.TryFindResource("VelaUiFont", ActualThemeVariant, out object? value) && value is FontFamily family
            ? new Typeface(family)
            : Typeface.Default;
    }

    private IBrush Brush(string key)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush)
        {
            return brush;
        }

        bool light = ActualThemeVariant == ThemeVariant.Light;
        return key switch
        {
            "VelaBgPage" or "VelaTraceLabelBg" => light ? Brushes.WhiteSmoke : Brushes.Black,
            "VelaTraceBorder" => Brushes.SlateGray,
            "VelaTextMuted" => light ? Brushes.DimGray : Brushes.Gray,
            "VelaAccent" => light ? Brushes.Teal : Brushes.Cyan,
            "VelaError" => light ? Brushes.Firebrick : Brushes.Red,
            "VelaTextSecondary" => light ? Brushes.DarkSlateGray : Brushes.LightGray,
            _ => Brushes.Transparent
        };
    }

    private void DrawEmptyHint(DrawingContext context, Rect bounds, IBrush brush, string text)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            UiTypeface(),
            11,
            brush)
        {
            MaxTextWidth = Math.Max(1, bounds.Width - 24)
        };
        context.DrawText(
            formatted,
            new(12, Math.Max(0, bounds.Bottom - formatted.Height - 8)));
    }

    private static double Distance(Point first, Point second)
    {
        double x = first.X - second.X;
        double y = first.Y - second.Y;
        return Math.Sqrt((x * x) + (y * y));
    }

    private readonly record struct SurfaceDot(SphereVector Location, bool IsLand);

    private readonly record struct GeoPoint(double Latitude, double Longitude);

    private readonly record struct ProjectedHop(SpherePoint Sphere, TraceHopViewModel Hop, SphereVector Location);

    private readonly record struct SpherePoint(Point Position, double Depth);

    private readonly record struct SphereVector(double X, double Y, double Z)
    {
        public static double Dot(SphereVector left, SphereVector right) =>
            (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

        public static SphereVector Normalize(SphereVector value)
        {
            double length = Math.Sqrt(Dot(value, value));
            return length < 0.000001 ? new(0, 0, 1) : value * (1 / length);
        }

        public static SphereVector operator +(SphereVector left, SphereVector right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

        public static SphereVector operator -(SphereVector left, SphereVector right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

        public static SphereVector operator *(SphereVector value, double scale) => new(value.X * scale, value.Y * scale, value.Z * scale);
    }
}
