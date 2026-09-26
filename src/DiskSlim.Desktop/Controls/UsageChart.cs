using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskSlim.Core.Formatting;
using DiskSlim.Desktop.ViewModels;

namespace DiskSlim.Desktop.Controls;

public sealed class UsagePointEventArgs(UsagePoint point) : EventArgs
{
    public UsagePoint Point { get; } = point;
}

/// <summary>
/// 磁盘已用空间的趋势线：横轴是扫描时间（按真实时间间隔），纵轴是已用空间。
/// 纵轴不从 0 开始（否则几 GB 的变化看不出来），所以不画面积填充，只画一条线。
/// 悬停显示那次扫描的数字；点击有明细的点，改为和那次扫描比较。
/// </summary>
public sealed class UsageChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<UsagePoint>), typeof(UsageChart),
        new FrameworkPropertyMetadata(Array.Empty<UsagePoint>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BaselineTimeProperty = DependencyProperty.Register(
        nameof(BaselineTime), typeof(DateTimeOffset?), typeof(UsageChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Left = 64;
    private const double Right = 16;
    private const double Top = 14;
    private const double Bottom = 24;
    private const double HitRadius = 24;

    private static readonly Typeface Face = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldFace = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Color Accent = Color.FromRgb(0x25, 0x63, 0xEB);
    private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Accent));
    private static readonly Brush SurfaceBrush = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)));
    private static readonly Brush MutedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF3)), 1));
    private static readonly Pen CrosshairPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xC9, 0xCF, 0xD8)), 1));
    private static readonly Pen LinePen = Frozen(new Pen(AccentBrush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen RingPen = Frozen(new Pen(SurfaceBrush, 2));
    private static readonly Pen TooltipBorder = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE3, 0xE7, 0xED)), 1));
    private static readonly double[] StepsInGb = [0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000];

    private UsagePoint? _hovered;

    public UsageChart()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    /// <summary>点击了一个有明细、可以用来比较的点。</summary>
    public event EventHandler<UsagePointEventArgs>? PointClicked;

    public IReadOnlyList<UsagePoint> Points
    {
        get => (IReadOnlyList<UsagePoint>)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public DateTimeOffset? BaselineTime
    {
        get => (DateTimeOffset?)GetValue(BaselineTimeProperty);
        set => SetValue(BaselineTimeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(SurfaceBrush, null, new Rect(RenderSize));
        var points = Points;
        var plot = PlotArea();
        if (points.Count == 0 || plot.Width < 20 || plot.Height < 20)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var (low, high, step) = YScale(points);

        // 纵轴：几条细的实线网格和刻度。
        for (var value = low; value <= high + step / 2; value += step)
        {
            var y = Math.Round(Y(value, low, high, plot)) + 0.5;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(TickLabel(value, step), 11, MutedBrush, Face, dpi);
            dc.DrawText(label, new Point(plot.Left - 8 - label.Width, y - label.Height / 2));
        }

        // 横轴：第一次和最后一次扫描的日期。
        var first = points[0].Time.ToLocalTime();
        var last = points[^1].Time.ToLocalTime();
        var sameDay = first.Date == last.Date;
        var firstLabel = Text(sameDay ? first.ToString("HH:mm", CultureInfo.InvariantCulture) : first.ToString("M月d日", CultureInfo.InvariantCulture), 11, MutedBrush, Face, dpi);
        var lastLabel = Text(sameDay ? last.ToString("M月d日 HH:mm", CultureInfo.InvariantCulture) : last.ToString("M月d日", CultureInfo.InvariantCulture), 11, MutedBrush, Face, dpi);
        dc.DrawText(firstLabel, new Point(plot.Left, plot.Bottom + 6));
        if (points.Count > 1)
        {
            dc.DrawText(lastLabel, new Point(plot.Right - lastLabel.Width, plot.Bottom + 6));
        }

        if (_hovered is { } hovered)
        {
            var x = Math.Round(X(hovered.Time, points, plot)) + 0.5;
            dc.DrawLine(CrosshairPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }

        // 趋势线。
        if (points.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(Position(points[0], points, plot, low, high), isFilled: false, isClosed: false);
                context.PolyLineTo(points.Skip(1).Select(p => Position(p, points, plot, low, high)).ToList(), isStroked: true, isSmoothJoin: true);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, LinePen, geometry);
        }

        // 只标出两个点：比较对象和这次扫描；其余的数字在悬停提示里。
        var positions = points.Select(p => Position(p, points, plot, low, high)).ToList();
        if (BaselineTime is { } baselineTime && points.ToList().FindIndex(p => p.Time == baselineTime) is var baseline and >= 0)
        {
            DrawMarker(dc, positions, baseline, "比较对象", dpi, plot);
        }

        DrawMarker(dc, positions, positions.Count - 1, "这次 " + ByteSize.Format(points[^1].UsedBytes), dpi, plot);

        if (_hovered is { } h)
        {
            var position = Position(h, points, plot, low, high);
            dc.DrawEllipse(AccentBrush, RingPen, position, 5, 5);
            DrawTooltip(dc, h, position, dpi);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = Nearest(e.GetPosition(this));
        Cursor = hit is { HasDetails: true } && hit != Points.LastOrDefault() ? Cursors.Hand : Cursors.Arrow;
        if (hit != _hovered)
        {
            _hovered = hit;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Nearest(e.GetPosition(this)) is { HasDetails: true } point && point != Points.LastOrDefault())
        {
            PointClicked?.Invoke(this, new UsagePointEventArgs(point));
        }
    }

    private UsagePoint? Nearest(Point mouse)
    {
        var points = Points;
        var plot = PlotArea();
        if (points.Count == 0 || mouse.Y < plot.Top - HitRadius || mouse.Y > plot.Bottom + HitRadius)
        {
            return null;
        }

        UsagePoint? best = null;
        var bestDistance = HitRadius;
        foreach (var point in points)
        {
            var distance = Math.Abs(X(point.Time, points, plot) - mouse.X);
            if (distance <= bestDistance)
            {
                best = point;
                bestDistance = distance;
            }
        }

        return best;
    }

    private Rect PlotArea() => new(Left, Top, Math.Max(0, ActualWidth - Left - Right), Math.Max(0, ActualHeight - Top - Bottom));

    private static Point Position(UsagePoint point, IReadOnlyList<UsagePoint> points, Rect plot, double low, double high) =>
        new(X(point.Time, points, plot), Y(point.UsedBytes, low, high, plot));

    private static double X(DateTimeOffset time, IReadOnlyList<UsagePoint> points, Rect plot)
    {
        var span = (points[^1].Time - points[0].Time).TotalSeconds;
        return span <= 0 ? plot.Left + plot.Width / 2 : plot.Left + plot.Width * (time - points[0].Time).TotalSeconds / span;
    }

    private static double Y(double value, double low, double high, Rect plot) => plot.Bottom - plot.Height * (value - low) / (high - low);

    /// <summary>纵轴范围取整到好读的刻度（例如 180 GB、182 GB、184 GB），最多 5 条刻度线。</summary>
    internal static (double Low, double High, double Step) YScale(IReadOnlyList<UsagePoint> points)
    {
        const double Gb = 1L << 30;
        var min = points.Min(p => p.UsedBytes) / Gb;
        var max = points.Max(p => p.UsedBytes) / Gb;
        foreach (var step in StepsInGb)
        {
            var low = Math.Floor(min / step) * step;
            var high = Math.Ceiling(max / step) * step;
            if (high - low < step)
            {
                high = low + step;
            }

            if ((high - low) / step <= 4)
            {
                return (low * Gb, high * Gb, step * Gb);
            }
        }

        return (0, Math.Ceiling(max / 1000) * 1000 * Gb, 1000 * Gb);
    }

    /// <summary>刻度文字的小数位跟着刻度间隔走：间隔 0.1 GB 时显示 “197.1 GB”，而不是一排 “197 GB”。</summary>
    internal static string TickLabel(double bytes, double step)
    {
        const double Gb = 1L << 30;
        var decimals = Math.Clamp((int)Math.Ceiling(-Math.Log10(step / Gb) - 1e-9), 0, 2);
        return (bytes / Gb).ToString("N" + decimals, CultureInfo.InvariantCulture) + " GB";
    }

    /// <summary>
    /// 画一个带白色描边的点和它的文字。文字放在线不经过的一侧：相邻的点在上方时放到下方，
    /// 上下都放不下时放到左右两侧。
    /// </summary>
    private static void DrawMarker(DrawingContext dc, IReadOnlyList<Point> positions, int index, string label, double dpi, Rect plot)
    {
        var position = positions[index];
        dc.DrawEllipse(AccentBrush, RingPen, position, 4, 4);
        var text = Text(label, 11, TextBrush, Face, dpi);

        var neighbors = new[] { index - 1, index + 1 }.Where(i => i >= 0 && i < positions.Count).Select(i => positions[i]).ToList();
        var lineAbove = neighbors.Any(n => n.Y < position.Y - 1);
        var lineBelow = neighbors.Any(n => n.Y > position.Y + 1);
        var aboveY = position.Y - text.Height - 8;
        var belowY = position.Y + 8;
        var fitsAbove = aboveY >= plot.Top - 10;
        var fitsBelow = belowY + text.Height <= plot.Bottom;

        Point origin;
        if (!lineAbove && fitsAbove)
        {
            origin = new Point(position.X - text.Width / 2, aboveY);
        }
        else if (!lineBelow && fitsBelow)
        {
            origin = new Point(position.X - text.Width / 2, belowY);
        }
        else
        {
            var right = position.X < plot.Left + plot.Width / 2;
            origin = new Point(right ? position.X + 10 : position.X - 10 - text.Width, position.Y - text.Height / 2);
        }

        origin.X = Math.Clamp(origin.X, plot.Left - Left + 4, plot.Right - text.Width);
        dc.DrawText(text, origin);
    }

    private void DrawTooltip(DrawingContext dc, UsagePoint point, Point anchor, double dpi)
    {
        var title = Text(point.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), 12, TextBrush, BoldFace, dpi);
        var body = Text($"已用 {ByteSize.Format(point.UsedBytes)} · 剩余 {ByteSize.Format(point.FreeBytes)}", 12, TextBrush, Face, dpi);
        var hint = point == Points.LastOrDefault() ? "这次扫描"
            : point.HasDetails ? point.Time == BaselineTime ? "正在和这次比较" : "点击改为和这次比较"
            : "明细已按保留期限删除，不能比较";
        var hintText = Text(hint, 11, MutedBrush, Face, dpi);

        var width = Math.Max(title.Width, Math.Max(body.Width, hintText.Width)) + 20;
        var height = title.Height + body.Height + hintText.Height + 16;
        var x = anchor.X + 12 + width > ActualWidth ? anchor.X - 12 - width : anchor.X + 12;
        var y = Math.Clamp(anchor.Y - height / 2, 2, Math.Max(2, ActualHeight - height - 2));
        dc.DrawRoundedRectangle(SurfaceBrush, TooltipBorder, new Rect(x, y, width, height), 6, 6);
        dc.DrawText(title, new Point(x + 10, y + 6));
        dc.DrawText(body, new Point(x + 10, y + 6 + title.Height + 2));
        dc.DrawText(hintText, new Point(x + 10, y + 6 + title.Height + body.Height + 4));
    }

    private static FormattedText Text(string text, double size, Brush brush, Typeface face, double dpi) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, dpi);

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
