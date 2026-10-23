using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using PeerDM7.ViewModels;

namespace PeerDM7.Views;

public partial class ChatWindow : ChromeWindow
{
    readonly ConversationViewModel _conv;
    readonly MainViewModel _main;
    bool _shaking;

    public ChatWindow(ConversationViewModel conv, MainViewModel main)
    {
        _conv = conv;
        _main = main;
        DataContext = conv;
        InitializeComponent();

        var group = conv.IsGroup;
        NudgeButton.Visibility = group ? Visibility.Collapsed : Visibility.Visible;
        AddButton.Visibility = group ? Visibility.Visible : Visibility.Collapsed;
        LeaveButton.Visibility = group ? Visibility.Visible : Visibility.Collapsed;
        StatusRun.Text = group ? "" : StatusRun.Text;
        TheirPicture.Visibility = group ? Visibility.Collapsed : Visibility.Visible;
        MemberPanel.Visibility = group ? Visibility.Visible : Visibility.Collapsed;
        MyPicture.Visibility = group ? Visibility.Collapsed : Visibility.Visible;
        MyPicture.Initials = main.Initials;
        MyPicture.Ring = Controls.StatusToBrush.For(main.Status);
        main.PropertyChanged += Main_PropertyChanged;

        conv.Messages.CollectionChanged += Messages_Changed;
        conv.Members.CollectionChanged += (_, _) => UpdateMemberCount();
        conv.NudgeArrived += OnNudge;
        conv.Closed += OnConversationClosed;
        UpdateMemberCount();
        UpdateEmpty();

        Activated += (_, _) => { conv.IsActive = true; Input.Focus(); };
        Deactivated += (_, _) => conv.IsActive = false;
        Closed += (_, _) =>
        {
            conv.IsActive = false;
            conv.IsOpen = false;
            conv.Messages.CollectionChanged -= Messages_Changed;
            conv.NudgeArrived -= OnNudge;
            conv.Closed -= OnConversationClosed;
            main.PropertyChanged -= Main_PropertyChanged;
        };
        Loaded += (_, _) => Scroller.ScrollToEnd();
    }

    void Main_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        MyPicture.Initials = _main.Initials;
        MyPicture.Ring = Controls.StatusToBrush.For(_main.Status);
    }

    void UpdateMemberCount() => MemberCount.Text = $"({_conv.Members.Count + 1})";

    void UpdateEmpty()
    {
        EmptyTranscript.Visibility = _conv.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTranscript.Text = _conv.IsGroup
            ? "This group is new. Say hello to everyone."
            : $"This is the start of your conversation with {_conv.Title}.";
    }

    void Messages_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmpty();
        // Follow new messages, unless the user scrolled up to read something.
        if (Scroller.VerticalOffset >= Scroller.ScrollableHeight - 40 || e.NewItems?.OfType<MessageViewModel>().Any(m => m.IsMine) == true)
            Dispatcher.BeginInvoke(() => Scroller.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
    }

    void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            if (_conv.SendCommand.CanExecute(null)) _conv.SendCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    void AddPeople_Click(object sender, RoutedEventArgs e)
    {
        var members = _conv.Group!.Record.Members.Select(m => m.Id).ToList();
        var picker = new PeoplePickerWindow(_main, PickerMode.AddToGroup, members, members.Count) { Owner = this };
        if (picker.ShowDialog() == true) _main.AddToGroup(_conv, picker.Chosen);
    }

    void OnConversationClosed(ConversationViewModel _) => Close();

    /// <summary>The nudge: a short, sharp shake. Skipped when Windows animations are turned off.</summary>
    void OnNudge(ConversationViewModel _)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        FlashTaskbar();
        if (_shaking || !SystemParameters.ClientAreaAnimation) return;
        _shaking = true;

        var origin = Left;
        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
        double[] offsets = [-12, 11, -10, 9, -7, 6, -4, 2, 0];
        for (var i = 0; i < offsets.Length; i++)
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(origin + offsets[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55 * (i + 1)))));
        shake.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            Left = origin;
            _shaking = false;
        };
        BeginAnimation(LeftProperty, shake);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void Min_Click(object sender, RoutedEventArgs e) => Minimize();
    void Max_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
}
