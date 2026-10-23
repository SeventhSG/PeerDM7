namespace PeerDM7.Core;

public enum PeerStatus
{
    Online,
    Away,
    Busy,
    Offline,
}

public enum MessageKind
{
    Text,
    Nudge,
}

public sealed class ChatMessage
{
    public required string Id { get; init; }
    public required string From { get; init; }
    public required string Text { get; init; }
    public required DateTimeOffset Time { get; init; }
    public MessageKind Kind { get; init; } = MessageKind.Text;

    /// <summary>
    /// For my own messages: the people who have not confirmed receipt yet. Anyone listed here
    /// gets it again if their connection drops and comes back during this session.
    /// </summary>
    public HashSet<string> Pending { get; init; } = [];

    /// <summary>Whether it ever left this PC (to at least one person).</summary>
    public bool Sent { get; set; }
}

/// <summary>A group chat. Membership is replicated to every member; the newest version wins. Lives in memory only.</summary>
public sealed class GroupRecord
{
    public const int MaxMembers = 20;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<GroupMember> Members { get; set; } = [];
    public long Version { get; set; }
    public string CreatedBy { get; set; } = "";
}

public sealed class GroupMember
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class PeerProfile
{
    public string Name { get; set; } = "";
    public string PersonalMessage { get; set; } = "";
    public PeerStatus Status { get; set; } = PeerStatus.Online;
}

public static class StatusText
{
    public static string For(PeerStatus s) => s switch
    {
        PeerStatus.Online => "Online",
        PeerStatus.Away => "Away",
        PeerStatus.Busy => "Busy",
        _ => "Offline",
    };

    public static PeerStatus Parse(string? s) => s switch
    {
        "away" => PeerStatus.Away,
        "busy" => PeerStatus.Busy,
        "offline" => PeerStatus.Offline,
        _ => PeerStatus.Online,
    };

    public static string Wire(PeerStatus s) => s.ToString().ToLowerInvariant();
}
