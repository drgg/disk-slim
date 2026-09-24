using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Rules;
using DiskSlim.Core.Scanning;
using DiskSlim.Core.Visualization;

namespace DiskSlim.Desktop.Controls;

public sealed class TreemapTileEventArgs(TreemapTile? tile) : EventArgs
{
    public TreemapTile? Tile { get; } = tile;
}

/// <summary>
/// 方块图：每个方块的面积代表大小。普通目录用柔和的颜色，规则命中的目录用风险颜色；
/// 方块底部的绿色细条表示里面有多少可以清理。直接用 DrawingContext 绘制，几百个方块也很流畅。
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty NodeProperty = DependencyProperty.Register(
        nameof(Node), typeof(DirNode), typeof(TreemapControl), new FrameworkPropertyMetadata(null, OnLayoutInputChanged));

    public static readonly DependencyProperty IndexProperty = DependencyProperty.Register(
        nameof(Index), typeof(CleanableIndex), typeof(TreemapControl), new FrameworkPropertyMetadata(CleanableIndex.Empty, OnLayoutInputChanged));

    private static readonly Typeface LabelFace = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface HeaderFace = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Pen TileBorder = Frozen(new Pen(Brushes.White, 1));
    private static readonly Pen HoverBorder = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)), 2));
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)));
    private static readonly Brush SubtleLabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)));
    private static readonly Brush StripeBrush = Frozen(new SolidColorBrush(Palette.CleanableStripe));
    private static readonly Brush EmptyBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF9)));

    private readonly Dictionary<Color, Brush> _brushes = [];
    private IReadOnlyList<TreemapTile> _tiles = [];
    private Size _laidOutFor;
    private TreemapTile? _hovered;

    public TreemapControl()
    {
        ClipToBounds = true;
        Focusable = false;
        Cursor = Cursors.Hand;
    }

    /// <summary>点击了一个方块。</summary>
    public event EventHandler<TreemapTileEventArgs>? TileClicked;

    /// <summary>鼠标下的方块变了（离开时为 null）。</summary>
    public event EventHandler<TreemapTileEventArgs>? HoveredTileChanged;

    public DirNode? Node
    {
        get => (DirNode?)GetValue(NodeProperty);
        set => SetValue(NodeProperty, value);
    }

    public CleanableIndex Index
    {
        get => (CleanableIndex)GetValue(IndexProperty);
        set => SetValue(IndexProperty, value);
    }

    public TreemapTile? HoveredTile => _hovered;

    /// <summary>最近一次右键点中的方块（弹出菜单后鼠标会离开控件，所以单独记下）。</summary>
    public TreemapTile? ContextTile { get; private set; }

    public IReadOnlyList<TreemapTile> Tiles => _tiles;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(EmptyBrush, null, new Rect(RenderSize));
        EnsureLayout();
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var tile in _tiles)
        {
            var rect = ToRect(tile.Bounds);
            if (rect.Width < 1 || rect.Height < 1)
            {
                continue;
            }

            dc.DrawRectangle(BrushFor(tile), TileBorder, rect);
            DrawCleanableStripe(dc, tile, rect);
            DrawLabel(dc, tile, rect, pixelsPerDip);
        }

        if (_hovered is { } hovered)
        {
            var rect = ToRect(hovered.Bounds);
            rect.Inflate(-1, -1);
            if (rect.Width > 0 && rect.Height > 0)
            {
                dc.DrawRectangle(null, HoverBorder, rect);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        SetHovered(HitTest(position.X, position.Y));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHovered(null);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var position = e.GetPosition(this);
        if (HitTest(position.X, position.Y) is { } tile)
        {
            TileClicked?.Invoke(this, new TreemapTileEventArgs(tile));
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        var position = e.GetPosition(this);
        ContextTile = HitTest(position.X, position.Y);
    }

    /// <summary>找到坐标所在的最内层方块。</summary>
    public TreemapTile? HitTest(double x, double y)
    {
        // 父方块总是排在子方块前面，倒着找就能先找到最内层的。
        for (var i = _tiles.Count - 1; i >= 0; i--)
        {
            if (_tiles[i].Bounds.Contains(x, y))
            {
                return _tiles[i];
            }
        }

        return null;
    }

    private static void OnLayoutInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TreemapControl)d;
        control._laidOutFor = default;
        control.SetHovered(null);
        control.InvalidateVisual();
    }

    private void EnsureLayout()
    {
        if (_laidOutFor == RenderSize)
        {
            return;
        }

        _laidOutFor = RenderSize;
        _tiles = Node is { } node && RenderSize.Width > 0 && RenderSize.Height > 0
            ? new TreemapBuilder { Index = Index }.Build(node, new TreemapRect(0, 0, RenderSize.Width, RenderSize.Height))
            : [];
    }

    private void SetHovered(TreemapTile? tile)
    {
        if (ReferenceEquals(tile, _hovered))
        {
            return;
        }

        _hovered = tile;
        InvalidateVisual();
        HoveredTileChanged?.Invoke(this, new TreemapTileEventArgs(tile));
    }

    private Brush BrushFor(TreemapTile tile)
    {
        var baseColor = tile.Kind switch
        {
            TileKind.OwnFiles => Palette.TileOwnFiles,
            TileKind.Other => Palette.TileOther,
            _ => tile.Risk switch
            {
                RiskLevel.Low => Palette.TileLow,
                RiskLevel.Medium => Palette.TileMedium,
                RiskLevel.High => Palette.TileHigh,
                _ => Palette.TileColors[tile.ColorIndex % Palette.TileColors.Length],
            },
        };

        // 第二层比第一层浅一些，看起来像“装在里面”。
        var color = tile.Depth > 1 ? Palette.Lighten(baseColor, 0.35) : baseColor;
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = Frozen(new SolidColorBrush(color));
            _brushes[color] = brush;
        }

        return brush;
    }

    private static void DrawCleanableStripe(DrawingContext dc, TreemapTile tile, Rect rect)
    {
        if (tile.CleanableBytes <= 0 || tile.Bytes <= 0 || tile.Risk is not null || rect.Height < 12)
        {
            return;
        }

        var ratio = Math.Clamp((double)tile.CleanableBytes / tile.Bytes, 0, 1);
        var width = Math.Max(3, (rect.Width - 2) * ratio);
        dc.DrawRectangle(StripeBrush, null, new Rect(rect.X + 1, rect.Bottom - 4, width, 3));
    }

    private static void DrawLabel(DrawingContext dc, TreemapTile tile, Rect rect, double pixelsPerDip)
    {
        var hasChildrenDrawn = tile.Depth == 1 && rect.Width >= 70 && rect.Height >= 48 && tile.Node is { Children.Count: > 0 };
        if (rect.Width < 46 || rect.Height < 18)
        {
            return;
        }

        var text = tile.Label;
        if (rect.Width >= 90)
        {
            text += "  " + ByteSize.Format(tile.Bytes);
        }

        var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            hasChildrenDrawn ? HeaderFace : LabelFace, tile.Depth == 1 ? 12 : 11, hasChildrenDrawn ? LabelBrush : SubtleLabelBrush, pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, rect.Width - 8),
            MaxTextHeight = 16,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1,
        };
        dc.DrawText(label, new Point(rect.X + 4, rect.Y + 2));
    }

    private static Rect ToRect(TreemapRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
