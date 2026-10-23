using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PeerDM7.Core;
using PeerDM7.ViewModels;

namespace PeerDM7.Views;

public partial class MainWindow : ChromeWindow
{
    readonly MainViewModel _vm;

    /// <summary>Raised when the user closes the list. The app decides whether that hides or quits.</summary>
    public event Action? CloseRequested;

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

    void Status_Click(object sender, RoutedEventArgs e)
    {
        foreach (MenuItem item in StatusMenu.Items)
            item.IsChecked = (string)item.Tag == _vm.Status.ToString();
        StatusMenu.PlacementTarget = StatusButton;
        StatusMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
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

    void SendMessage_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item) { _vm.Open(item); return; }
        var picker = new PeoplePickerWindow(_vm, PickerMode.Single, []) { Owner = this };
        if (picker.ShowDialog() == true && picker.Chosen.FirstOrDefault() is { } id)
            _vm.Open(_vm.ContactFor(id));
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
        IpPanel.Visibility = IpPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (IpPanel.Visibility == Visibility.Visible) IpBox.Focus();
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
