using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 团队控制台里反复用到的小积木:按令牌取色的文字、图标、半透明色底。
/// </summary>
/// <remarks>
/// 颜色一律绑 <c>Vela*</c> 令牌(<see cref="DynamicResourceExtension" />),不取一次性的值 ——
/// 控件建出来时多半还没进可视树,那时查不到宿主资源;绑上去才会在进树后解析,也才跟着主题变。
/// </remarks>
internal static class TeamUi
{
    /// <summary>一段文字:字号与颜色都走令牌。</summary>
    public static TextBlock Text(string text, string colorKey = "VelaTextPrimary", int size = 12,
        FontWeight weight = FontWeight.Normal, bool mono = false)
    {
        var block = new TextBlock { Text = text, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center };
        block[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize" + size);
        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(colorKey);
        if (mono) block[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiMonoFont");
        return block;
    }

    /// <summary>单行、放不下就省略的文字。</summary>
    public static TextBlock Line(string text, string colorKey = "VelaTextPrimary", int size = 12,
        FontWeight weight = FontWeight.Normal, bool mono = false)
    {
        TextBlock block = Text(text, colorKey, size, weight, mono);
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        block.MaxLines = 1;
        return block;
    }

    /// <summary>lucide 描边图标(24 视图框等比缩放)。</summary>
    public static Viewbox Icon(string geometryKey, string brushKey, double size)
    {
        var path = new Path
        {
            Width = 24,
            Height = 24,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        path[!Path.DataProperty] = new DynamicResourceExtension(geometryKey);
        path[!Shape.StrokeProperty] = new DynamicResourceExtension(brushKey);
        return new Viewbox { Width = size, Height = size, Child = path, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>
    /// 半透明色底上的内容(状态药丸、类型标签):底是 <paramref name="colorKey" /> 按 <paramref name="opacity" /> 调淡的一层。
    /// 令牌里只有少数颜色带现成的 Dim 变体,所以统一叠一层带不透明度的底,而不是去找各色的淡色键。
    /// </summary>
    public static Panel Tint(Control content, string colorKey, double opacity, double radius, Thickness padding)
    {
        var wash = new Border { CornerRadius = new CornerRadius(radius), Opacity = opacity };
        wash[!Border.BackgroundProperty] = new DynamicResourceExtension(colorKey);
        var panel = new Panel();
        panel.Children.Add(wash);
        panel.Children.Add(new Border { Padding = padding, Child = content });
        return panel;
    }

    /// <summary>状态药丸:色底 + 小圆点 + 文字,同一个颜色。</summary>
    public static Panel Pill(string text, string colorKey)
    {
        var dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        dot[!Shape.FillProperty] = new DynamicResourceExtension(colorKey);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        row.Children.Add(dot);
        row.Children.Add(Text(text, colorKey, 11));
        Panel pill = Tint(row, colorKey, 0.16, 99, new Thickness(8, 1));
        pill.HorizontalAlignment = HorizontalAlignment.Left;
        pill.VerticalAlignment = VerticalAlignment.Center;
        return pill;
    }

    /// <summary>类型标签:小圆角色底 + 同色文字。</summary>
    public static Panel Tag(string text, string colorKey)
    {
        TextBlock label = Text(text, colorKey, 11);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        Panel tag = Tint(label, colorKey, 0.18, 5, new Thickness(7, 1));
        tag.HorizontalAlignment = HorizontalAlignment.Left;
        tag.VerticalAlignment = VerticalAlignment.Center;
        return tag;
    }

    /// <summary>描边小标签(模型名、任务 id):细边框 + 等宽字。</summary>
    public static Border Chip(string text, string colorKey = "VelaTextSecondary", string borderKey = "VelaBorderSecondary",
        double radius = 99)
    {
        var chip = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(7, 0),
            Child = Text(text, colorKey, 11, mono: true),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        chip[!Border.BorderBrushProperty] = new DynamicResourceExtension(borderKey);
        return chip;
    }

    /// <summary>把画刷属性绑到令牌上。</summary>
    public static T Bind<T>(this T target, AvaloniaProperty property, string key) where T : AvaloniaObject
    {
        target[!property] = new DynamicResourceExtension(key);
        return target;
    }

    /// <summary>挂到网格的某一列。</summary>
    public static T At<T>(this T control, int column, int row = 0) where T : Control
    {
        Grid.SetColumn(control, column);
        Grid.SetRow(control, row);
        return control;
    }
}

/// <summary>成员头像长什么样:主色、底色,以及眼睛与头顶配件的款式。</summary>
/// <param name="ColorKey">主色令牌。</param>
/// <param name="WashKey">圆形底色令牌(主色的淡色变体)。</param>
/// <param name="Eyes">眼睛款式:visor / lens / square / glasses / pixel / dots / scan。</param>
/// <param name="Accessory">头顶配件:crown / dish / antenna / feather / headphones / bolt / shield。</param>
internal sealed record TeamFace(string ColorKey, string WashKey, string Eyes, string Accessory)
{
    private static readonly string[] MemberEyes = ["lens", "square", "glasses", "pixel", "dots", "scan"];
    private static readonly string[] MemberAccessories = ["dish", "antenna", "feather", "headphones", "bolt", "shield"];

    /// <summary>主控:中性色、戴皇冠(不占成员的身份色)。</summary>
    public static TeamFace Lead { get; } = new("VelaTextSecondary", "VelaBgHover", "visor", "crown");

    /// <summary>「全体」「引擎」这类不是某个人的一端:中性色、最朴素的款式。</summary>
    public static TeamFace Neutral { get; } = new("VelaTextTertiary", "VelaBgHover", "dots", "antenna");

    /// <summary>成员:身份色按序号轮换,眼睛与配件也按序号轮换,六名成员各不相同。</summary>
    public static TeamFace Member(string colorKey, int index)
        => new(colorKey, colorKey + "Dim", MemberEyes[index % MemberEyes.Length], MemberAccessories[index % MemberAccessories.Length]);
}

/// <summary>
/// 成员头像:一个圆形底上的小机器人,主色是成员的身份色,眼睛与头顶配件各人不同 —— 不看名字也认得出谁是谁。
/// 全部用矢量形状拼出来(100×100 的画布等比缩放),任何尺寸都清楚。
/// </summary>
internal static class TeamAvatar
{
    /// <summary>面罩用的底色(深色主题下是深的,浅色主题下是浅的,眼睛的主色在两种底上都看得清)。</summary>
    private const string Plate = "VelaBgPage";

    /// <summary>高光与浅色配件。</summary>
    private const string Light = "VelaTextPrimary";

    /// <summary>指定边长的头像。</summary>
    public static Viewbox Create(TeamFace face, double size)
        => new() { Width = size, Height = size, Child = Build(face), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>100×100 的头像画布(拓扑图里直接放进节点,由节点自己缩放)。</summary>
    public static Canvas Build(TeamFace face)
    {
        string c = face.ColorKey;
        var canvas = new Canvas { Width = 100, Height = 100 };
        void Add(Control shape) => canvas.Children.Add(shape);

        Add(Circle(50, 50, 50, face.WashKey));
        Add(Ring(50, 50, 48.5, c, 2, 0.55));

        switch (face.Accessory)
        {
            case "crown":
                Add(Shape("M36 33 L38 17 L45 25 L50 12 L55 25 L62 17 L64 33 Z", fill: "VelaShellYellow", stroke: "VelaWarning", thickness: 1));
                break;
            case "antenna":
                Add(Stroke("M50 33 L50 18", c, 3));
                Add(Circle(50, 16, 4.5, Light));
                break;
            case "dish":
                Add(Stroke("M50 33 L50 24", c, 3));
                Path dish = Shape("M37 24 a13 13 0 0 0 26 0 z", fill: c);
                dish.RenderTransform = new RotateTransform(-18, 50, 24);
                dish.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
                Add(dish);
                Add(Circle(50, 18, 2, Light));
                break;
            case "feather":
                Path feather = Shape("M51 34 C 49 22, 58 12, 70 9 C 68 20, 61 29, 51 34 Z", fill: Light);
                feather.Opacity = 0.92;
                Add(feather);
                Add(Stroke("M51 34 L66 14", c, 1.3));
                break;
            case "bolt":
                Add(Shape("M53 11 L43 26 L50 26 L46 36 L59 20 L52 20 L57 11 Z", fill: "VelaShellYellow", stroke: "VelaWarning", thickness: 0.8));
                break;
            case "shield":
                Path shield = Shape("M50 13 L61 17 L60 27 C59 31 55 34 50 36 C45 34 41 31 40 27 L39 17 Z", fill: Light);
                shield.Opacity = 0.92;
                Add(shield);
                Add(Stroke("M45 24 l4 4 l7 -7", c, 2.2));
                break;
        }

        // 头
        Add(Box(26, 32, 48, 44, 15, c));
        if (face.Accessory == "headphones")
        {
            Path band = Stroke("M19 58 A31 31 0 0 1 81 58", Light, 4);
            band.Opacity = 0.85;
            Add(band);
            Add(Box(14, 50, 11, 19, 5, Light));
            Add(Box(75, 50, 11, 19, 5, Light));
        }
        else
        {
            Add(Box(20, 48, 7, 13, 3, c));
            Add(Box(73, 48, 7, 13, 3, c));
        }

        // 面罩与眼睛
        Add(Box(31, 42, 38, 22, 9, Plate));
        switch (face.Eyes)
        {
            case "visor":
                Add(Box(35, 49, 30, 7, 3.5, c));
                Add(Box(37, 50, 10, 2, 1, Light, 0.8));
                break;
            case "dots":
                Add(Circle(42, 53, 3.8, c));
                Add(Circle(58, 53, 3.8, c));
                Add(Circle(43, 52, 1.2, Light));
                Add(Circle(59, 52, 1.2, Light));
                break;
            case "square":
                Add(Box(37, 48, 9, 9, 2, c));
                Add(Box(54, 48, 9, 9, 2, c));
                break;
            case "lens":
                Add(Ring(42, 53, 6, c, 2, 1));
                Add(Circle(42, 53, 2.4, Light));
                Add(Circle(59, 53, 3.4, c));
                break;
            case "glasses":
                Add(Ring(42, 53, 5.6, Light, 1.6, 1));
                Add(Ring(58, 53, 5.6, Light, 1.6, 1));
                Add(Stroke("M47.6 53 L52.4 53", Light, 1.6));
                Add(Circle(42, 53, 2, c));
                Add(Circle(58, 53, 2, c));
                break;
            case "pixel":
                foreach ((double x, double y) in ((double, double)[])[(37, 48), (55, 48)])
                {
                    Add(Box(x, y, 3.6, 3.6, 0, c));
                    Add(Box(x + 4.2, y, 3.6, 3.6, 0, c, 0.55));
                    Add(Box(x, y + 4.2, 3.6, 3.6, 0, c, 0.55));
                    Add(Box(x + 4.2, y + 4.2, 3.6, 3.6, 0, c));
                }
                break;
            default: // scan
                Add(Box(35, 51, 30, 3, 1.5, c, 0.35));
                Add(Circle(50, 52.5, 3.3, c));
                break;
        }
        // 嘴
        Add(Box(44, 68, 12, 3, 1.5, Plate, 0.5));
        return canvas;
    }

    private static Ellipse Circle(double cx, double cy, double r, string fillKey)
    {
        var circle = new Ellipse { Width = r * 2, Height = r * 2 };
        circle[!Avalonia.Controls.Shapes.Shape.FillProperty] = new DynamicResourceExtension(fillKey);
        Canvas.SetLeft(circle, cx - r);
        Canvas.SetTop(circle, cy - r);
        return circle;
    }

    private static Ellipse Ring(double cx, double cy, double r, string strokeKey, double thickness, double opacity)
    {
        // Ellipse 的描边画在边界以内:外径要把半个线宽补回来,圆心线才落在 r 上
        double outer = r + (thickness / 2);
        var ring = new Ellipse { Width = outer * 2, Height = outer * 2, StrokeThickness = thickness, Opacity = opacity };
        ring[!Avalonia.Controls.Shapes.Shape.StrokeProperty] = new DynamicResourceExtension(strokeKey);
        Canvas.SetLeft(ring, cx - outer);
        Canvas.SetTop(ring, cy - outer);
        return ring;
    }

    private static Rectangle Box(double x, double y, double width, double height, double radius, string fillKey, double opacity = 1)
    {
        var box = new Rectangle { Width = width, Height = height, RadiusX = radius, RadiusY = radius, Opacity = opacity };
        box[!Avalonia.Controls.Shapes.Shape.FillProperty] = new DynamicResourceExtension(fillKey);
        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, y);
        return box;
    }

    private static Path Shape(string data, string? fill = null, string? stroke = null, double thickness = 0)
    {
        var path = new Path { Data = Geometry.Parse(data), StrokeThickness = thickness, StrokeJoin = PenLineJoin.Round };
        if (fill is not null) path[!Avalonia.Controls.Shapes.Shape.FillProperty] = new DynamicResourceExtension(fill);
        if (stroke is not null) path[!Avalonia.Controls.Shapes.Shape.StrokeProperty] = new DynamicResourceExtension(stroke);
        return path;
    }

    private static Path Stroke(string data, string strokeKey, double thickness)
    {
        Path path = Shape(data, stroke: strokeKey, thickness: thickness);
        path.StrokeLineCap = PenLineCap.Round;
        return path;
    }
}

/// <summary>
/// 一条走势:折线 + 线下的淡色面积,纵轴从 0 到序列里的最大值。迷你走势(成员卡)与速率图(用量面板)都用它。
/// </summary>
internal sealed class TeamSparkline : Control
{
    /// <summary>线与面积的颜色。</summary>
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<TeamSparkline, IBrush?>(nameof(Stroke));

    /// <summary>中线(速率图上那条虚线)的颜色;不给就不画。</summary>
    public static readonly StyledProperty<IBrush?> GuideProperty =
        AvaloniaProperty.Register<TeamSparkline, IBrush?>(nameof(Guide));

    static TeamSparkline() => AffectsRender<TeamSparkline>(StrokeProperty, GuideProperty);

    /// <summary>线与面积的颜色。</summary>
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>中线的颜色。</summary>
    public IBrush? Guide
    {
        get => GetValue(GuideProperty);
        set => SetValue(GuideProperty, value);
    }

    /// <summary>线宽。</summary>
    public double Thickness { get; init; } = 1.5;

    /// <summary>纵向留多少顶部余量(1 = 最高点贴顶)。</summary>
    public double Headroom { get; init; } = 1;

    /// <summary>要画的序列。</summary>
    public IReadOnlyList<double> Values
    {
        get;
        set
        {
            field = [.. value];
            InvalidateVisual();
        }
    } = [];

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width, height = Bounds.Height;
        if (width <= 0 || height <= 0) return;
        if (Guide is { } guide)
        {
            context.DrawLine(new Pen(guide, 1, new DashStyle([3, 4], 0)), new Point(0, height / 2), new Point(width, height / 2));
        }
        if (Stroke is not { } stroke || Values.Count < 2) return;
        double max = Math.Max(1, Values.Max()) * Headroom;
        var points = new Point[Values.Count];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new Point(i / (double)(points.Length - 1) * width,
                height - 1 - (Math.Clamp(Values[i] / max, 0, 1) * (height - 2)));
        }
        var area = new StreamGeometry();
        using (StreamGeometryContext g = area.Open())
        {
            g.BeginFigure(new Point(0, height), true);
            foreach (Point p in points) g.LineTo(p);
            g.LineTo(new Point(width, height));
            g.EndFigure(true);
        }
        using (context.PushOpacity(0.16))
        {
            context.DrawGeometry(stroke, null, area);
        }
        var line = new StreamGeometry();
        using (StreamGeometryContext g = line.Open())
        {
            g.BeginFigure(points[0], false);
            for (int i = 1; i < points.Length; i++) g.LineTo(points[i]);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(stroke, Thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
    }
}
