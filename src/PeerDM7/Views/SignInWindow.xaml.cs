using System.Windows;
using System.Windows.Input;
using PeerDM7.ViewModels;

namespace PeerDM7.Views;

public partial class SignInWindow : ChromeWindow
{
    public string? UserName { get; private set; }

    public SignInWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        Preview.Initials = name.Length == 0 ? "" : Images.Initials(name);
        Go.IsEnabled = name.Length > 0;
        Error.Visibility = Visibility.Collapsed;
    }

    void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Submit();
    }

    void Go_Click(object sender, RoutedEventArgs e) => Submit();

    void Submit()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            Error.Text = "Type a name first.";
            Error.Visibility = Visibility.Visible;
            return;
        }
        UserName = name;
        DialogResult = true;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void Min_Click(object sender, RoutedEventArgs e) => Minimize();
}
