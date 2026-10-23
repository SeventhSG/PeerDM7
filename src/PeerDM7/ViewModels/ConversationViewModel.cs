using System.Collections.ObjectModel;
using PeerDM7.Core;

namespace PeerDM7.ViewModels;

public sealed class MessageViewModel : ObservableObject
{
    readonly ConversationViewModel _owner;

    public MessageViewModel(ChatMessage model, ConversationViewModel owner, bool mine, string senderName)
    {
        Model = model;
        _owner = owner;
        IsMine = mine;
        SenderName = senderName;
    }

    public ChatMessage Model { get; }
    public bool IsMine { get; }
    public string SenderName { get; }
    public string Text => Model.Text;
    public bool IsNudge => Model.Kind == MessageKind.Nudge;
    public string Time => Model.Time.ToLocalTime().ToString("HH:mm");

    /// <summary>Show "Name says:" only when the speaker changes or after a pause, like the old client.</summary>
    bool _showHeader = true;
    public bool ShowHeader { get => _showHeader; set => Set(ref _showHeader, value); }

    public string Header => IsMine ? "You say:" : $"{SenderName} says:";

    public string NudgeText => IsMine
        ? (_owner.IsGroup ? "You sent a nudge." : $"You sent {_owner.Title} a nudge.")
        : $"{SenderName} sent you a nudge.";

    public string StateText
    {
        get
        {
            if (!IsMine || Model.Pending.Count == 0) return "";
            if (_owner.IsGroup)
            {
                var total = _owner.RecipientCount;
                return Model.Pending.Count == total
                    ? "Not delivered yet. Goes out when someone in the group is online."
                    : $"Delivered to {total - Model.Pending.Count} of {total}.";
            }
            return Model.Sent ? "" : $"Not delivered yet. Goes out if {_owner.Title} comes back online.";
        }
    }

    public void RefreshState() => Raise(nameof(StateText));
}

/// <summary>A one-to-one chat or a group chat. Same window, same transcript.</summary>
public sealed class ConversationViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly Dictionary<string, DateTime> _typing = new();
    DateTime _lastTypingSent;

    public ConversationViewModel(MainViewModel main, ContactViewModel? contact, GroupViewModel? group)
    {
        _main = main;
        Contact = contact;
        Group = group;
        SendCommand = new RelayCommand(Send, _ => CanSend);
        NudgeCommand = new RelayCommand(() => _main.SendNudge(this), _ => !IsGroup);
        AddPeopleCommand = new RelayCommand(() => AddPeopleRequested?.Invoke(this), _ => IsGroup);
        LeaveCommand = new RelayCommand(() => _main.LeaveGroup(this), _ => IsGroup);

        if (contact is not null) contact.PropertyChanged += (_, _) => RaiseHeader();
        if (group is not null) group.PropertyChanged += (_, _) => RaiseHeader();
    }

    public ContactViewModel? Contact { get; }
    public GroupViewModel? Group { get; }
    public bool IsGroup => Group is not null;
    public string Key => IsGroup ? Group!.Key : Contact!.Key;

    public ObservableCollection<MessageViewModel> Messages { get; } = [];
    public ObservableCollection<ContactViewModel> Members { get; } = [];

    public RelayCommand SendCommand { get; }
    public RelayCommand NudgeCommand { get; }
    public RelayCommand AddPeopleCommand { get; }
    public RelayCommand LeaveCommand { get; }

    public event Action<ConversationViewModel>? AddPeopleRequested;
    public event Action<ConversationViewModel>? NudgeArrived;
    public event Action<ConversationViewModel>? Closed;

    public string Title => IsGroup ? Group!.Title : Contact!.Title;
    public string Subtitle => IsGroup ? Group!.Subtitle : Contact!.Subtitle;
    public string StatusLabel => IsGroup ? "" : Contact!.StatusLabel;
    public string Initials => IsGroup ? "" : Contact!.Initials;
    public PeerStatus Status => IsGroup ? PeerStatus.Online : Contact!.Status;
    public bool IsReachable => IsGroup ? Group!.OtherMemberIds.Any(_main.IsConnected) : Contact!.IsOnline;
    public int RecipientCount => IsGroup ? Group!.OtherMemberIds.Count() : 1;

    public IEnumerable<string> Recipients => IsGroup ? Group!.OtherMemberIds : [Contact!.Id];

    public string OfflineNotice => IsReachable ? "" : IsGroup
        ? "Nobody else in this group is online right now. Messages wait here until someone comes back."
        : $"{Title} is not online. Messages wait here and go out if they come back before you close PeerDM7.";

    string _draft = "";
    public string Draft
    {
        get => _draft;
        set
        {
            if (!Set(ref _draft, value)) return;
            if (value.Length > 0 && DateTime.UtcNow - _lastTypingSent > TimeSpan.FromSeconds(3))
            {
                _lastTypingSent = DateTime.UtcNow;
                _main.SendTyping(this, true);
            }
        }
    }

    bool CanSend => Draft.Trim().Length > 0 && Recipients.Any();

    string _typingText = "";
    public string TypingText { get => _typingText; private set => Set(ref _typingText, value); }

    bool _isActive;
    /// <summary>Set by the window. While the window has focus, nothing counts as unread.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!Set(ref _isActive, value) || !value) return;
            if (IsGroup) Group!.Unread = 0; else Contact!.Unread = 0;
        }
    }

    public bool IsOpen { get; set; }

    void Send()
    {
        var text = Draft.Trim();
        if (text.Length == 0) return;
        if (text.Length > 4000) text = text[..4000];
        Draft = "";
        _lastTypingSent = DateTime.MinValue;
        _main.SendText(this, text);
    }

    public void Add(MessageViewModel m)
    {
        var prev = Messages.LastOrDefault();
        m.ShowHeader = m.IsNudge || prev is null || prev.IsNudge
                       || prev.Model.From != m.Model.From
                       || m.Model.Time - prev.Model.Time > TimeSpan.FromMinutes(5);
        Messages.Add(m);
        if (!m.IsMine) _typing.Remove(m.Model.From);
        UpdateTypingText();
    }

    public void SetTyping(string personId, bool on)
    {
        if (on) _typing[personId] = DateTime.UtcNow;
        else _typing.Remove(personId);
        UpdateTypingText();
    }

    /// <summary>Called once a second. Typing pings stop when someone stops, so stale ones expire.</summary>
    public void ExpireTyping()
    {
        if (_typing.Count == 0) return;
        foreach (var k in _typing.Where(kv => DateTime.UtcNow - kv.Value > TimeSpan.FromSeconds(6)).Select(kv => kv.Key).ToList())
            _typing.Remove(k);
        UpdateTypingText();
    }

    void UpdateTypingText()
    {
        var names = _typing.Keys.Select(id => _main.NameOf(id)).ToList();
        TypingText = names.Count switch
        {
            0 => "",
            1 => $"{names[0]} is typing...",
            2 => $"{names[0]} and {names[1]} are typing...",
            _ => "Several people are typing...",
        };
    }

    public void RefreshMembers()
    {
        if (!IsGroup) return;
        var ids = Group!.Record.Members.Select(m => m.Id).ToList();
        foreach (var gone in Members.Where(m => !ids.Contains(m.Id)).ToList()) Members.Remove(gone);
        foreach (var id in ids)
        {
            if (id == _main.MyId || Members.Any(m => m.Id == id)) continue;
            Members.Add(_main.ContactFor(id, Group.Record.Members.First(m => m.Id == id).Name));
        }
        RaiseHeader();
    }

    public void RaiseHeader()
    {
        Raise(nameof(Title));
        Raise(nameof(Subtitle));
        Raise(nameof(StatusLabel));
        Raise(nameof(Status));
        Raise(nameof(Initials));
        Raise(nameof(IsReachable));
        Raise(nameof(OfflineNotice));
        Raise(nameof(RecipientCount));
    }

    public void RaiseNudge() => NudgeArrived?.Invoke(this);
    public void RaiseClosed() => Closed?.Invoke(this);
}
