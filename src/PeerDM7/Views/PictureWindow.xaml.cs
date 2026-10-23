using System.Windows;
using System.Windows.Media;
using PeerDM7.Controls;
using PeerDM7.ViewModels;

namespace PeerDM7.Views;

public partial class PictureWindow : ChromeWindow
{
    public sealed record Builtin(byte[] Data, ImageSource? Image);

    /// <summary>The chosen picture as JPEG, or null for the default aero guy.</summary>
    public byte[]? Result { get; private set; }

    public PictureWindow(MainViewModel vm)
    {
        InitializeComponent();
        Builtins.ItemsSource = BuddyView.Palette
            .Select(c => BuddyView.RenderPicture(c))
            .Select(b => new Builtin(b, BuddyView.ToImage(b)))
            .ToList();
        PreviewName.Text = vm.Name;
        Preview.BuddyColor = vm.BuddyColor;
        Preview.Source = vm.Picture;
        Preview.Ring = StatusToBrush.For(vm.Status);
        Result = null;
        _current = vm.Picture;
    }

    readonly ImageSource? _current;
    bool _changed;

    void Show(byte[]? data)
    {
        Result = data;
        _changed = true;
        Preview.Source = BuddyView.ToImage(data);
    }

    void Builtin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Builtin b }) Show(b.Data);
    }

    void Default_Click(object sender, RoutedEventArgs e) => Show(null);

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a display picture",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Show(BuddyView.FromFile(dlg.FileName));
        }
        catch
        {
            MessageBox.Show(this, "That file could not be opened as a picture.", "Display picture", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = _changed;
    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
