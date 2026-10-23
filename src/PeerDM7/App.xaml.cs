using System.IO;
using System.Windows;
using Microsoft.Win32;
using PeerDM7.Core;
using PeerDM7.ViewModels;
using PeerDM7.Views;
using WinForms = System.Windows.Forms;

namespace PeerDM7;

public partial class App : Application
{
    MainViewModel? _vm;
    MainWindow? _list;
    WinForms.NotifyIcon? _tray;
    bool _quiet;
    bool _trayHintShown;
    readonly Dictionary<string, ChatWindow> _chats = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled", ex.Exception);
            ex.Handled = true;
        };

        // --name skips the sign-in screen, --quiet mutes sounds, --log writes diagnostics to a file.
        string? presetName = null;
        for (var i = 0; i < e.Args.Length; i++)
        {
            switch (e.Args[i])
            {
                case "--name" when i + 1 < e.Args.Length: presetName = e.Args[++i]; break;
                case "--quiet": _quiet = true; break;
                case "--log" when i + 1 < e.Args.Length: Log.FilePath = e.Args[++i]; break;
            }
        }

        ApplyTheme();
        SystemEvents.UserPreferenceChanged += (_, a) =>
        {
            if (a.Category == UserPreferenceCategory.General) Dispatcher.BeginInvoke(ApplyTheme);
        };

        var name = presetName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            var signIn = new SignInWindow();
            if (signIn.ShowDialog() != true || signIn.UserName is null)
            {
                Shutdown();
                return;
            }
            name = signIn.UserName;
        }
        if (name.Length > 40) name = name[..40];

        _vm = new MainViewModel(name);
        _vm.OpenRequested += OpenChat;
        _vm.Notify += Notify;
        try
        {
            _vm.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Network start failed", ex);
            MessageBox.Show("PeerDM7 could not open a network port on this PC.\n\n" + ex.Message, "PeerDM7", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _list = new MainWindow(_vm);
        _list.CloseRequested += HideToTray;
        _list.Show();
        CreateTray();
    }

    void ApplyTheme()
    {
        var dark = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { }
        var dict = new ResourceDictionary { Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative) };
        Resources.MergedDictionaries[0] = dict;
    }

    // ---- chats ----

    void OpenChat(ConversationViewModel conv, bool activate)
    {
        if (_chats.TryGetValue(conv.Key, out var existing))
        {
            if (activate)
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
            }
            else existing.FlashTaskbar();
            return;
        }

        var w = new ChatWindow(conv, _vm!) { ShowActivated = activate };
        var offset = (_chats.Count % 6) * 26;
        var area = SystemParameters.WorkArea;
        w.Left = Math.Max(area.Left + 20, area.Left + (area.Width - w.Width) / 2 - 120 + offset);
        w.Top = Math.Max(area.Top + 20, area.Top + (area.Height - w.Height) / 2 - 40 + offset);
        if (!activate && conv.Messages.Count > 0) w.WindowState = WindowState.Normal;
        _chats[conv.Key] = w;
        conv.IsOpen = true;
        w.Closed += (_, _) => _chats.Remove(conv.Key);
        w.Show();
        if (!activate) w.FlashTaskbar();
    }

    void Notify(ConversationViewModel conv, string text)
    {
        PlaySound();
        var oneLine = text.Replace('\n', ' ').Replace('\r', ' ');
        if (oneLine.Length > 140) oneLine = oneLine[..140] + "...";
        new ToastWindow(conv, oneLine, () => OpenChat(conv, true)).ShowToast();
    }

    void PlaySound()
    {
        if (_quiet) return;
        try
        {
            var wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify Messaging.wav");
            if (File.Exists(wav)) new System.Media.SoundPlayer(wav).Play();
            else System.Media.SystemSounds.Asterisk.Play();
        }
        catch { }
    }

    // ---- tray ----

    void CreateTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open PeerDM7", null, (_, _) => ShowList());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Sign out and quit", null, (_, _) => Quit());

        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/PeerDM7.ico"))!.Stream;
        _tray = new WinForms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "PeerDM7",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, a) => { if (a.Button == WinForms.MouseButtons.Left) ShowList(); };
    }

    void ShowList()
    {
        if (_list is null) return;
        _list.Show();
        if (_list.WindowState == WindowState.Minimized) _list.WindowState = WindowState.Normal;
        _list.Activate();
    }

    void HideToTray()
    {
        _list?.Hide();
        if (_trayHintShown || _tray is null) return;
        _trayHintShown = true;
        _tray.ShowBalloonTip(4000, "PeerDM7 is still running",
            "You stay online in the tray. Right-click the icon to sign out and quit.", WinForms.ToolTipIcon.None);
    }

    void Quit()
    {
        foreach (var w in _chats.Values.ToList()) w.Close();
        if (_tray is not null) _tray.Visible = false;
        _tray?.Dispose();
        _vm?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _vm?.Dispose();
        base.OnExit(e);
    }
}
