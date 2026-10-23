using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PeerDM7.Controls;

/// <summary>
/// The glossy "aero guy": a round head over rounded shoulders, lit from the top left.
/// Used as the status icon in lists and as the built-in display pictures.
/// Drawn on a 20 by 20 grid and scaled.
/// </summary>
public sealed class BuddyView : FrameworkElement
{
    public static readonly DependencyProperty ColorProperty =
        DependencyProperty.Register(nameof(Color), typeof(Color), typeof(BuddyView),
            new FrameworkPropertyMetadata(Colors.ForestGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var s = Math.Min(ActualWidth, ActualHeight) / 20.0;
        if (s <= 0) return;
        var offsetX = (ActualWidth - 20 * s) / 2;
        dc.PushTransform(new TranslateTransform(offsetX, 0));
        dc.PushTransform(new ScaleTransform(s, s));
        Draw(dc, Color);
        dc.Pop();
        dc.Pop();
    }

    static readonly Geometry Body = Freeze(Geometry.Parse("M2.6,19.4 Q2.4,11.6 10,11.4 Q17.6,11.6 17.4,19.4 Q10,20.4 2.6,19.4 Z"));
    static readonly Geometry BodyGloss = Freeze(Geometry.Parse("M4.6,16.2 Q5,12.8 10,12.6 Q15,12.8 15.4,16.2 Q10,14.6 4.6,16.2 Z"));
    static readonly Brush GlossBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x9C, 0xFF, 0xFF, 0xFF)));

    public static void Draw(DrawingContext dc, Color c)
    {
        var edge = new Pen(new SolidColorBrush(Shade(c, 0.55)), 0.7);
        dc.DrawGeometry(Orb(c, new Point(0.35, 0.15)), edge, Body);
        dc.DrawGeometry(GlossBrush, null, BodyGloss);
        dc.DrawEllipse(Orb(c, new Point(0.32, 0.25)), edge, new Point(10, 6.3), 4.4, 4.4);
        dc.DrawEllipse(GlossBrush, null, new Point(9.2, 4.4), 2.5, 1.5);
    }

    static Brush Orb(Color c, Point origin)
    {
        var b = new RadialGradientBrush { GradientOrigin = origin, Center = new Point(0.45, 0.4), RadiusX = 0.7, RadiusY = 0.7 };
        b.GradientStops.Add(new GradientStop(Tint(c, 0.55), 0));
        b.GradientStops.Add(new GradientStop(c, 0.55));
        b.GradientStops.Add(new GradientStop(Shade(c, 0.7), 1));
        b.Freeze();
        return b;
    }

    public static Color Tint(Color c, double t) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

    public static Color Shade(Color c, double f) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // ---- built-in display pictures ----

    public static readonly Color[] Palette =
    [
        (Color)ColorConverter.ConvertFromString("#3FAE2A"),
        (Color)ColorConverter.ConvertFromString("#2F7FE0"),
        (Color)ColorConverter.ConvertFromString("#F08A1C"),
        (Color)ColorConverter.ConvertFromString("#8A4FD8"),
        (Color)ColorConverter.ConvertFromString("#D9362B"),
        (Color)ColorConverter.ConvertFromString("#16A39C"),
        (Color)ColorConverter.ConvertFromString("#E0559A"),
        (Color)ColorConverter.ConvertFromString("#5B6B80"),
    ];

    /// <summary>
    /// The colour someone gets before they pick a picture, stable for their name. Red, orange and
    /// grey are left out because next to a status they would read as busy, away or offline.
    /// </summary>
    public static Color DefaultFor(string name)
    {
        int[] calm = [0, 1, 3, 5, 6];
        var h = 0;
        foreach (var ch in name) h = h * 31 + ch;
        return Palette[calm[(int)((uint)h % calm.Length)]];
    }

    /// <summary>An aero guy on a sky gradient, as a 96 px JPEG ready to send.</summary>
    public static byte[] RenderPicture(Color c, int size = 96)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            var sky = new LinearGradientBrush(Tint(c, 0.86), Tint(c, 0.55), 90);
            dc.DrawRectangle(sky, null, new Rect(0, 0, size, size));
            var glow = new RadialGradientBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF))
            {
                Center = new Point(0.3, 0.2), GradientOrigin = new Point(0.3, 0.2), RadiusX = 0.6, RadiusY = 0.5,
            };
            dc.DrawRectangle(glow, null, new Rect(0, 0, size, size));
            var k = size / 20.0 * 0.86;
            dc.PushTransform(new TranslateTransform((size - 20 * k) / 2, size - 19.6 * k));
            dc.PushTransform(new ScaleTransform(k, k));
            Draw(dc, c);
            dc.Pop();
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        return Encode(bmp);
    }

    public static byte[] Encode(BitmapSource bmp)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = 88 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Any picture file, center-cropped to a square and scaled to 96 px.</summary>
    public static byte[] FromFile(string path, int size = 96)
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.UriSource = new Uri(path);
        src.EndInit();
        var side = Math.Min(src.PixelWidth, src.PixelHeight);
        var crop = new CroppedBitmap(src, new Int32Rect((src.PixelWidth - side) / 2, (src.PixelHeight - side) / 2, side, side));
        var scaled = new TransformedBitmap(crop, new ScaleTransform((double)size / side, (double)size / side));
        var fixedDpi = new FormatConvertedBitmap(scaled, PixelFormats.Bgr32, null, 0);
        return Encode(fixedDpi);
    }

    public static ImageSource? ToImage(byte[]? data)
    {
        if (data is not { Length: > 0 }) return null;
        try
        {
            var img = new BitmapImage();
            using var ms = new MemoryStream(data);
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }
}
