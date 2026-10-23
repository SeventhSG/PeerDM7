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

/// <summary>Status colours are semantic and identical in both themes, so they live here, not in the theme files.</summary>
public sealed class StatusToBrush : IValueConverter
{
    public static readonly Brush Online = Frozen("#2EA043");
    public static readonly Brush Away = Frozen("#D4A017");
    public static readonly Brush Busy = Frozen("#D73A35");
    public static readonly Brush Offline = Frozen("#8D94A0");

    public static Brush For(PeerStatus s) => s switch
    {
        PeerStatus.Online => Online,
        PeerStatus.Away => Away,
        PeerStatus.Busy => Busy,
        _ => Offline,
    };

    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is PeerStatus s ? For(s) : Offline;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();

    static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>The status frame sits outside the picture, so its corners need the extra padding added.</summary>
public sealed class RadiusConverter : IValueConverter
{
    public static readonly RadiusConverter Outer = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        new CornerRadius(value is double d ? d + 3 : 9);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
