using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PeerDM7.ViewModels;

namespace PeerDM7.Views;

/// <summary>The little pop-up in the corner. Rises in once, fades out, never steals focus.</summary>
public partial class ToastWindow : Window
{
    static readonly List<ToastWindow> Open = [];
    readonly Action _onClick;
    readonly DispatcherTimer _life;

    public ToastWindow(ConversationViewModel conv, string text, Action onClick)
    {
        InitializeComponent();
        _onClick = onClick;
        Who.Text = conv.IsGroup ? conv.Title : $"{conv.Title} says:";
        What.Text = text;
        if (conv.IsGroup) Pic.Glyph = "";
        else
        {
            Pic.Initials = conv.Initials;
            Pic.Ring = Controls.StatusToBrush.For(conv.Status);
        }

        _life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _life.Tick += (_, _) => Dismiss();
        MouseEnter += (_, _) => _life.Stop();
        MouseLeave += (_, _) => _life.Start();
        Loaded += (_, _) => Place();
    }

    public void ShowToast()
    {
        // Keep it to three on screen; the oldest makes room.
        if (Open.Count >= 3) Open[0].Dismiss();
        Open.Add(this);
        Show();
        _life.Start();

        if (!SystemParameters.ClientAreaAnimation) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
    }

    void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 6;
        var below = Open.TakeWhile(t => t != this).Sum(t => t.ActualHeight);
        Top = area.Bottom - ActualHeight - 6 - below;
    }

    void Dismiss()
    {
        _life.Stop();
        if (!Open.Remove(this)) return;
        if (!SystemParameters.ClientAreaAnimation) { Close(); return; }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) => Close();
        Card.BeginAnimation(OpacityProperty, fade);
    }

    void Card_Click(object sender, MouseButtonEventArgs e)
    {
        _onClick();
        Dismiss();
    }

    void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }
}
