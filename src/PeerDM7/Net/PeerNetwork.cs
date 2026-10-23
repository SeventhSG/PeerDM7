using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PeerDM7.Core;

namespace PeerDM7.Net;

/// <summary>
/// Owns discovery, the listener and every live session. Keeps at most one session per peer:
/// when both sides dial each other at the same time, the session started by the lower ID wins
/// on both ends, so they agree without talking about it.
///
/// Events fire on background threads; the UI layer marshals them.
/// </summary>
public sealed class PeerNetwork : IDisposable
{
    static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(15);
    static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(50);
    static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(12);

    readonly Identity _me;
    readonly Func<PeerProfile> _profile;
    readonly object _gate = new();
    readonly Dictionary<string, PeerConnection> _sessions = new();
    readonly HashSet<string> _dialing = new();
    readonly Dictionary<string, DateTime> _backoff = new();
    readonly Dictionary<string, string> _manualIds = new();
    readonly List<IPEndPoint> _manual = new();
    readonly CancellationTokenSource _cts = new();

    TcpListener? _listener;
    Discovery? _discovery;

    public int TcpPort { get; private set; }
    public bool ListeningOnDefaultPort => TcpPort == Discovery.Port;

    public event Action<string, IPEndPoint>? PeerConnected;
    public event Action<string>? PeerDisconnected;
    public event Action<string, PeerProfile>? ProfileReceived;
    public event Action<string, ChatMessage>? MessageReceived;
    public event Action<string, string>? Delivered;
    public event Action<string, string?, bool>? TypingChanged;
    /// <summary>Frames the network layer does not interpret itself (group sync, group messages).</summary>
    public event Action<string, JsonObject>? OtherFrame;

    public PeerNetwork(Identity me, Func<PeerProfile> profile)
    {
        _me = me;
        _profile = profile;
    }

    public void Start(IEnumerable<string> manualPeers)
    {
        _listener = TryListen(Discovery.Port) ?? TryListen(0)
                    ?? throw new InvalidOperationException("Could not open a TCP port");
        TcpPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Log.Info($"Listening on TCP {TcpPort} as {_me.Id}");
        _ = AcceptLoop(_cts.Token);

        _discovery = new Discovery(() => new Beacon { Id = _me.Id, Name = _profile().Name, Port = TcpPort });
        _discovery.Heard += OnHeard;
        _discovery.Start();

        foreach (var m in manualPeers)
            if (TryParseEndpoint(m, out var ep)) lock (_gate) _manual.Add(ep);

        _ = Housekeeping(_cts.Token);
    }

    static TcpListener? TryListen(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Server.ExclusiveAddressUse = true;
            l.Start();
            return l;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    public bool IsConnected(string peerId)
    {
        lock (_gate) return _sessions.TryGetValue(peerId, out var s) && !s.IsClosed;
    }

    public IPEndPoint? EndpointOf(string peerId)
    {
        lock (_gate) return _sessions.TryGetValue(peerId, out var s) ? s.RemoteEndPoint : null;
    }

    public async Task<bool> SendAsync(string peerId, JsonObject frame)
    {
        PeerConnection? s;
        lock (_gate) _sessions.TryGetValue(peerId, out s);
        return s is not null && await s.SendAsync(frame);
    }

    public void BroadcastProfile()
    {
        List<PeerConnection> all;
        lock (_gate) all = _sessions.Values.ToList();
        var frame = ProfileFrame();
        foreach (var s in all) _ = s.SendAsync(frame.DeepClone().AsObject());
        _discovery?.AnnounceNow();
    }

    /// <summary>Connect to someone by address. Returns null on success or a short reason.</summary>
    public async Task<string?> ConnectManuallyAsync(IPEndPoint ep)
    {
        lock (_gate)
            if (!_manual.Any(m => m.Equals(ep))) _manual.Add(ep);
        _discovery?.Poke(ep.Address);
        var result = await DialAsync(ep, force: true);
        return result;
    }

    public IReadOnlyList<string> ManualPeers()
    {
        lock (_gate) return _manual.Select(e => e.ToString()).ToList();
    }

    public void Disconnect(string peerId)
    {
        PeerConnection? s;
        lock (_gate) _sessions.TryGetValue(peerId, out s);
        s?.Close();
    }

    // ---- discovery and dialling ----

    void OnHeard(Beacon b, IPEndPoint from)
    {
        if (b.Id == _me.Id) return;
        if (IsConnected(b.Id)) return;
        _ = DialAsync(new IPEndPoint(from.Address, b.Port), force: false);
    }

    async Task<string?> DialAsync(IPEndPoint ep, bool force)
    {
        var key = ep.ToString();
        lock (_gate)
        {
            if (_dialing.Contains(key)) return null;
            if (!force && _backoff.TryGetValue(key, out var until) && DateTime.UtcNow < until) return null;
            _dialing.Add(key);
        }

        var tcp = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await tcp.ConnectAsync(ep, timeout.Token);
            var session = await PeerConnection.EstablishAsync(tcp, _me, outbound: true, _cts.Token);
            if (session.RemoteId == _me.Id)
            {
                session.Dispose();
                return "That address is this PC.";
            }
            lock (_gate)
            {
                _backoff.Remove(key);
                if (_manual.Any(m => m.Equals(ep))) _manualIds[key] = session.RemoteId;
            }
            Adopt(session);
            return null;
        }
        catch (OperationCanceledException)
        {
            tcp.Dispose();
            lock (_gate) _backoff[key] = DateTime.UtcNow + RetryAfterFailure;
            return "No answer. Check the address, and that PeerDM7 is open on that PC.";
        }
        catch (SocketException ex)
        {
            tcp.Dispose();
            lock (_gate) _backoff[key] = DateTime.UtcNow + RetryAfterFailure;
            return ex.SocketErrorCode == SocketError.ConnectionRefused
                ? "That PC refused the connection. PeerDM7 may not be running there."
                : "Could not reach that PC (" + ex.SocketErrorCode + ").";
        }
        catch (Exception ex)
        {
            tcp.Dispose();
            lock (_gate) _backoff[key] = DateTime.UtcNow + RetryAfterFailure;
            Log.Warn($"Dial {key} failed: {ex.GetType().Name}: {ex.Message}");
            return "Something answered, but it is not PeerDM7.";
        }
        finally
        {
            lock (_gate) _dialing.Remove(key);
        }
    }

    async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            _ = Task.Run(async () =>
            {
                try
                {
                    var session = await PeerConnection.EstablishAsync(tcp, _me, outbound: false, ct);
                    if (session.RemoteId == _me.Id) { session.Dispose(); return; }
                    Adopt(session);
                }
                catch (Exception ex)
                {
                    tcp.Dispose();
                    if (ex is not OperationCanceledException)
                        Log.Warn($"Inbound handshake failed: {ex.GetType().Name}: {ex.Message}");
                }
            }, ct);
        }
    }

    void Adopt(PeerConnection session)
    {
        var id = session.RemoteId;
        PeerConnection? replaced = null;
        lock (_gate)
        {
            if (_sessions.TryGetValue(id, out var existing) && !existing.IsClosed)
            {
                var preferred = string.CompareOrdinal(_me.Id, id) < 0 ? _me.Id : id;
                if (existing.InitiatorId == preferred || session.InitiatorId != preferred)
                {
                    session.Dispose();
                    return;
                }
                replaced = existing;
            }
            _sessions[id] = session;
        }

        session.FrameReceived += OnFrame;
        session.Closed += OnClosed;
        session.Start();
        replaced?.Dispose();

        _ = session.SendAsync(ProfileFrame());
        if (replaced is null)
        {
            Log.Info($"Connected to {id} at {session.RemoteEndPoint}");
            PeerConnected?.Invoke(id, session.RemoteEndPoint);
        }
    }

    void OnClosed(PeerConnection session)
    {
        bool wasCurrent;
        lock (_gate)
        {
            wasCurrent = _sessions.TryGetValue(session.RemoteId, out var cur) && ReferenceEquals(cur, session);
            if (wasCurrent) _sessions.Remove(session.RemoteId);
        }
        if (wasCurrent)
        {
            Log.Info($"Disconnected from {session.RemoteId}");
            PeerDisconnected?.Invoke(session.RemoteId);
        }
    }

    void OnFrame(PeerConnection session, JsonObject f)
    {
        var id = session.RemoteId;
        try
        {
            switch ((string?)f["t"])
            {
                case "profile":
                    ProfileReceived?.Invoke(id, new PeerProfile
                    {
                        Name = Clip((string?)f["name"], 40),
                        PersonalMessage = Clip((string?)f["psm"], 120),
                        Status = StatusText.Parse((string?)f["status"]),
                    });
                    break;

                case "msg":
                    var msgId = (string?)f["id"];
                    if (string.IsNullOrEmpty(msgId) || msgId.Length > 64) return;
                    var ts = (long?)f["ts"] ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    MessageReceived?.Invoke(id, new ChatMessage
                    {
                        Id = msgId,
                        From = id,
                        Text = Clip((string?)f["text"], 8000),
                        Time = DateTimeOffset.FromUnixTimeMilliseconds(ts),
                        Kind = (bool?)f["nudge"] == true ? MessageKind.Nudge : MessageKind.Text,
                    });
                    _ = session.SendAsync(new JsonObject { ["t"] = "ack", ["id"] = msgId });
                    break;

                case "ack":
                    if ((string?)f["id"] is { } ackId) Delivered?.Invoke(id, ackId);
                    break;

                case "typing":
                    TypingChanged?.Invoke(id, (string?)f["gid"], (bool?)f["on"] == true);
                    break;

                case "ping":
                    _ = session.SendAsync(new JsonObject { ["t"] = "pong" });
                    break;

                case "pong":
                    break;

                default:
                    OtherFrame?.Invoke(id, f);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Bad frame from {id}: {ex.Message}");
        }
    }

    JsonObject ProfileFrame()
    {
        var p = _profile();
        var f = new JsonObject
        {
            ["t"] = "profile",
            ["name"] = p.Name,
            ["psm"] = p.PersonalMessage,
            ["status"] = StatusText.Wire(p.Status),
        };
        return f;
    }

    async Task Housekeeping(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PingEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                List<PeerConnection> all;
                List<IPEndPoint> manual;
                lock (_gate)
                {
                    all = _sessions.Values.ToList();
                    manual = _manual.Where(m => !(_manualIds.TryGetValue(m.ToString(), out var mid) && _sessions.ContainsKey(mid))).ToList();
                }
                foreach (var s in all)
                {
                    if (DateTime.UtcNow - s.LastHeard > DeadAfter) s.Close();
                    else _ = s.SendAsync(new JsonObject { ["t"] = "ping" });
                }
                foreach (var ep in manual)
                {
                    _discovery?.Poke(ep.Address);
                    _ = DialAsync(ep, force: false);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public static bool TryParseEndpoint(string text, out IPEndPoint ep)
    {
        text = text.Trim();
        if (IPEndPoint.TryParse(text, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            ep = parsed.Port == 0 ? new IPEndPoint(parsed.Address, Discovery.Port) : parsed;
            return true;
        }
        ep = null!;
        return false;
    }

    static string Clip(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length > max ? s[..max] : s;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _discovery?.Dispose();
        try { _listener?.Stop(); } catch { }
        List<PeerConnection> all;
        lock (_gate) all = _sessions.Values.ToList();
        foreach (var s in all) s.Dispose();
    }
}
