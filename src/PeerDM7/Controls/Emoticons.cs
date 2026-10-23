using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PeerDM7.Controls;

public enum Emo
{
    Smile, Grin, Wink, Tongue, Sad, Cry, Angry, Surprised, Confused, Blush, Cool, Nerd,
    Sleepy, Sick, Angel, Devil, Kiss, Heart, BrokenHeart, Star, ThumbsUp, ThumbsDown,
}

/// <summary>
/// The classic text emoticons. The artwork is drawn here as vectors in the glossy style of
/// the era, so nothing is copied from anyone's image set.
/// </summary>
public static class Emoticons
{
    public sealed record Entry(Emo Kind, string Name, string[] Codes)
    {
        public string Code => Codes[0];
        public string Tip => $"{Name}  {Code}";
    }

    public static readonly Entry[] All =
    [
        new(Emo.Smile, "Smile", [":)", ":-)"]),
        new(Emo.Grin, "Big smile", [":D", ":-D", ":d"]),
        new(Emo.Wink, "Wink", [";)", ";-)"]),
        new(Emo.Tongue, "Tongue out", [":P", ":-P", ":p"]),
        new(Emo.Surprised, "Surprised", [":O", ":-O", ":o"]),
        new(Emo.Sad, "Sad", [":(", ":-("]),
        new(Emo.Cry, "Crying", [":'("]),
        new(Emo.Angry, "Angry", [":@", ":-@"]),
        new(Emo.Confused, "Confused", [":S", ":-S", ":s"]),
        new(Emo.Blush, "Embarrassed", [":$", ":-$"]),
        new(Emo.Cool, "Hot", ["(H)"]),
        new(Emo.Nerd, "Nerd", ["8-|"]),
        new(Emo.Sleepy, "Sleepy", ["|-)"]),
        new(Emo.Sick, "Sick", ["+o("]),
        new(Emo.Angel, "Angel", ["(A)"]),
        new(Emo.Devil, "Devil", ["(6)"]),
        new(Emo.Kiss, "Kiss", [":-*", ":*"]),
        new(Emo.Heart, "Heart", ["(L)", "<3"]),
        new(Emo.BrokenHeart, "Broken heart", ["(U)", "</3"]),
        new(Emo.Star, "Star", ["(*)"]),
        new(Emo.ThumbsUp, "Thumbs up", ["(Y)"]),
        new(Emo.ThumbsDown, "Thumbs down", ["(N)"]),
    ];

    static readonly (string Code, Emo Kind)[] ByLength =
        All.SelectMany(e => e.Codes.Select(c => (c, e.Kind))).OrderByDescending(x => x.c.Length).ToArray();

    /// <summary>Splits text into plain runs and emoticons, longest code first.</summary>
    public static IEnumerable<(string? Text, Emo? Emo)> Parse(string text)
    {
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var hit = false;
            foreach (var (code, kind) in ByLength)
            {
                if (string.CompareOrdinal(text, i, code, 0, code.Length) != 0) continue;
                if (i > start) yield return (text[start..i], null);
                yield return (null, kind);
                i += code.Length;
                start = i;
                hit = true;
                break;
            }
            if (!hit) i++;
        }
        if (start < text.Length) yield return (text[start..], null);
    }
}

/// <summary>One emoticon, drawn on a 20 by 20 grid and scaled to its size.</summary>
public sealed class EmoticonView : FrameworkElement
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(Emo), typeof(EmoticonView),
            new FrameworkPropertyMetadata(Emo.Smile, FrameworkPropertyMetadataOptions.AffectsRender));

    public Emo Kind { get => (Emo)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public EmoticonView()
    {
        Width = 19;
        Height = 19;
        SnapsToDevicePixels = true;
    }

    static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0x3B, 0x24, 0x05)));
    static readonly Pen InkPen = FrozenPen(Ink, 1.25);
    static readonly Pen ThinInk = FrozenPen(Ink, 0.9);
    static readonly Brush Yellow = Face("#FFF6B8", "#FFD83A", "#F0A500");
    static readonly Brush Red = Face("#FFC2A8", "#F2603F", "#C4321A");
    static readonly Brush Green = Face("#E9F7B5", "#A9D44B", "#6E9E1E");
    static readonly Pen YellowEdge = FrozenPen(new SolidColorBrush(Color.FromRgb(0xC2, 0x84, 0x00)), 0.7);
    static readonly Pen RedEdge = FrozenPen(new SolidColorBrush(Color.FromRgb(0x9A, 0x22, 0x10)), 0.7);
    static readonly Pen GreenEdge = FrozenPen(new SolidColorBrush(Color.FromRgb(0x56, 0x7D, 0x15)), 0.7);
    static readonly Brush Gloss = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));
    static readonly Brush Mouth = Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0x22, 0x0A)));
    static readonly Brush White = Frozen(new SolidColorBrush(Color.FromRgb(0xFD, 0xFE, 0xFF)));
    static readonly Brush Tear = Frozen(new SolidColorBrush(Color.FromRgb(0x4F, 0xA3, 0xF7)));
    static readonly Brush Pink = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0x7F, 0x96)));
    static readonly Brush Lips = Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0x34, 0x4F)));
    static readonly Brush Shades = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x24)));
    static readonly Pen Halo = FrozenPen(new SolidColorBrush(Color.FromRgb(0xF5, 0xC4, 0x00)), 1.2);
    static readonly Pen Zzz = FrozenPen(new SolidColorBrush(Color.FromRgb(0x3B, 0x6F, 0xD8)), 0.9);
    static readonly Brush HeartFill = Face("#FFB3BF", "#F0304F", "#B0122E");
    static readonly Pen HeartEdge = FrozenPen(new SolidColorBrush(Color.FromRgb(0x8E, 0x0E, 0x24)), 0.7);
    static readonly Pen Crack = FrozenPen(new SolidColorBrush(Color.FromRgb(0x5A, 0x0A, 0x16)), 1.1);

    protected override void OnRender(DrawingContext dc)
    {
        var s = Math.Min(ActualWidth, ActualHeight) / 20.0;
        if (s <= 0) return;
        dc.PushTransform(new ScaleTransform(s, s));
        switch (Kind)
        {
            case Emo.Heart: DrawHeart(dc, false); break;
            case Emo.BrokenHeart: DrawHeart(dc, true); break;
            case Emo.Star: DrawStar(dc); break;
            case Emo.ThumbsUp: DrawThumb(dc, ""); break;
            case Emo.ThumbsDown: DrawThumb(dc, ""); break;
            default: DrawFace(dc); break;
        }
        dc.Pop();
    }

    void DrawFace(DrawingContext dc)
    {
        var k = Kind;
        var (fill, edge) = k switch
        {
            Emo.Angry or Emo.Devil => (Red, RedEdge),
            Emo.Sick => (Green, GreenEdge),
            _ => (Yellow, YellowEdge),
        };

        if (k == Emo.Devil)
        {
            dc.DrawGeometry(fill, edge, G("M4.6,5.2 L3.4,0.9 L7.6,3.4 Z"));
            dc.DrawGeometry(fill, edge, G("M15.4,5.2 L16.6,0.9 L12.4,3.4 Z"));
        }

        dc.DrawEllipse(fill, edge, new Point(10, 10.4), 9, 9);
        dc.DrawEllipse(Gloss, null, new Point(10, 5.3), 5.6, 2.9);

        // Eyes
        switch (k)
        {
            case Emo.Wink:
                dc.DrawGeometry(null, InkPen, G("M5.7,8.6 Q7,7.1 8.3,8.6"));
                Eye(dc, 13, 8.4);
                break;
            case Emo.Blush:
            case Emo.Kiss:
                dc.DrawGeometry(null, InkPen, G("M5.7,8.8 Q7,7.3 8.3,8.8"));
                dc.DrawGeometry(null, InkPen, G("M11.7,8.8 Q13,7.3 14.3,8.8"));
                break;
            case Emo.Sleepy:
                dc.DrawGeometry(null, InkPen, G("M5.7,8.4 Q7,9.6 8.3,8.4"));
                dc.DrawGeometry(null, InkPen, G("M11.7,8.4 Q13,9.6 14.3,8.4"));
                break;
            case Emo.Cool:
                dc.DrawGeometry(Shades, null, G("M3.4,6.9 L16.6,6.9 L16.2,8.4 Q15.8,11 13,10.8 Q11,10.6 10.6,8.6 L9.4,8.6 Q9,10.6 7,10.8 Q4.2,11 3.8,8.4 Z"));
                dc.DrawGeometry(null, FrozenPen(Gloss, 0.7), G("M5,7.8 L6.6,7.8"));
                break;
            case Emo.Nerd:
                dc.DrawEllipse(White, ThinInk, new Point(7, 8.5), 2.4, 2.4);
                dc.DrawEllipse(White, ThinInk, new Point(13, 8.5), 2.4, 2.4);
                dc.DrawLine(ThinInk, new Point(9.4, 8.3), new Point(10.6, 8.3));
                dc.DrawEllipse(Ink, null, new Point(7.3, 8.7), 0.9, 0.9);
                dc.DrawEllipse(Ink, null, new Point(13.3, 8.7), 0.9, 0.9);
                break;
            case Emo.Surprised:
                dc.DrawEllipse(Ink, null, new Point(7, 8.1), 1.2, 1.9);
                dc.DrawEllipse(Ink, null, new Point(13, 8.1), 1.2, 1.9);
                break;
            default:
                Eye(dc, 7, 8.4);
                Eye(dc, 13, 8.4);
                break;
        }

        // Brows
        if (k is Emo.Angry or Emo.Devil)
        {
            dc.DrawGeometry(null, InkPen, G("M4.9,5.9 L8.5,7.3"));
            dc.DrawGeometry(null, InkPen, G("M15.1,5.9 L11.5,7.3"));
        }

        // Mouth and extras
        switch (k)
        {
            case Emo.Smile:
            case Emo.Wink:
            case Emo.Cool:
            case Emo.Angel:
                dc.DrawGeometry(null, InkPen, G("M5.8,12 Q10,16.2 14.2,12"));
                break;
            case Emo.Grin:
            case Emo.Devil:
                dc.DrawGeometry(Mouth, null, G("M5.4,11.4 L14.6,11.4 Q14.1,17 10,17 Q5.9,17 5.4,11.4 Z"));
                dc.DrawGeometry(White, null, G("M5.9,11.4 L14.1,11.4 L13.9,12.8 L6.1,12.8 Z"));
                break;
            case Emo.Tongue:
                dc.DrawGeometry(null, InkPen, G("M6,12.2 Q10,13.9 14,12.2"));
                dc.DrawGeometry(Lips, ThinInk, G("M8.3,12.9 Q8.3,16.4 10,16.4 Q11.7,16.4 11.7,12.9 Z"));
                break;
            case Emo.Sad:
            case Emo.Cry:
                dc.DrawGeometry(null, InkPen, G("M6,15 Q10,11.2 14,15"));
                if (k == Emo.Cry)
                {
                    dc.DrawGeometry(Tear, null, G("M6.2,10.2 Q4.9,12.4 6.2,13.3 Q7.5,12.4 6.2,10.2 Z"));
                    dc.DrawGeometry(Tear, null, G("M13.8,10.2 Q12.5,12.4 13.8,13.3 Q15.1,12.4 13.8,10.2 Z"));
                }
                break;
            case Emo.Angry:
                dc.DrawGeometry(null, InkPen, G("M6.6,14.4 Q10,12.2 13.4,14.4"));
                break;
            case Emo.Surprised:
                dc.DrawEllipse(Mouth, null, new Point(10, 13.9), 1.9, 2.4);
                break;
            case Emo.Confused:
            case Emo.Sick:
                dc.DrawGeometry(null, InkPen, G("M5.8,13.4 Q7.9,11.8 10,13.4 Q12.1,15 14.2,13.4"));
                break;
            case Emo.Blush:
                dc.DrawEllipse(Pink, null, new Point(5.4, 11.6), 1.9, 1.15);
                dc.DrawEllipse(Pink, null, new Point(14.6, 11.6), 1.9, 1.15);
                dc.DrawGeometry(null, InkPen, G("M7.7,12.8 Q10,14.6 12.3,12.8"));
                break;
            case Emo.Nerd:
                dc.DrawGeometry(null, InkPen, G("M6.3,12.4 Q10,15.6 13.7,12.4"));
                dc.DrawRectangle(White, ThinInk, new Rect(9, 13.2, 2, 1.7));
                break;
            case Emo.Sleepy:
                dc.DrawEllipse(Mouth, null, new Point(10, 14), 1.1, 0.9);
                dc.DrawGeometry(null, Zzz, G("M14.6,1.4 L17.4,1.4 L14.6,4.2 L17.4,4.2"));
                break;
            case Emo.Kiss:
                dc.DrawEllipse(Lips, null, new Point(11, 13.4), 1.7, 1.3);
                break;
        }

        if (k == Emo.Angel)
            dc.DrawEllipse(null, Halo, new Point(10, 1.6), 5, 1.3);
    }

    static void Eye(DrawingContext dc, double x, double y) => dc.DrawEllipse(Ink, null, new Point(x, y), 1.15, 1.65);

    static void DrawHeart(DrawingContext dc, bool broken)
    {
        var heart = G("M10,18 C2,12.4 1.6,7 5.2,4.7 C7.5,3.3 9.4,4.5 10,6.2 C10.6,4.5 12.5,3.3 14.8,4.7 C18.4,7 18,12.4 10,18 Z");
        dc.DrawGeometry(HeartFill, HeartEdge, heart);
        dc.DrawEllipse(Gloss, null, new Point(6.6, 7), 1.9, 1.2);
        if (broken) dc.DrawGeometry(null, Crack, G("M10,6.4 L8.7,9.6 L11.2,11.6 L9.3,14.6 L10,17.4"));
    }

    static void DrawStar(DrawingContext dc)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            for (var i = 0; i < 10; i++)
            {
                var r = i % 2 == 0 ? 9 : 3.9;
                var a = Math.PI / 5 * i - Math.PI / 2;
                var p = new Point(10 + r * Math.Cos(a), 10.6 + r * Math.Sin(a));
                if (i == 0) c.BeginFigure(p, true, true); else c.LineTo(p, true, true);
            }
        }
        g.Freeze();
        dc.DrawGeometry(Yellow, YellowEdge, g);
        dc.DrawEllipse(Gloss, null, new Point(9, 7.6), 2.2, 1.3);
    }

    void DrawThumb(DrawingContext dc, string glyph)
    {
        var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            17, Brushes.Black, 1.0);
        var geo = ft.BuildGeometry(new Point(1.5, 1.6));
        var face = new CombinedGeometry(GeometryCombineMode.Union, geo, Geometry.Empty);
        dc.DrawGeometry(Yellow, YellowEdge, face);
    }

    static Geometry G(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    static Brush Face(string light, string mid, string dark)
    {
        var b = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.35, 0.28),
            Center = new Point(0.45, 0.4),
            RadiusX = 0.65,
            RadiusY = 0.65,
        };
        b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(light), 0));
        b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(mid), 0.55));
        b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(dark), 1));
        b.Freeze();
        return b;
    }

    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    static Pen FrozenPen(Brush b, double w)
    {
        var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }
}

/// <summary>Attach to a TextBlock: renders its text with emoticon codes replaced by pictures.</summary>
public static class EmoticonText
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(EmoticonText), new PropertyMetadata(null, Rebuild));
    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.RegisterAttached("Size", typeof(double), typeof(EmoticonText), new PropertyMetadata(19.0, Rebuild));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? v) => d.SetValue(TextProperty, v);
    public static double GetSize(DependencyObject d) => (double)d.GetValue(SizeProperty);
    public static void SetSize(DependencyObject d, double v) => d.SetValue(SizeProperty, v);

    static void Rebuild(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var text = GetText(tb);
        if (string.IsNullOrEmpty(text)) return;
        var size = GetSize(tb);
        foreach (var (plain, emo) in Emoticons.Parse(text))
        {
            if (plain is not null) tb.Inlines.Add(new Run(plain));
            else if (emo is { } kind)
                tb.Inlines.Add(new InlineUIContainer(new EmoticonView { Kind = kind, Width = size, Height = size, Margin = new Thickness(1, 0, 1, 0) })
                {
                    BaselineAlignment = BaselineAlignment.Center,
                });
        }
    }
}
