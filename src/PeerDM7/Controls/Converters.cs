using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PeerDM7.Core;

namespace PeerDM7.Controls;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class EmptyToCollapsed : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        null => Visibility.Collapsed,
        string s when s.Length == 0 => Visibility.Collapsed,
        int i when i == 0 => Visibility.Collapsed,
        _ => Visibility.Visible,
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>
/// Status colours are semantic and identical in both themes. Frames are the glossy borders
/// around display pictures; Buddy is the colour of the little aero guy in the list.
/// </summary>
public sealed class StatusToBrush : IValueConverter
{
    public static Color BuddyColor(PeerStatus s) => s switch
    {
        PeerStatus.Online => Rgb("#3FAE2A"),
        PeerStatus.Away => Rgb("#F08A1C"),
        PeerStatus.Busy => Rgb("#D9362B"),
        _ => Rgb("#8D96A3"),
    };

    static readonly Dictionary<PeerStatus, Brush> Frames = Enum.GetValues<PeerStatus>().ToDictionary(s => s, s =>
    {
        var c = BuddyColor(s);
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(BuddyView.Tint(c, 0.6), 0));
        b.GradientStops.Add(new GradientStop(c, 0.5));
        b.GradientStops.Add(new GradientStop(BuddyView.Shade(c, 0.75), 1));
        b.Freeze();
        return (Brush)b;
    });

    public static Brush For(PeerStatus s) => Frames[s];
    public static Brush Online => Frames[PeerStatus.Online];

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var s = value is PeerStatus ps ? ps : PeerStatus.Offline;
        return p as string == "buddy" ? BuddyColor(s) : For(s);
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();

    static Color Rgb(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}

/// <summary>The status frame sits outside the picture, so its corners need the extra padding added.</summary>
public sealed class RadiusConverter : IValueConverter
{
    public static readonly RadiusConverter Outer = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        new CornerRadius(value is double d ? d + 3 : 9);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToVisible : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
