using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PeerDM7.Controls;

// Renders the PeerDM7 logo with the app's own aero guy drawing, so the icon and the UI match.
// Run from the repo root: dotnet run --project tools/LogoGen
//
// Writes:
//   src/PeerDM7/Assets/PeerDM7.ico       every size from 16 to 256, white glass (reads on light and dark taskbars)
//   src/PeerDM7/Assets/PeerDM7.png       256 px, white theme
//   src/PeerDM7/Assets/PeerDM7-dark.png  256 px, dark theme
//   docs/logo-light.png, docs/logo-dark.png   512 px, for the README

var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var assets = Path.Combine(root, "src", "PeerDM7", "Assets");
var docs = Path.Combine(root, "docs");
Directory.CreateDirectory(assets);
Directory.CreateDirectory(docs);

var thread = new Thread(() =>
{
    Save(Render(256, dark: false), Path.Combine(assets, "PeerDM7.png"));
    Save(Render(256, dark: true), Path.Combine(assets, "PeerDM7-dark.png"));
    Save(Render(512, dark: false), Path.Combine(docs, "logo-light.png"));
    Save(Render(512, dark: true), Path.Combine(docs, "logo-dark.png"));

    // Only the 256 px entry may be PNG; older readers (System.Drawing, the tray icon) need bitmaps below that.
    int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
    WriteIco(sizes.Select(s => s >= 256 ? Png(Render(s, dark: false)) : Dib(Render(s, dark: false))).ToList(), sizes, Path.Combine(assets, "PeerDM7.ico"));
    Console.WriteLine("Logo written.");
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

static BitmapSource Render(int size, bool dark)
{
    var v = new DrawingVisual();
    using (var dc = v.RenderOpen())
    {
        dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
        if (size <= 24) Small(dc);
        else Full(dc, dark, size);
        dc.Pop();
    }
    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(v);
    bmp.Freeze();
    return bmp;
}

// Two aero guys only: the bubble turns to mush below 32 px.
static void Small(DrawingContext dc)
{
    Buddy(dc, BuddyView.Palette[1], x: 4, y: 6, unit: 9.6);
    Buddy(dc, BuddyView.Palette[0], x: 62, y: 46, unit: 10.6);
}

static void Full(DrawingContext dc, bool dark, int size)
{
    var bubble = BubbleGeometry();

    // Soft shadow, so the white glass still has an edge on a white background.
    var shadow = bubble.Clone();
    shadow.Transform = new TranslateTransform(0, 6);
    dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(dark ? (byte)0x70 : (byte)0x38, 0x0A, 0x22, 0x4A)), null, shadow);

    var fill = dark
        ? Vertical("#33557F", "#1F3352", "#162539")
        : Vertical("#FFFFFF", "#E4EFFC", "#C2DAF5");
    var rim = new Pen(new SolidColorBrush(dark ? Rgb("#6FA6EE") : Rgb("#3A7BD8")), size >= 64 ? 6 : 9);
    dc.DrawGeometry(fill, rim, bubble);

    // Glass highlight across the top of the bubble.
    dc.PushClip(bubble);
    var gloss = new LinearGradientBrush(Color.FromArgb(dark ? (byte)0x55 : (byte)0xD0, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 90);
    dc.DrawEllipse(gloss, null, new Point(118, 30), 116, 62);
    dc.Pop();

    // Blue friend behind, green friend in front, stepping out of the bubble.
    Buddy(dc, BuddyView.Palette[1], x: 40, y: 34, unit: 6.4);
    Buddy(dc, BuddyView.Palette[0], x: 94, y: 88, unit: 7.6);
}

static Geometry BubbleGeometry()
{
    var body = new RectangleGeometry(new Rect(14, 14, 228, 166), 46, 46);
    var tail = Geometry.Parse("M 58,160 L 34,226 L 112,172 Z");
    var g = new CombinedGeometry(GeometryCombineMode.Union, body, tail);
    g.Freeze();
    return g;
}

static void Buddy(DrawingContext dc, Color c, double x, double y, double unit)
{
    dc.PushTransform(new TranslateTransform(x, y));
    dc.PushTransform(new ScaleTransform(unit, unit));
    BuddyView.Draw(dc, c);
    dc.Pop();
    dc.Pop();
}

static LinearGradientBrush Vertical(string a, string b, string c)
{
    var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
    g.GradientStops.Add(new GradientStop(Rgb(a), 0));
    g.GradientStops.Add(new GradientStop(Rgb(b), 0.55));
    g.GradientStops.Add(new GradientStop(Rgb(c), 1));
    return g;
}

static Color Rgb(string hex) => (Color)ColorConverter.ConvertFromString(hex);

static byte[] Png(BitmapSource bmp)
{
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

static void Save(BitmapSource bmp, string path) => File.WriteAllBytes(path, Png(bmp));

// Icon bitmap entry: BITMAPINFOHEADER, bottom-up straight-alpha BGRA, then an all-zero AND mask.
static byte[] Dib(BitmapSource src)
{
    var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
    int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
    var pixels = new byte[stride * h];
    bmp.CopyPixels(pixels, stride, 0);
    var maskStride = ((w + 31) / 32) * 4;

    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);
    bw.Write(40); bw.Write(w); bw.Write(h * 2);
    bw.Write((short)1); bw.Write((short)32);
    bw.Write(0); bw.Write(stride * h + maskStride * h);
    bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
    for (var y = h - 1; y >= 0; y--) bw.Write(pixels, y * stride, stride);
    bw.Write(new byte[maskStride * h]);
    return ms.ToArray();
}

static void WriteIco(List<byte[]> images, int[] sizes, string path)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((short)0);
    w.Write((short)1);
    w.Write((short)images.Count);
    var offset = 6 + 16 * images.Count;
    for (var i = 0; i < images.Count; i++)
    {
        var s = sizes[i] >= 256 ? 0 : sizes[i];
        w.Write((byte)s);
        w.Write((byte)s);
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((short)1);
        w.Write((short)32);
        w.Write(images[i].Length);
        w.Write(offset);
        offset += images[i].Length;
    }
    foreach (var img in images) w.Write(img);
}
