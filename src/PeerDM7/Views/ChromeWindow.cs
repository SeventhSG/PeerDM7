using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;

namespace PeerDM7.Views;

/// <summary>
/// Custom title bar that still behaves like a real Windows window: snap, resize, shadow,
/// rounded corners on Windows 11, system menu on Alt+Space.
/// </summary>
public class ChromeWindow : Window
{
    public ChromeWindow()
    {
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 34,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        SetResourceReference(BackgroundProperty, "Bg.Window");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        SetResourceReference(FontFamilyProperty, "Font.UI");
        FontSize = 13;
        UseLayoutRounding = true;
        System.Windows.Media.TextOptions.SetTextFormattingMode(this, System.Windows.Media.TextFormattingMode.Display);
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/PeerDM7.ico"));
        SourceInitialized += (_, _) => RoundCorners();
        StateChanged += (_, _) => FitMaximized();
    }

    void FitMaximized()
    {
        // A chrome-less maximized window hangs over the screen edge by the resize border.
        if (Content is FrameworkElement root)
            root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    void RoundCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        }
        catch
        {
            // Windows 10 has square corners. That is fine.
        }
    }

    protected void Minimize() => SystemCommands.MinimizeWindow(this);
    protected void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    static extern bool FlashWindowEx(ref FLASHWINFO info);

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    /// <summary>Flash the taskbar button until the window is focused, like every messenger ever.</summary>
    public void FlashTaskbar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || IsActive) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = 2 | 12, // FLASHW_TRAY | FLASHW_TIMERNOFG
            uCount = 3,
        };
        FlashWindowEx(ref info);
    }
}
