using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.ComponentModel;
using System.Net;
using System.Text.Json.Nodes;
using System.Windows.Data;
using System.Windows.Threading;
using PeerDM7.Core;
using PeerDM7.Net;

namespace PeerDM7.ViewModels;

/// <summary>
/// The whole session: who is around, every conversation, group membership. Network events are
/// marshalled onto the UI thread, so everything in here runs single-threaded.
/// Nothing is persisted. Close the app and it is all gone.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    readonly Dispatcher _ui;
    readonly Identity _me = new();
    readonly PeerNetwork _net;
    readonly Dictionary<string, ContactViewModel> _contacts = new();
    readonly Dictionary<string, GroupViewModel> _groups = new();
    readonly Dictionary<string, ConversationViewModel> _conversations = new();
    readonly HashSet<string> _seenMessageIds = new();
    readonly DispatcherTimer _tick;

    public MainViewModel(string name)
    {
        _ui = Dispatcher.CurrentDispatcher;
        _name = name;
        _net = new PeerNetwork(_me, () => new PeerProfile { Name = Name, PersonalMessage = PersonalMessage, Status = Status, Picture = _myPicture });

        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ListItem.Section)));
        View.SortDescriptions.Add(new SortDescription(nameof(ListItem.SectionOrder), ListSortDirection.Ascending));
        View.SortDescriptions.Add(new SortDescription(nameof(ListItem.Title), ListSortDirection.Ascending));
        View.IsLiveGrouping = true;
        View.IsLiveSorting = true;
        View.IsLiveFiltering = true;
        View.LiveGroupingProperties.Add(nameof(ListItem.Section));
        View.LiveSortingProperties.Add(nameof(ListItem.SectionOrder));
        View.LiveSortingProperties.Add(nameof(ListItem.Title));
        View.LiveFilteringProperties.Add(nameof(ListItem.Title));
        View.Filter = o => Search.Length == 0 || o is ListItem i && i.Title.Contains(Search, StringComparison.CurrentCultureIgnoreCase);

        _net.PeerConnected += (id, ep) => Ui(() => OnConnected(id, ep));
        _net.PeerDisconnected += id => Ui(() => OnDisconnected(id));
        _net.ProfileReceived += (id, p) => Ui(() => OnProfile(id, p));
        _net.MessageReceived += (id, m) => Ui(() => OnDirectMessage(id, m));
        _net.Delivered += (id, mid) => Ui(() => OnDelivered(id, null, mid));
        _net.TypingChanged += (id, gid, on) => Ui(() => OnTyping(id, gid, on));
        _net.OtherFrame += (id, f) =>
        {
            // File chunks are written straight from the network thread; everything else goes to the UI.
            if ((string?)f["t"] == "filechunk") OnChunk(id, f);
            else Ui(() => OnOtherFrame(id, f));
        };

        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var c in _conversations.Values) c.ExpireTyping();
        }, _ui);
    }

    public void Start()
    {
        _net.Start();
        _tick.Start();
        Raise(nameof(NetworkLine));
    }

    void Ui(Action a) => _ui.BeginInvoke(a);
    public void OnUi(Action a) => _ui.BeginInvoke(a);

    // ---- me ----

    public string MyId => _me.Id;

    string _name;
    public string Name
    {
        get => _name;
        set
        {
            value = value.Trim();
            if (value.Length == 0 || value.Length > 40) return;
            if (Set(ref _name, value)) { Raise(nameof(Initials)); _net.BroadcastProfile(); }
        }
    }

    public string Initials => Images.Initials(Name);
    public System.Windows.Media.Color BuddyColor => Controls.BuddyView.DefaultFor(Name);

    byte[]? _myPicture;
    System.Windows.Media.ImageSource? _myPictureImage;
    public System.Windows.Media.ImageSource? Picture { get => _myPictureImage; private set => Set(ref _myPictureImage, value); }

    public void SetPicture(byte[]? jpeg)
    {
        _myPicture = jpeg;
        Picture = Controls.BuddyView.ToImage(jpeg);
        _net.BroadcastProfile();
    }

    string _psm = "";
    public string PersonalMessage
    {
        get => _psm;
        set
        {
            value = value.Length > 120 ? value[..120] : value;
            if (Set(ref _psm, value)) _net.BroadcastProfile();
        }
    }

    PeerStatus _status = PeerStatus.Online;
    public PeerStatus Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            Raise(nameof(StatusLabel));
            _net.BroadcastProfile();
        }
    }

    public string StatusLabel => StatusText.For(Status);

    public string NetworkLine
    {
        get
        {
            var ip = NetInfo.PrimaryAddress();
            if (ip is null) return "No network connection";
            return _net.ListeningOnDefaultPort ? $"Your address: {ip}" : $"Your address: {ip}:{_net.Port}";
        }
    }

    // ---- list ----

    public ObservableCollection<ListItem> Items { get; } = [];
    public ListCollectionView View { get; }

    string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) View.Refresh(); }
    }

    public int OnlineCount => _contacts.Values.Count(c => c.IsOnline);
    public bool NobodyOnline => OnlineCount == 0;
    public IEnumerable<ContactViewModel> Contacts => _contacts.Values.OrderByDescending(c => c.IsOnline).ThenBy(c => c.Title);

    public bool IsConnected(string id) => _net.IsConnected(id);

    public string NameOf(string id) =>
        _contacts.TryGetValue(id, out var c) ? c.Title
        : _groups.Values.SelectMany(g => g.Record.Members).FirstOrDefault(m => m.Id == id)?.Name ?? "Someone";

    public ContactViewModel ContactFor(string id, string? fallbackName = null)
    {
        if (_contacts.TryGetValue(id, out var c)) return c;
        c = new ContactViewModel(id) { Name = fallbackName ?? "" };
        _contacts[id] = c;
        Items.Add(c);
        return c;
    }

    void RaiseCounts()
    {
        Raise(nameof(OnlineCount));
        Raise(nameof(NobodyOnline));
        foreach (var g in _groups.Values) g.Refresh();
        foreach (var c in _conversations.Values) c.RaiseHeader();
    }

    // ---- conversations ----

    public event Action<ConversationViewModel, bool>? OpenRequested;
    public event Action<ConversationViewModel, string>? Notify;

    public ConversationViewModel ConversationFor(ListItem item)
    {
        if (_conversations.TryGetValue(item.Key, out var conv)) return conv;
        conv = item is GroupViewModel g
            ? new ConversationViewModel(this, null, g)
            : new ConversationViewModel(this, (ContactViewModel)item, null);
        conv.RefreshMembers();
        _conversations[item.Key] = conv;
        return conv;
    }

    public void Open(ListItem item) => OpenRequested?.Invoke(ConversationFor(item), true);

    public void SendText(ConversationViewModel conv, string text) => Post(conv, text, MessageKind.Text);

    // ---- spam limits ----
    // Sending: a burst of 5 messages, then one every 1.2 seconds. Nudges: one per 15 seconds.
    // Receiving: the same idea per person, so a modified client cannot flood anyone either.

    static readonly TimeSpan NudgeGap = TimeSpan.FromSeconds(15);
    readonly TokenBucket _sendBucket = new(5, TimeSpan.FromMilliseconds(1200));
    readonly Dictionary<string, DateTime> _lastNudgeSent = new();
    readonly Dictionary<string, TokenBucket> _incoming = new();
    readonly Dictionary<string, DateTime> _lastNudgeIn = new();
    readonly Dictionary<string, DateTime> _floodNoticeShown = new();

    public bool TryTakeSendToken() => _sendBucket.TryTake();

    public void SendNudge(ConversationViewModel conv)
    {
        if (_lastNudgeSent.TryGetValue(conv.Key, out var last) && DateTime.UtcNow - last < NudgeGap)
        {
            var wait = (int)Math.Ceiling((NudgeGap - (DateTime.UtcNow - last)).TotalSeconds);
            conv.ShowNotice($"You can send another nudge in {wait} seconds.");
            return;
        }
        if (!_sendBucket.TryTake())
        {
            conv.ShowNotice("Slow down a little. You can send again in a moment.");
            return;
        }
        _lastNudgeSent[conv.Key] = DateTime.UtcNow;
        Post(conv, "", MessageKind.Nudge);
    }

    /// <summary>False when this person is sending faster than any human types.</summary>
    bool AllowIncoming(string senderId, ConversationViewModel conv, MessageKind kind)
    {
        if (kind == MessageKind.Nudge)
        {
            if (_lastNudgeIn.TryGetValue(senderId, out var last) && DateTime.UtcNow - last < NudgeGap) return false;
            _lastNudgeIn[senderId] = DateTime.UtcNow;
        }
        if (!_incoming.TryGetValue(senderId, out var bucket))
            _incoming[senderId] = bucket = new TokenBucket(8, TimeSpan.FromMilliseconds(700));
        if (bucket.TryTake()) return true;

        if (!_floodNoticeShown.TryGetValue(senderId, out var shown) || DateTime.UtcNow - shown > TimeSpan.FromSeconds(10))
        {
            _floodNoticeShown[senderId] = DateTime.UtcNow;
            conv.ShowNotice($"{NameOf(senderId)} is sending messages too fast. Some were hidden.");
        }
        return false;
    }

    void Post(ConversationViewModel conv, string text, MessageKind kind)
    {
        var msg = new ChatMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            From = MyId,
            Text = text,
            Time = DateTimeOffset.Now,
            Kind = kind,
            Pending = [.. conv.Recipients],
        };
        var vm = new MessageViewModel(msg, conv, mine: true, Name);
        conv.Add(vm);
        _ = Deliver(conv, vm);
    }

    async Task Deliver(ConversationViewModel conv, MessageViewModel vm, string? onlyTo = null)
    {
        var msg = vm.Model;
        foreach (var to in msg.Pending.ToList())
        {
            if (onlyTo is not null && to != onlyTo) continue;
            var frame = conv.IsGroup
                ? new JsonObject { ["t"] = "gmsg", ["gid"] = conv.Group!.Id, ["id"] = msg.Id, ["text"] = msg.Text, ["ts"] = msg.Time.ToUnixTimeMilliseconds() }
                : new JsonObject { ["t"] = "msg", ["id"] = msg.Id, ["text"] = msg.Text, ["ts"] = msg.Time.ToUnixTimeMilliseconds(), ["nudge"] = msg.Kind == MessageKind.Nudge };
            if (await _net.SendAsync(to, frame)) msg.Sent = true;
        }
        vm.RefreshState();
    }

    public void SendTyping(ConversationViewModel conv, bool on)
    {
        foreach (var to in conv.Recipients)
        {
            var f = new JsonObject { ["t"] = "typing", ["on"] = on };
            if (conv.IsGroup) f["gid"] = conv.Group!.Id;
            _ = _net.SendAsync(to, f);
        }
    }

    void Receive(ConversationViewModel conv, ChatMessage m, string senderId, FileTransfer? transfer = null)
    {
        if (!_seenMessageIds.Add(m.Id)) return;
        if (!AllowIncoming(senderId, conv, m.Kind)) return;
        var vm = new MessageViewModel(m, conv, mine: false, NameOf(senderId)) { Transfer = transfer };
        conv.Add(vm);

        var item = (ListItem?)conv.Group ?? conv.Contact!;
        if (!conv.IsActive) item.Unread++;
        OpenRequested?.Invoke(conv, false);

        if (m.Kind == MessageKind.Nudge)
        {
            if (DateTimeOffset.Now - m.Time < TimeSpan.FromMinutes(1)) conv.RaiseNudge();
            if (!conv.IsActive) Notify?.Invoke(conv, $"{vm.SenderName} sent you a nudge.");
        }
        else if (m.Kind == MessageKind.File)
        {
            if (!conv.IsActive) Notify?.Invoke(conv, $"Wants to send you {m.Text}");
        }
        else if (!conv.IsActive)
        {
            Notify?.Invoke(conv, conv.IsGroup ? $"{vm.SenderName}: {m.Text}" : m.Text);
        }
    }

    // ---- network events ----

    void OnConnected(string id, IPEndPoint ep)
    {
        var c = ContactFor(id);
        c.Address = ep.Address.ToString();
        if (c.Status == PeerStatus.Offline) c.Status = PeerStatus.Online;
        RaiseCounts();

        // Bring them up to date on groups they belong to, then retry anything they missed.
        foreach (var g in _groups.Values.Where(g => g.Record.Members.Any(m => m.Id == id)))
            _ = _net.SendAsync(id, GroupFrame(g.Record));

        foreach (var conv in _conversations.Values.Where(cv => cv.Recipients.Contains(id)))
            foreach (var m in conv.Messages.Where(m => m.IsMine && m.Model.Pending.Contains(id)))
                _ = Deliver(conv, m, onlyTo: id);
    }

    void OnDisconnected(string id)
    {
        if (!_contacts.TryGetValue(id, out var c)) return;
        c.Status = PeerStatus.Offline;
        foreach (var conv in _conversations.Values) conv.SetTyping(id, false);
        foreach (var t in _transfers.Values.Where(t => t.PeerId == id && (t.IsActive || t.State == TransferState.Offered)))
            t.Fail($"{c.Title} went offline.");
        RaiseCounts();
    }

    void OnProfile(string id, PeerProfile p)
    {
        var c = ContactFor(id);
        c.Name = p.Name.Length > 0 ? p.Name : c.Name;
        c.PersonalMessage = p.PersonalMessage;
        c.SetPicture(p.Picture);
        c.Status = _net.IsConnected(id) ? p.Status : PeerStatus.Offline;
        foreach (var g in _groups.Values)
            foreach (var m in g.Record.Members.Where(m => m.Id == id)) m.Name = c.Name;
        RaiseCounts();
    }

    void OnDirectMessage(string id, ChatMessage m) => Receive(ConversationFor(ContactFor(id)), m, id);

    void OnDelivered(string id, string? gid, string messageId)
    {
        var key = gid is null ? id : "g_" + gid;
        if (!_conversations.TryGetValue(key, out var conv)) return;
        var vm = conv.Messages.LastOrDefault(x => x.Model.Id == messageId);
        if (vm is null) return;
        vm.Model.Pending.Remove(id);
        vm.Model.Sent = true;
        vm.RefreshState();
    }

    void OnTyping(string id, string? gid, bool on)
    {
        var key = gid is null ? id : "g_" + gid;
        if (_conversations.TryGetValue(key, out var conv)) conv.SetTyping(id, on);
    }

    void OnOtherFrame(string id, JsonObject f)
    {
        switch ((string?)f["t"])
        {
            case "group": OnGroupFrame(id, f); break;
            case "gmsg": OnGroupMessage(id, f); break;
            case "fileoffer": OnFileOffer(id, f); break;
            case "fileaccept":
            case "filedecline":
            case "filecancel":
            case "filegot":
                OnFileControl(id, f);
                break;
            case "gack":
                if ((string?)f["gid"] is { } gid && (string?)f["id"] is { } mid) OnDelivered(id, gid, mid);
                break;
        }
    }

    // ---- groups ----

    public bool CanCreateGroup => _contacts.Values.Any(c => c.IsOnline);

    public void CreateGroup(string name, IEnumerable<string> memberIds)
    {
        var record = new GroupRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim().Length > 0 ? name.Trim() : "Group chat",
            CreatedBy = MyId,
            Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Members = [new GroupMember { Id = MyId, Name = Name }],
        };
        foreach (var id in memberIds.Distinct().Take(GroupRecord.MaxMembers - 1))
            record.Members.Add(new GroupMember { Id = id, Name = NameOf(id) });

        var g = AdoptGroup(record);
        PushGroup(record);
        Open(g);
    }

    public void AddToGroup(ConversationViewModel conv, IEnumerable<string> ids)
    {
        if (!conv.IsGroup) return;
        var r = Clone(conv.Group!.Record);
        foreach (var id in ids)
        {
            if (r.Members.Count >= GroupRecord.MaxMembers) break;
            if (r.Members.All(m => m.Id != id)) r.Members.Add(new GroupMember { Id = id, Name = NameOf(id) });
        }
        r.Version = Math.Max(r.Version + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var previous = conv.Group.Record.Members.Select(m => m.Id).ToList();
        conv.Group.Update(r);
        conv.RefreshMembers();
        PushGroup(r, previous);
    }

    public void LeaveGroup(ConversationViewModel conv)
    {
        if (!conv.IsGroup) return;
        var g = conv.Group!;
        var r = Clone(g.Record);
        r.Members.RemoveAll(m => m.Id == MyId);
        r.Version = Math.Max(r.Version + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        PushGroup(r);
        DropGroup(g);
    }

    GroupViewModel AdoptGroup(GroupRecord record)
    {
        var g = new GroupViewModel(record, MyId, id => _contacts.GetValueOrDefault(id));
        _groups[record.Id] = g;
        Items.Add(g);
        foreach (var m in record.Members.Where(m => m.Id != MyId)) ContactFor(m.Id, m.Name);
        return g;
    }

    void DropGroup(GroupViewModel g)
    {
        _groups.Remove(g.Id);
        Items.Remove(g);
        if (_conversations.Remove(g.Key, out var conv)) conv.RaiseClosed();
    }

    void PushGroup(GroupRecord r, IEnumerable<string>? alsoTo = null)
    {
        var targets = r.Members.Select(m => m.Id).Concat(alsoTo ?? []).Where(id => id != MyId).Distinct();
        foreach (var id in targets) _ = _net.SendAsync(id, GroupFrame(r));
    }

    static JsonObject GroupFrame(GroupRecord r) => new()
    {
        ["t"] = "group",
        ["gid"] = r.Id,
        ["name"] = r.Name,
        ["ver"] = r.Version,
        ["by"] = r.CreatedBy,
        ["members"] = new JsonArray(r.Members.Select(m => (JsonNode)new JsonObject { ["id"] = m.Id, ["name"] = m.Name }).ToArray()),
    };

    void OnGroupFrame(string from, JsonObject f)
    {
        var gid = (string?)f["gid"];
        if (string.IsNullOrEmpty(gid) || gid.Length > 64) return;
        var members = (f["members"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(o => new GroupMember { Id = (string?)o["id"] ?? "", Name = (string?)o["name"] ?? "" })
            .Where(m => m.Id.Length > 0)
            .GroupBy(m => m.Id).Select(x => x.First())
            .Take(GroupRecord.MaxMembers)
            .ToList() ?? [];
        var incoming = new GroupRecord
        {
            Id = gid,
            Name = ((string?)f["name"] ?? "Group chat").Trim() is { Length: > 0 } n ? (n.Length > 60 ? n[..60] : n) : "Group chat",
            Version = (long?)f["ver"] ?? 0,
            CreatedBy = (string?)f["by"] ?? from,
            Members = members,
        };
        var iAmIn = members.Any(m => m.Id == MyId);

        if (!_groups.TryGetValue(gid, out var existing))
        {
            // Only someone listed in the group can add us to it.
            if (iAmIn && members.Any(m => m.Id == from)) AdoptGroup(incoming);
            return;
        }

        var senderWasMember = existing.Record.Members.Any(m => m.Id == from);
        if (!senderWasMember || incoming.Version <= existing.Record.Version)
        {
            // They are behind. Send our copy back so everyone converges.
            if (incoming.Version < existing.Record.Version && existing.Record.Members.Any(m => m.Id == from))
                _ = _net.SendAsync(from, GroupFrame(existing.Record));
            return;
        }

        if (!iAmIn) { DropGroup(existing); return; }

        existing.Update(incoming);
        foreach (var m in incoming.Members.Where(m => m.Id != MyId)) ContactFor(m.Id, m.Name);
        if (_conversations.TryGetValue(existing.Key, out var conv)) conv.RefreshMembers();
    }

    void OnGroupMessage(string from, JsonObject f)
    {
        var gid = (string?)f["gid"];
        var id = (string?)f["id"];
        if (gid is null || string.IsNullOrEmpty(id) || id.Length > 64) return;
        if (!_groups.TryGetValue(gid, out var g) || g.Record.Members.All(m => m.Id != from)) return;

        _ = _net.SendAsync(from, new JsonObject { ["t"] = "gack", ["gid"] = gid, ["id"] = id });
        var text = (string?)f["text"] ?? "";
        Receive(ConversationFor(g), new ChatMessage
        {
            Id = id,
            From = from,
            Text = text.Length > 8000 ? text[..8000] : text,
            Time = DateTimeOffset.FromUnixTimeMilliseconds((long?)f["ts"] ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        }, from);
    }

    static GroupRecord Clone(GroupRecord r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Version = r.Version,
        CreatedBy = r.CreatedBy,
        Members = r.Members.Select(m => new GroupMember { Id = m.Id, Name = m.Name }).ToList(),
    };

    // ---- files ----

    readonly ConcurrentDictionary<string, FileTransfer> _transfers = new();

    public int InFlight(string peerId) => _net.InFlight(peerId);
    public Task<bool> SendFileFrame(string peerId, JsonObject frame) => _net.SendAsync(peerId, frame);

    public async void SendFile(ConversationViewModel conv, string path)
    {
        if (conv.IsGroup || conv.Contact is null) return;
        FileInfo info;
        try { info = new FileInfo(path); } catch { return; }
        if (!info.Exists) return;
        if (info.Length > FileTransfer.MaxSize)
        {
            conv.ShowNotice("That file is too big. The limit is 2 GB.");
            return;
        }
        if (!conv.Contact.IsOnline)
        {
            conv.ShowNotice($"{conv.Title} needs to be online to receive files.");
            return;
        }

        var t = new FileTransfer(this, Guid.NewGuid().ToString("N"), conv.Contact.Id, conv.Title, FileTransfer.SafeName(info.Name), info.Length, incoming: false)
        {
            SourcePath = info.FullName,
            State = TransferState.Preparing,
        };
        _transfers[t.Id] = t;
        var msg = new ChatMessage { Id = t.Id, From = MyId, Text = t.Name, Time = DateTimeOffset.Now, Kind = MessageKind.File };
        conv.Add(new MessageViewModel(msg, conv, mine: true, Name) { Transfer = t });

        try
        {
            await using var fs = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            t.Hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
        }
        catch (Exception ex)
        {
            t.Fail("Could not read the file: " + ex.Message);
            return;
        }
        if (t.State != TransferState.Preparing) return;

        var ok = await _net.SendAsync(t.PeerId, new JsonObject
        {
            ["t"] = "fileoffer",
            ["fid"] = t.Id,
            ["name"] = t.Name,
            ["size"] = t.Size,
            ["sha"] = t.Hash,
            ["ts"] = msg.Time.ToUnixTimeMilliseconds(),
        });
        if (ok) t.State = TransferState.Waiting;
        else t.Fail($"{conv.Title} went offline.");
    }

    void OnFileOffer(string from, JsonObject f)
    {
        var fid = (string?)f["fid"];
        var size = (long?)f["size"] ?? -1;
        var sha = (string?)f["sha"];
        if (string.IsNullOrEmpty(fid) || fid.Length > 64 || size < 0 || size > FileTransfer.MaxSize || sha is not { Length: 64 }) return;
        if (_transfers.ContainsKey(fid)) return;

        var contact = ContactFor(from);
        var conv = ConversationFor(contact);
        var t = new FileTransfer(this, fid, from, contact.Title, FileTransfer.SafeName((string?)f["name"] ?? "file"), size, incoming: true)
        {
            Hash = sha,
            State = TransferState.Offered,
        };
        _transfers[fid] = t;
        Receive(conv, new ChatMessage
        {
            Id = fid,
            From = from,
            Text = t.Name,
            Time = DateTimeOffset.FromUnixTimeMilliseconds((long?)f["ts"] ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            Kind = MessageKind.File,
        }, from, t);
    }

    void OnFileControl(string from, JsonObject f)
    {
        if ((string?)f["fid"] is not { } fid || !_transfers.TryGetValue(fid, out var t) || t.PeerId != from) return;
        switch ((string?)f["t"])
        {
            case "fileaccept" when !t.Incoming && t.State == TransferState.Waiting:
                t.StartSending();
                break;
            case "filedecline" when !t.Incoming:
                t.State = TransferState.Declined;
                break;
            case "filecancel":
                t.Cancel(notify: false);
                break;
            case "filegot" when !t.Incoming:
                t.Progress = 1;
                t.State = TransferState.Done;
                break;
        }
    }

    void OnChunk(string from, JsonObject f)
    {
        if ((string?)f["fid"] is not { } fid || !_transfers.TryGetValue(fid, out var t) || !t.Incoming || t.PeerId != from) return;
        if ((int?)f["i"] is not { } i || (string?)f["d"] is not { } d) return;
        byte[] data;
        try { data = Convert.FromBase64String(d); } catch { return; }
        t.OnChunk(i, data);
    }

    // ---- manual connect ----

    public async Task<string?> ConnectByAddress(string text)
    {
        if (!PeerNetwork.TryParseEndpoint(text, out var ep))
            return "That does not look like an address. Use something like 192.168.1.23";
        return await _net.ConnectManuallyAsync(ep);
    }

    public void Dispose()
    {
        foreach (var t in _transfers.Values) t.Cancel(notify: false);
        _tick.Stop();
        _net.Dispose();
        _me.Dispose();
    }
}

/// <summary>Allows a burst, then refills one token per interval.</summary>
public sealed class TokenBucket(int capacity, TimeSpan refill)
{
    double _tokens = capacity;
    DateTime _last = DateTime.UtcNow;

    public bool TryTake()
    {
        var now = DateTime.UtcNow;
        _tokens = Math.Min(capacity, _tokens + (now - _last).TotalMilliseconds / refill.TotalMilliseconds);
        _last = now;
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }
}
