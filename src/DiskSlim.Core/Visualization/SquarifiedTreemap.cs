namespace DiskSlim.Core.Visualization;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double Area => Width * Height;

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>向内收缩；收缩后宽高不会小于 0。</summary>
    public TreemapRect Inset(double left, double top, double right, double bottom) =>
        new(X + left, Y + top, Math.Max(0, Width - left - right), Math.Max(0, Height - top - bottom));
}

/// <summary>
/// Squarified Treemap 布局（Bruls、Huizing、van Wijk，2000）：让每个矩形尽量接近正方形，方便比较大小和放置标签。
/// </summary>
public static class SquarifiedTreemap
{
    /// <summary>把 <paramref name="bounds"/> 按 <paramref name="sizes"/> 的比例切分。</summary>
    /// <param name="sizes">各项大小，应按从大到小排序（不排序也能得到合法的切分，只是不够方正）；负数按 0 处理。</param>
    /// <returns>和 <paramref name="sizes"/> 一一对应的矩形；大小为 0 的项得到面积为 0 的矩形。</returns>
    public static TreemapRect[] Layout(IReadOnlyList<double> sizes, TreemapRect bounds)
    {
        var result = new TreemapRect[sizes.Count];
        var total = sizes.Sum(s => Math.Max(0, s));
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            Array.Fill(result, new TreemapRect(bounds.X, bounds.Y, 0, 0));
            return result;
        }

        var scale = bounds.Area / total;
        var areas = sizes.Select(s => Math.Max(0, s) * scale).ToArray();
        var remaining = bounds;
        var start = 0;
        while (start < areas.Length)
        {
            if (areas[start] <= 0)
            {
                result[start] = new TreemapRect(remaining.X, remaining.Y, 0, 0);
                start++;
                continue;
            }

            // 贪心地往当前这一行里加项目，直到最差的长宽比开始变差。
            var side = Math.Min(remaining.Width, remaining.Height);
            var end = start;
            var rowSum = 0.0;
            var worst = double.MaxValue;
            while (end < areas.Length && areas[end] > 0)
            {
                var candidateSum = rowSum + areas[end];
                var candidateWorst = WorstAspectRatio(areas, start, end, candidateSum, side);
                if (end > start && candidateWorst > worst)
                {
                    break;
                }

                rowSum = candidateSum;
                worst = candidateWorst;
                end++;
            }

            remaining = PlaceRow(areas, start, end, rowSum, remaining, result);
            start = end;
        }

        return result;
    }

    /// <summary>一行中最差的长宽比（≥ 1，越接近 1 越方正）。</summary>
    private static double WorstAspectRatio(double[] areas, int start, int lastIndex, double sum, double side)
    {
        double max = 0, min = double.MaxValue;
        for (var i = start; i <= lastIndex; i++)
        {
            max = Math.Max(max, areas[i]);
            min = Math.Min(min, areas[i]);
        }

        var sideSquared = side * side;
        var sumSquared = sum * sum;
        return Math.Max(sideSquared * max / sumSquared, sumSquared / (sideSquared * min));
    }

    /// <summary>沿较短的一边摆放一行，返回剩下的区域。</summary>
    private static TreemapRect PlaceRow(double[] areas, int start, int end, double sum, TreemapRect bounds, TreemapRect[] result)
    {
        if (bounds.Width >= bounds.Height)
        {
            // 竖着排成一列，放在左边。
            var columnWidth = sum / bounds.Height;
            var y = bounds.Y;
            for (var i = start; i < end; i++)
            {
                var height = areas[i] / columnWidth;
                result[i] = new TreemapRect(bounds.X, y, columnWidth, height);
                y += height;
            }

            return new TreemapRect(bounds.X + columnWidth, bounds.Y, Math.Max(0, bounds.Width - columnWidth), bounds.Height);
        }

        // 横着排成一行，放在上边。
        var rowHeight = sum / bounds.Width;
        var x = bounds.X;
        for (var i = start; i < end; i++)
        {
            var width = areas[i] / rowHeight;
            result[i] = new TreemapRect(x, bounds.Y, width, rowHeight);
            x += width;
        }

        return new TreemapRect(bounds.X, bounds.Y + rowHeight, bounds.Width, Math.Max(0, bounds.Height - rowHeight));
    }
}
