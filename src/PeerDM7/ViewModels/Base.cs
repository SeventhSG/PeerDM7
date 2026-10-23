using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using PeerDM7.Core;

namespace PeerDM7.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand(Action run, Func<object?, bool>? can = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? p) => can?.Invoke(p) ?? true;
    public void Execute(object? p) => run();
}

public static class Images
{
    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        var first = char.ToUpperInvariant(parts[0][0]).ToString();
        return parts.Length > 1 ? first + char.ToUpperInvariant(parts[^1][0]) : first;
    }
}

/// <summary>Anything that sits in the contact list: a person or a group chat.</summary>
public abstract class ListItem : ObservableObject
{
    public abstract string Key { get; }
    public abstract string Title { get; }
    public abstract string Subtitle { get; }
    public abstract string Section { get; }
    /// <summary>Sort order of sections: groups, online, not online.</summary>
    public abstract int SectionOrder { get; }

    int _unread;
    public int Unread { get => _unread; set => Set(ref _unread, value); }
}

public sealed class ContactViewModel : ListItem
{
    public ContactViewModel(string id) => Id = id;

    public string Id { get; }
    public override string Key => Id;

    string _name = "";
    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) { Raise(nameof(Title)); Raise(nameof(Initials)); } }
    }

    string _psm = "";
    public string PersonalMessage
    {
        get => _psm;
        set { if (Set(ref _psm, value)) Raise(nameof(Subtitle)); }
    }

    PeerStatus _status = PeerStatus.Offline;
    public PeerStatus Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            Raise(nameof(IsOnline));
            Raise(nameof(Subtitle));
            Raise(nameof(Section));
            Raise(nameof(SectionOrder));
            Raise(nameof(StatusLabel));
        }
    }

    public string? Address { get; set; }

    public bool IsOnline => Status != PeerStatus.Offline;
    public string Initials => Images.Initials(Name);
    public string StatusLabel => StatusText.For(Status);

    public override string Title => Name.Length == 0 ? "Someone new" : Name;
    public override string Subtitle =>
        Status is PeerStatus.Away or PeerStatus.Busy
            ? (PersonalMessage.Length > 0 ? $"{StatusLabel}. {PersonalMessage}" : StatusLabel)
            : PersonalMessage;
    public override string Section => IsOnline ? "Online" : "Not online";
    public override int SectionOrder => IsOnline ? 1 : 2;
}

public sealed class GroupViewModel : ListItem
{
    readonly Func<string, ContactViewModel?> _lookup;
    readonly string _myId;

    public GroupViewModel(GroupRecord record, string myId, Func<string, ContactViewModel?> lookup)
    {
        Record = record;
        _myId = myId;
        _lookup = lookup;
    }

    public GroupRecord Record { get; private set; }
    public string Id => Record.Id;
    public override string Key => "g_" + Record.Id;
    public override string Title => Record.Name;
    public override string Section => "Group chats";
    public override int SectionOrder => 0;

    public IEnumerable<string> OtherMemberIds => Record.Members.Where(m => m.Id != _myId).Select(m => m.Id);

    public override string Subtitle
    {
        get
        {
            var others = OtherMemberIds.ToList();
            var online = others.Count(id => _lookup(id)?.IsOnline == true);
            var people = Record.Members.Count == 1 ? "1 person" : $"{Record.Members.Count} people";
            return $"{people}, {online} online";
        }
    }

    public void Update(GroupRecord record)
    {
        Record = record;
        Refresh();
    }

    public void Refresh()
    {
        Raise(nameof(Title));
        Raise(nameof(Subtitle));
    }
}
