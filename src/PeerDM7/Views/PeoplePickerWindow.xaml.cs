using System.ComponentModel;
using System.Windows;
using PeerDM7.Core;
using PeerDM7.ViewModels;

namespace PeerDM7.Views;

public enum PickerMode
{
    Single,
    NewGroup,
    AddToGroup,
}

public partial class PeoplePickerWindow : ChromeWindow
{
    public sealed class Option(ContactViewModel contact) : INotifyPropertyChanged
    {
        public ContactViewModel Contact { get; } = contact;
        bool _checked;
        public bool IsChecked { get => _checked; set { _checked = value; Changed(nameof(IsChecked)); } }
        bool _enabled = true;
        public bool IsEnabled { get => _enabled; set { _enabled = value; Changed(nameof(IsEnabled)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    readonly PickerMode _mode;
    readonly int _capacity;
    readonly List<Option> _options;

    public IReadOnlyList<string> Chosen => _options.Where(o => o.IsChecked).Select(o => o.Contact.Id).ToList();
    public string GroupName => NameBox.Text;

    /// <param name="ids">Preselected people for a new group, or current members when adding to one.</param>
    public PeoplePickerWindow(MainViewModel vm, PickerMode mode, IReadOnlyCollection<string> ids, int currentMembers = 1)
    {
        InitializeComponent();
        _mode = mode;
        _capacity = mode == PickerMode.Single ? 1 : GroupRecord.MaxMembers - currentMembers;

        var people = vm.Contacts.Where(c => mode != PickerMode.AddToGroup || !ids.Contains(c.Id)).ToList();
        _options = people.Select(c => new Option(c) { IsChecked = mode == PickerMode.NewGroup && ids.Contains(c.Id) }).ToList();
        People.ItemsSource = _options;
        Empty.Visibility = _options.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        switch (mode)
        {
            case PickerMode.Single:
                Heading.Text = "Send a message";
                Ok.Content = "Open chat";
                NamePanel.Visibility = Visibility.Collapsed;
                Hint.Text = "Who do you want to talk to?";
                break;
            case PickerMode.NewGroup:
                Heading.Text = "Start a group chat";
                Ok.Content = "Create";
                Hint.Text = $"Pick up to {_capacity} people. Groups hold {GroupRecord.MaxMembers} people including you.";
                break;
            default:
                Heading.Text = "Add people";
                Ok.Content = "Add";
                NamePanel.Visibility = Visibility.Collapsed;
                Hint.Text = _capacity > 0 ? $"This group has room for {_capacity} more." : "This group is full.";
                break;
        }
        Title = Heading.Text;
        Toggled(null, null);
        Loaded += (_, _) => { if (mode == PickerMode.NewGroup) NameBox.Focus(); };
    }

    void Toggled(object? sender, RoutedEventArgs? e)
    {
        if (_mode == PickerMode.Single && sender is FrameworkElement { DataContext: Option picked } && picked.IsChecked)
            foreach (var o in _options.Where(o => o != picked)) o.IsChecked = false;

        var count = _options.Count(o => o.IsChecked);
        if (_mode != PickerMode.Single)
            foreach (var o in _options) o.IsEnabled = o.IsChecked || count < _capacity;
        Ok.IsEnabled = count > 0;
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
