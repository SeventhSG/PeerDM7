using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PeerDM7.Controls;

/// <summary>
/// Display picture in a glossy status-coloured frame, the signature element of the old
/// messenger. With no picture it shows an aero guy in the person's default colour, or a
/// group glyph.
/// </summary>
public sealed class AvatarView : Control
{
    static AvatarView() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(AvatarView), new FrameworkPropertyMetadata(typeof(AvatarView)));

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(AvatarView));
    public static readonly DependencyProperty BuddyColorProperty =
        DependencyProperty.Register(nameof(BuddyColor), typeof(Color), typeof(AvatarView), new PropertyMetadata(BuddyView.Palette[0]));
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(AvatarView), new PropertyMetadata(""));
    public static readonly DependencyProperty RingProperty =
        DependencyProperty.Register(nameof(Ring), typeof(Brush), typeof(AvatarView));
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.Register(nameof(Radius), typeof(double), typeof(AvatarView), new PropertyMetadata(6.0));
    public static readonly DependencyProperty RingThicknessProperty =
        DependencyProperty.Register(nameof(RingThickness), typeof(Thickness), typeof(AvatarView), new PropertyMetadata(new Thickness(3)));

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public Color BuddyColor { get => (Color)GetValue(BuddyColorProperty); set => SetValue(BuddyColorProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public Brush? Ring { get => (Brush?)GetValue(RingProperty); set => SetValue(RingProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public Thickness RingThickness { get => (Thickness)GetValue(RingThicknessProperty); set => SetValue(RingThicknessProperty, value); }
}

public static class Hint
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Hint), new PropertyMetadata(""));
    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string v) => d.SetValue(TextProperty, v);
}
