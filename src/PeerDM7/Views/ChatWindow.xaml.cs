using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using PeerDM7.Controls;
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
        foreach (var b in new UIElement[] { NudgeButton, FileButton, NudgeMini, FileMini, TheirPicture, MyPicture })
            b.Visibility = group ? Visibility.Collapsed : Visibility.Visible;
        foreach (var b in new UIElement[] { AddButton, LeaveButton, MemberPanel })
            b.Visibility = group ? Visibility.Visible : Visibility.Collapsed;
        if (group) StatusRun.Text = "";
        EmoticonGrid.ItemsSource = Emoticons.All;

        UpdateMe();
        main.PropertyChanged += Main_PropertyChanged;
        conv.Messages.CollectionChanged += Messages_Changed;
        conv.Members.CollectionChanged += (_, _) => UpdateMemberCount();
        conv.NudgeArrived += OnNudge;
        conv.Closed += OnConversationClosed;
        conv.SendFileRequested += OnSendFileRequested;
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
            conv.SendFileRequested -= OnSendFileRequested;
            main.PropertyChanged -= Main_PropertyChanged;
        };
        Loaded += (_, _) => Scroller.ScrollToEnd();

        // Drop a file anywhere on the window to send it.
        AllowDrop = !group;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var f in files.Take(5)) _main.SendFile(_conv, f);
        };
    }

    void Main_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateMe();

    void UpdateMe()
    {
        MyPicture.Source = _main.Picture;
        MyPicture.BuddyColor = _main.BuddyColor;
        MyPicture.Ring = StatusToBrush.For(_main.Status);
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

    void Emoticons_Click(object sender, RoutedEventArgs e) => EmoticonPopup.IsOpen = !EmoticonPopup.IsOpen;

    void Emoticon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Emoticons.Entry entry }) return;
        EmoticonPopup.IsOpen = false;
        var caret = Input.CaretIndex;
        var text = Input.Text;
        var needsSpace = caret > 0 && !char.IsWhiteSpace(text[caret - 1]);
        var insert = (needsSpace ? " " : "") + entry.Code + " ";
        Input.Text = text.Insert(caret, insert);
        Input.CaretIndex = caret + insert.Length;
        Input.Focus();
    }

    void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: MessageViewModel m })
            try { Clipboard.SetText(m.Text); } catch { }
    }

    void OnSendFileRequested(ConversationViewModel conv)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"Send a file to {conv.Title}", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames.Take(5)) _main.SendFile(conv, f);
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
