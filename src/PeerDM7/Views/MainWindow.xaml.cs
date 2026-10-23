using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PeerDM7.Core;
using PeerDM7.ViewModels;

namespace PeerDM7.Views;

public partial class MainWindow : ChromeWindow
{
    readonly MainViewModel _vm;

    /// <summary>Raised when the user closes the list. The app decides whether that hides or quits.</summary>
    public event Action? CloseRequested;

    static App TheApp => (App)Application.Current;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Top + Math.Max(24, (area.Height - Height) / 2);
    }

    ListItem? Selected => List.SelectedItem as ListItem;

    void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    void Min_Click(object sender, RoutedEventArgs e) => Minimize();

    // ---- menu bar ----

    static MenuItem Item(string header, Action run, bool? check = null)
    {
        var m = new MenuItem { Header = header };
        if (check is { } c) m.IsChecked = c;
        m.Click += (_, _) => run();
        return m;
    }

    void OpenMenu(Button anchor, params object[] items)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var i in items) menu.Items.Add(i);
        menu.IsOpen = true;
    }

    void FileMenu_Click(object sender, RoutedEventArgs e) => OpenMenu(FileMenuButton,
        Item("Change display picture...", ChangePicture),
        Item("Send a file...", PickAndSendFile),
        new Separator(),
        Item("Close to tray", () => CloseRequested?.Invoke()),
        Item("Sign out and quit", TheApp.Quit));

    void ContactsMenu_Click(object sender, RoutedEventArgs e) => OpenMenu(ContactsMenuButton,
        Item("Send an instant message...", () => SendMessage_Click(this, new RoutedEventArgs())),
        Item("Start a group chat...", () => PickGroup([])),
        Item("Add a contact by address...", ShowIpPanel));

    void ToolsMenu_Click(object sender, RoutedEventArgs e) => OpenMenu(ToolsMenuButton,
        Item("White theme", () => TheApp.SetTheme(dark: false), !TheApp.IsDark),
        Item("Dark theme", () => TheApp.SetTheme(dark: true), TheApp.IsDark),
        new Separator(),
        Item("Play sounds", () => TheApp.SoundsOn = !TheApp.SoundsOn, TheApp.SoundsOn),
        Item("Show pop-up alerts", () => TheApp.ToastsOn = !TheApp.ToastsOn, TheApp.ToastsOn));

    void HelpMenu_Click(object sender, RoutedEventArgs e) => OpenMenu(HelpMenuButton,
        Item("About PeerDM7", () => MessageBox.Show(this,
            $"PeerDM7 {typeof(App).Assembly.GetName().Version?.ToString(3)}\n\nA serverless messenger for people on the same network. Nothing is stored: close it and your chats are gone.\n\nby SeventhSG\nhttps://github.com/SeventhSG/PeerDM7",
            "About PeerDM7", MessageBoxButton.OK, MessageBoxImage.Information)),
        Item("Project page", () => OpenUrl("https://github.com/SeventhSG/PeerDM7")));

    static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ---- me ----

    void Picture_Click(object sender, RoutedEventArgs e) => ChangePicture();

    void ChangePicture()
    {
        var w = new PictureWindow(_vm) { Owner = this };
        if (w.ShowDialog() == true) _vm.SetPicture(w.Result);
    }

    void Status_Click(object sender, RoutedEventArgs e)
    {
        foreach (MenuItem item in StatusMenu.Items)
            item.IsChecked = (string)item.Tag == _vm.Status.ToString();
        StatusMenu.PlacementTarget = StatusButton;
        StatusMenu.Placement = PlacementMode.Bottom;
        StatusMenu.IsOpen = true;
    }

    void SetStatus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<PeerStatus>(tag, out var s))
            _vm.Status = s;
    }

    void Psm_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape) List.Focus();
    }

    // ---- list ----

    void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>() is not null && Selected is { } item)
            _vm.Open(item);
    }

    void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Selected is { } item) _vm.Open(item);
    }

    void ListMenu_Opened(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        MenuOpen.IsEnabled = item is not null;
        MenuGroupWith.Visibility = item is ContactViewModel ? Visibility.Visible : Visibility.Collapsed;
        MenuLeave.Visibility = item is GroupViewModel ? Visibility.Visible : Visibility.Collapsed;
    }

    void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item) _vm.Open(item);
    }

    void MenuGroupWith_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is ContactViewModel c) PickGroup([c.Id]);
    }

    void MenuLeave_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is GroupViewModel g) _vm.LeaveGroup(_vm.ConversationFor(g));
    }

    // ---- I want to... ----

    ContactViewModel? PickPerson(string heading)
    {
        if (Selected is ContactViewModel c) return c;
        var picker = new PeoplePickerWindow(_vm, PickerMode.Single, []) { Owner = this };
        picker.Heading.Text = heading;
        return picker.ShowDialog() == true && picker.Chosen.FirstOrDefault() is { } id ? _vm.ContactFor(id) : null;
    }

    void SendMessage_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is GroupViewModel g) { _vm.Open(g); return; }
        if (PickPerson("Send an instant message") is { } c) _vm.Open(c);
    }

    void SendFileAction_Click(object sender, RoutedEventArgs e) => PickAndSendFile();

    void PickAndSendFile()
    {
        if (PickPerson("Send a file") is not { } c) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"Send a file to {c.Title}" };
        if (dlg.ShowDialog(this) != true) return;
        var conv = _vm.ConversationFor(c);
        _vm.Open(c);
        _vm.SendFile(conv, dlg.FileName);
    }

    void NewGroup_Click(object sender, RoutedEventArgs e) => PickGroup([]);

    void PickGroup(IReadOnlyCollection<string> preselected)
    {
        var picker = new PeoplePickerWindow(_vm, PickerMode.NewGroup, preselected) { Owner = this };
        if (picker.ShowDialog() == true && picker.Chosen.Count > 0)
            _vm.CreateGroup(picker.GroupName, picker.Chosen);
    }

    void AddByIp_Click(object sender, RoutedEventArgs e)
    {
        if (IpPanel.Visibility == Visibility.Visible) IpPanel.Visibility = Visibility.Collapsed;
        else ShowIpPanel();
    }

    void ShowIpPanel()
    {
        IWantTo.IsExpanded = true;
        IpPanel.Visibility = Visibility.Visible;
        IpBox.Focus();
    }

    void IpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Connect();
    }

    void IpGo_Click(object sender, RoutedEventArgs e) => Connect();

    async void Connect()
    {
        IpGo.IsEnabled = false;
        IpResult.Visibility = Visibility.Visible;
        IpResult.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        IpResult.Text = "Connecting...";
        var error = await _vm.ConnectByAddress(IpBox.Text);
        IpGo.IsEnabled = true;
        if (error is null)
        {
            IpResult.Text = "Connected.";
            IpBox.Text = "";
        }
        else
        {
            IpResult.SetResourceReference(TextBlock.ForegroundProperty, "Danger.Text");
            IpResult.Text = error;
        }
    }
}

static class TreeExtensions
{
    public static T? FindAncestor<T>(this DependencyObject d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
