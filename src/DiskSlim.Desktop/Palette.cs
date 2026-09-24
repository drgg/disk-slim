using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DiskSlim.Core.Presentation;
using DiskSlim.Core.Rules;

namespace DiskSlim.Desktop;

/// <summary>界面和方块图共用的颜色。风险颜色和命令行一致：绿 = 可放心，黄 = 需确认，红 = 仅参考。</summary>
internal static class Palette
{
    public static readonly SolidColorBrush Text = Freeze("#1F2937");
    public static readonly SolidColorBrush SubtleText = Freeze("#6B7280");
    public static readonly SolidColorBrush Warning = Freeze("#B45309");

    public static readonly SolidColorBrush LowAccent = Freeze("#2E7D32");
    public static readonly SolidColorBrush LowSoft = Freeze("#EAF5EA");
    public static readonly SolidColorBrush MediumAccent = Freeze("#B26A00");
    public static readonly SolidColorBrush MediumSoft = Freeze("#FFF4E0");
    public static readonly SolidColorBrush HighAccent = Freeze("#C62828");
    public static readonly SolidColorBrush HighSoft = Freeze("#FDECEC");

    public static readonly SolidColorBrush Nothing = Freeze("#9CA3AF");

    /// <summary>
    /// 方块图中普通目录的颜色：只用蓝、紫、灰色系，避开绿、黄、红，免得和风险颜色混淆。
    /// </summary>
    public static readonly Color[] TileColors =
    [
        Parse("#8FB3DE"), Parse("#B4ABDB"), Parse("#A9B6C6"), Parse("#9CC0D8"), Parse("#A7AEE0"), Parse("#B8C2D4"),
    ];

    public static readonly Color TileLow = Parse("#6CC070");
    public static readonly Color TileMedium = Parse("#FFC940");
    public static readonly Color TileHigh = Parse("#F0A0A0");
    public static readonly Color TileOwnFiles = Parse("#CBD2DA");
    public static readonly Color TileOther = Parse("#DEE2E7");
    public static readonly Color CleanableStripe = Parse("#2E7D32");

    public static SolidColorBrush RiskAccent(RiskLevel risk) => risk switch
    {
        RiskLevel.Low => LowAccent,
        RiskLevel.Medium => MediumAccent,
        _ => HighAccent,
    };

    public static SolidColorBrush RiskSoft(RiskLevel risk) => risk switch
    {
        RiskLevel.Low => LowSoft,
        RiskLevel.Medium => MediumSoft,
        _ => HighSoft,
    };

    public static SolidColorBrush Outcome(OutcomeKind kind) => kind switch
    {
        OutcomeKind.Done => LowAccent,
        OutcomeKind.Warning => Warning,
        OutcomeKind.Refused => HighAccent,
        _ => Nothing,
    };

    public static SolidColorBrush Usage(double ratio) => ratio >= 0.9 ? HighAccent : ratio >= 0.75 ? MediumAccent : Freeze("#2563EB");

    /// <summary>向白色混合，<paramref name="amount"/> 为 0～1。</summary>
    public static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)(color.R + ((255 - color.R) * amount)),
        (byte)(color.G + ((255 - color.G) * amount)),
        (byte)(color.B + ((255 - color.B) * amount)));

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush(Parse(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>风险等级 → 颜色。ConverterParameter 为 "soft" 时返回浅色背景。</summary>
public sealed class RiskBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is RiskLevel risk
            ? string.Equals(parameter as string, "soft", StringComparison.OrdinalIgnoreCase) ? Palette.RiskSoft(risk) : Palette.RiskAccent(risk)
            : Palette.SubtleText;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class OutcomeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is OutcomeKind kind ? Palette.Outcome(kind) : Palette.SubtleText;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class UsageBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Palette.Usage(value is double ratio ? ratio : 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>布尔值 → 可见性。ConverterParameter 为 "invert" 时取反。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
