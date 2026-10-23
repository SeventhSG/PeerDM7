using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PeerDM7.Core;

namespace PeerDM7.Net;

/// <summary>
/// Everything goes over one UDP socket, and nothing ever waits for an unsolicited connection.
///
/// Why: a student PC without admin rights cannot open the firewall. Windows Firewall does let
/// in replies to traffic the PC sent itself. So every copy keeps sending small probes to the
/// addresses on its subnet; once two copies have both sent to each other, each firewall treats
/// the other's packets as replies and lets them through. No rule, no prompt answer, no admin.
///
/// On top of that: a signed hello carrying session keys, AES-GCM on every data packet, and a
/// small reliability layer (sequence numbers, acks, retransmits) because UDP can drop packets.
/// Events fire on background threads; the UI layer marshals them.
/// </summary>
public sealed class PeerNetwork : IDisposable
{
    public const int DefaultPort = 47007;

    const byte Version = 1;
    const byte TProbe = 0, THello = 1, TData = 2;
    const int IdLength = 20;
    const int HeaderLength = 4 + IdLength;
    const int MaxFrame = 60_000;
    static readonly byte[] HelloContext = "PeerDM7/hello/1"u8.ToArray();

    static readonly TimeSpan AnnounceEvery = TimeSpan.FromSeconds(3);
    static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(8);
    static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(4);
    static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(20);
    static readonly TimeSpan RetransmitAfter = TimeSpan.FromMilliseconds(450);
    const int MaxTries = 24;

    readonly Identity _me;
    readonly Func<PeerProfile> _profile;
    readonly ECDiffieHellman _eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    readonly byte[] _ephPub;
    readonly byte[] _hello;
    readonly object _gate = new();
    readonly Dictionary<string, Peer> _peers = new();
    readonly Dictionary<IPEndPoint, DateTime> _helloSentTo = new();
    readonly List<IPEndPoint> _manual = new();
    readonly CancellationTokenSource _cts = new();
    UdpClient? _udp;

    public int Port { get; private set; }
    public bool ListeningOnDefaultPort => Port == DefaultPort;

    public event Action<string, IPEndPoint>? PeerConnected;
    public event Action<string>? PeerDisconnected;
    public event Action<string, PeerProfile>? ProfileReceived;
    public event Action<string, ChatMessage>? MessageReceived;
    public event Action<string, string>? Delivered;
    public event Action<string, string?, bool>? TypingChanged;
    /// <summary>Frames the network layer does not interpret itself (group sync, group messages).</summary>
    public event Action<string, JsonObject>? OtherFrame;

    sealed class Peer
    {
        public required string Id;
        public required IPEndPoint Endpoint;
        public required AesGcm SendKey;
        public required AesGcm RecvKey;
        public long NextSeq = 1;
        public readonly Dictionary<long, Outgoing> Unacked = new();
        public readonly HashSet<long> Seen = new();
        public long SeenFloor;
        public DateTime LastHeard = DateTime.MinValue;
        public bool Connected;
        public readonly object Lock = new();
    }

    sealed class Outgoing
    {
        public required byte[] Packet;
        public DateTime LastSent;
        public int Tries;
    }

    public PeerNetwork(Identity me, Func<PeerProfile> profile)
    {
        _me = me;
        _profile = profile;
        _ephPub = _eph.PublicKey.ExportSubjectPublicKeyInfo();
        var sig = _me.Key.SignData(Concat(HelloContext, _ephPub), HashAlgorithmName.SHA256);
        var body = new JsonObject
        {
            ["idk"] = Convert.ToBase64String(_me.PublicKey),
            ["eph"] = Convert.ToBase64String(_ephPub),
            ["sig"] = Convert.ToBase64String(sig),
        };
        _hello = Packet(THello, Encoding.UTF8.GetBytes(body.ToJsonString()));
    }

    public void Start()
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            _udp.Client.ExclusiveAddressUse = true;
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DefaultPort));
        }
        catch (SocketException)
        {
            // Port taken, usually by a second copy on the same PC. Any port works: the others
            // answer to wherever our packets come from.
            _udp.Dispose();
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        }
        _udp.EnableBroadcast = true;
        _udp.Client.ReceiveBufferSize = 1 << 20;
        // Probing empty addresses brings back ICMP "port unreachable"; it must not fail receives.
        try { _udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, [0], null); } catch { }
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        Log.Info($"UDP {Port} as {_me.Id}");

        _ = ReceiveLoop(_cts.Token);
        _ = AnnounceLoop(_cts.Token);
        _ = SweepLoop(_cts.Token);
        _ = ReliabilityLoop(_cts.Token);
    }

    // ---- public API ----

    public bool IsConnected(string peerId)
    {
        lock (_gate) return _peers.TryGetValue(peerId, out var p) && p.Connected;
    }

    /// <summary>Reliable packets to this person still waiting for an ack. File transfers pace themselves on it.</summary>
    public int InFlight(string peerId)
    {
        Peer? p;
        lock (_gate) _peers.TryGetValue(peerId, out p);
        if (p is null) return int.MaxValue;
        lock (p.Lock) return p.Unacked.Count;
    }

    public Task<bool> SendAsync(string peerId, JsonObject frame)
    {
        Peer? p;
        lock (_gate) _peers.TryGetValue(peerId, out p);
        return Task.FromResult(p is { Connected: true } && SendReliable(p, frame));
    }

    public void BroadcastProfile()
    {
        List<Peer> all;
        lock (_gate) all = _peers.Values.Where(p => p.Connected).ToList();
        foreach (var p in all) SendReliable(p, ProfileFrame());
    }

    /// <summary>Reach someone by address (another subnet). Null on success, or a short reason.</summary>
    public async Task<string?> ConnectManuallyAsync(IPEndPoint ep)
    {
        lock (_gate) if (!_manual.Contains(ep)) _manual.Add(ep);
        for (var i = 0; i < 8; i++)
        {
            Raw(_hello, ep);
            await Task.Delay(750);
            lock (_gate)
                if (_peers.Values.Any(p => p.Connected && p.Endpoint.Address.Equals(ep.Address))) return null;
        }
        return $"No answer yet. PeerDM7 keeps trying in the background. Ask them to add your address too: {NetInfo.PrimaryAddress()}";
    }

    // ---- loops ----

    async Task AnnounceLoop(CancellationToken ct)
    {
        try
        {
            for (var i = 0; !ct.IsCancellationRequested; i++)
            {
                List<IPEndPoint> targets;
                lock (_gate)
                {
                    targets = NetInfo.BroadcastTargets().Select(a => new IPEndPoint(a, DefaultPort)).ToList();
                    targets.AddRange(_manual);
                    targets.AddRange(_peers.Values.Where(p => !p.Connected && DateTime.UtcNow - p.LastHeard < TimeSpan.FromMinutes(5)).Select(p => p.Endpoint));
                }
                foreach (var t in targets) Raw(_hello, t);
                await Task.Delay(i < 3 ? TimeSpan.FromMilliseconds(800) : AnnounceEvery, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task SweepLoop(CancellationToken ct)
    {
        var probe = Packet(TProbe, []);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var targets = NetInfo.SweepTargets();
                // Spread the probes out instead of one burst.
                const int batch = 64;
                for (var i = 0; i < targets.Count; i += batch)
                {
                    foreach (var ip in targets.Skip(i).Take(batch)) Raw(probe, new IPEndPoint(ip, DefaultPort));
                    await Task.Delay(120, ct);
                }
                await Task.Delay(SweepEvery, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task ReliabilityLoop(CancellationToken ct)
    {
        var lastPing = DateTime.UtcNow;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(150, ct);
                var now = DateTime.UtcNow;
                var ping = now - lastPing > PingEvery;
                if (ping) lastPing = now;

                List<Peer> all;
                lock (_gate) all = _peers.Values.ToList();
                foreach (var p in all)
                {
                    if (p.Connected && now - p.LastHeard > DeadAfter)
                    {
                        lock (p.Lock) { p.Connected = false; p.Unacked.Clear(); }
                        Log.Info($"Lost {p.Id}");
                        PeerDisconnected?.Invoke(p.Id);
                        continue;
                    }

                    List<Outgoing> resend;
                    lock (p.Lock)
                    {
                        foreach (var dead in p.Unacked.Where(kv => kv.Value.Tries >= MaxTries).Select(kv => kv.Key).ToList())
                            p.Unacked.Remove(dead);
                        resend = p.Unacked.Values.Where(o => now - o.LastSent > RetransmitAfter).ToList();
                        foreach (var o in resend) { o.LastSent = now; o.Tries++; }
                    }
                    foreach (var o in resend) Raw(o.Packet, p.Endpoint);

                    // Pings keep both firewalls remembering this conversation.
                    if (ping && p.Connected) SendUnreliable(p, new JsonObject { ["t"] = "ping" });
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp!.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            try { Handle(r.Buffer, r.RemoteEndPoint); }
            catch (Exception ex) { Log.Warn($"Bad packet from {r.RemoteEndPoint}: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    // ---- packets ----

    void Handle(byte[] buf, IPEndPoint from)
    {
        if (buf.Length < HeaderLength || buf[0] != (byte)'P' || buf[1] != (byte)'D' || buf[2] != Version) return;
        var senderId = Encoding.ASCII.GetString(buf, 4, IdLength).TrimEnd('\0');
        if (senderId == _me.Id) return;

        switch (buf[3])
        {
            case TProbe:
                // Someone is sweeping. Tell them who we are, at most every couple of seconds.
                lock (_gate)
                {
                    if (_helloSentTo.TryGetValue(from, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(2)) return;
                    if (_helloSentTo.Count > 4096) _helloSentTo.Clear();
                    _helloSentTo[from] = DateTime.UtcNow;
                }
                Raw(_hello, from);
                break;

            case THello:
                OnHello(buf[HeaderLength..], senderId, from);
                break;

            case TData:
                OnData(buf, senderId, from);
                break;
        }
    }

    void OnHello(byte[] body, string claimedId, IPEndPoint from)
    {
        Peer? peer;
        lock (_gate) _peers.TryGetValue(claimedId, out peer);

        if (peer is null)
        {
            if (JsonNode.Parse(body) is not JsonObject h) return;
            var idk = Convert.FromBase64String((string?)h["idk"] ?? "");
            var eph = Convert.FromBase64String((string?)h["eph"] ?? "");
            var sig = Convert.FromBase64String((string?)h["sig"] ?? "");
            if (Identity.IdFromPublicKey(idk) != claimedId) return;
            using (var verifier = ECDsa.Create())
            {
                verifier.ImportSubjectPublicKeyInfo(idk, out _);
                if (!verifier.VerifyData(Concat(HelloContext, eph), sig, HashAlgorithmName.SHA256)) return;
            }

            using var theirEph = ECDiffieHellman.Create();
            theirEph.ImportSubjectPublicKeyInfo(eph, out _);
            var secret = _eph.DeriveRawSecretAgreement(theirEph.PublicKey);
            var salt = _ephPub.AsSpan().SequenceCompareTo(eph) <= 0
                ? SHA256.HashData(Concat(_ephPub, eph))
                : SHA256.HashData(Concat(eph, _ephPub));
            var sendKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, Encoding.UTF8.GetBytes("PeerDM7/key/" + _me.Id));
            var recvKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, Encoding.UTF8.GetBytes("PeerDM7/key/" + claimedId));
            CryptographicOperations.ZeroMemory(secret);

            var fresh = new Peer
            {
                Id = claimedId,
                Endpoint = from,
                SendKey = new AesGcm(sendKey, 16),
                RecvKey = new AesGcm(recvKey, 16),
            };
            lock (_gate)
            {
                if (_peers.TryGetValue(claimedId, out var raced)) peer = raced;
                else _peers[claimedId] = peer = fresh;
            }
            Log.Info($"Hello from {claimedId} at {from}");
        }

        if (!peer.Connected)
        {
            peer.Endpoint = from;
            // Make sure they can derive our keys too, then knock with real data. The first
            // packet that decrypts proves the path works both ways.
            Raw(_hello, from);
            SendUnreliable(peer, new JsonObject { ["t"] = "ping" });
        }
    }

    void OnData(byte[] buf, string senderId, IPEndPoint from)
    {
        Peer? p;
        lock (_gate) _peers.TryGetValue(senderId, out p);
        if (p is null)
        {
            // They know us but we missed their hello. Our hello makes them send theirs again.
            Raw(_hello, from);
            return;
        }
        if (buf.Length < HeaderLength + 12 + 16) return;

        var cipherLen = buf.Length - HeaderLength - 12 - 16;
        var plain = new byte[cipherLen];
        lock (p.Lock)
            p.RecvKey.Decrypt(buf.AsSpan(HeaderLength, 12), buf.AsSpan(HeaderLength + 12, cipherLen),
                buf.AsSpan(buf.Length - 16), plain, buf.AsSpan(0, HeaderLength));

        p.Endpoint = from;
        p.LastHeard = DateTime.UtcNow;
        bool becameConnected;
        lock (p.Lock)
        {
            becameConnected = !p.Connected;
            p.Connected = true;
        }
        if (becameConnected)
        {
            Log.Info($"Connected to {p.Id} at {from}");
            SendUnreliable(p, new JsonObject { ["t"] = "ping" });
            PeerConnected?.Invoke(p.Id, from);
            SendReliable(p, ProfileFrame());
            Introduce(p);
        }

        if (JsonNode.Parse(plain) is not JsonObject f) return;

        if ((string?)f["t"] == "rack")
        {
            if ((long?)f["q"] is { } acked) lock (p.Lock) p.Unacked.Remove(acked);
            return;
        }

        if ((long?)f["q"] is { } seq)
        {
            SendUnreliable(p, new JsonObject { ["t"] = "rack", ["q"] = seq });
            lock (p.Lock)
            {
                if (seq <= p.SeenFloor || !p.Seen.Add(seq)) return;
                while (p.Seen.Remove(p.SeenFloor + 1)) p.SeenFloor++;
            }
        }

        OnFrame(p.Id, f);
    }

    /// <summary>
    /// Tell a newcomer about everyone we can reach, and everyone about the newcomer. Both ends
    /// then knock at the same time, which opens both firewalls. This also links up people the
    /// subnet probe cannot reach, as long as somebody in between can.
    /// </summary>
    void Introduce(Peer newcomer)
    {
        List<Peer> others;
        lock (_gate) others = _peers.Values.Where(o => o.Connected && o != newcomer).ToList();
        if (others.Count == 0) return;
        SendReliable(newcomer, PeersFrame(others));
        foreach (var o in others) SendReliable(o, PeersFrame([newcomer]));
    }

    static JsonObject PeersFrame(IEnumerable<Peer> peers) => new()
    {
        ["t"] = "peers",
        ["list"] = new JsonArray(peers.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["ep"] = p.Endpoint.ToString() }).ToArray()),
    };

    async Task Knock(IPEndPoint ep)
    {
        for (var i = 0; i < 6 && !_cts.IsCancellationRequested; i++)
        {
            Raw(_hello, ep);
            await Task.Delay(500);
        }
    }

    bool SendReliable(Peer p, JsonObject frame)
    {
        byte[] packet;
        lock (p.Lock)
        {
            var seq = p.NextSeq++;
            frame["q"] = seq;
            packet = Seal(p, frame);
            if (packet.Length > MaxFrame) return false;
            p.Unacked[seq] = new Outgoing { Packet = packet, LastSent = DateTime.UtcNow };
        }
        Raw(packet, p.Endpoint);
        return true;
    }

    void SendUnreliable(Peer p, JsonObject frame)
    {
        byte[] packet;
        lock (p.Lock) packet = Seal(p, frame);
        Raw(packet, p.Endpoint);
    }

    byte[] Seal(Peer p, JsonObject frame)
    {
        var plain = Encoding.UTF8.GetBytes(frame.ToJsonString());
        var packet = new byte[HeaderLength + 12 + plain.Length + 16];
        WriteHeader(packet, TData);
        var nonce = packet.AsSpan(HeaderLength, 12);
        RandomNumberGenerator.Fill(nonce);
        p.SendKey.Encrypt(nonce, plain, packet.AsSpan(HeaderLength + 12, plain.Length),
            packet.AsSpan(packet.Length - 16), packet.AsSpan(0, HeaderLength));
        return packet;
    }

    byte[] Packet(byte type, byte[] body)
    {
        var packet = new byte[HeaderLength + body.Length];
        WriteHeader(packet, type);
        body.CopyTo(packet, HeaderLength);
        return packet;
    }

    void WriteHeader(byte[] packet, byte type)
    {
        packet[0] = (byte)'P';
        packet[1] = (byte)'D';
        packet[2] = Version;
        packet[3] = type;
        Encoding.ASCII.GetBytes(_me.Id.PadRight(IdLength, '\0'), 0, IdLength, packet, 4);
    }

    void Raw(byte[] packet, IPEndPoint to)
    {
        try { _udp?.Send(packet, packet.Length, to); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Unreachable hosts and adapters going away are normal while probing.
        }
    }

    // ---- frames ----

    void OnFrame(string id, JsonObject f)
    {
        switch ((string?)f["t"])
        {
            case "ping":
                break;

            case "peers":
                foreach (var o in (f["list"] as JsonArray)?.OfType<JsonObject>().Take(64) ?? [])
                {
                    var pid = (string?)o["id"];
                    if (pid is null || pid == _me.Id || IsConnected(pid)) continue;
                    if (IPEndPoint.TryParse((string?)o["ep"] ?? "", out var ep) && ep.AddressFamily == AddressFamily.InterNetwork)
                        _ = Knock(ep);
                }
                break;

            case "profile":
                byte[]? pic = null;
                if ((string?)f["pic"] is { Length: > 0 and < 60_000 } b64)
                    try { pic = Convert.FromBase64String(b64); } catch { }
                ProfileReceived?.Invoke(id, new PeerProfile
                {
                    Name = Clip((string?)f["name"], 40),
                    PersonalMessage = Clip((string?)f["psm"], 120),
                    Status = StatusText.Parse((string?)f["status"]),
                    Picture = pic,
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
                _ = SendAsync(id, new JsonObject { ["t"] = "ack", ["id"] = msgId });
                break;

            case "ack":
                if ((string?)f["id"] is { } ackId) Delivered?.Invoke(id, ackId);
                break;

            case "typing":
                TypingChanged?.Invoke(id, (string?)f["gid"], (bool?)f["on"] == true);
                break;

            default:
                OtherFrame?.Invoke(id, f);
                break;
        }
    }

    JsonObject ProfileFrame()
    {
        var p = _profile();
        return new JsonObject
        {
            ["t"] = "profile",
            ["name"] = p.Name,
            ["psm"] = p.PersonalMessage,
            ["status"] = StatusText.Wire(p.Status),
            ["pic"] = p.Picture is { Length: > 0 } pic ? Convert.ToBase64String(pic) : "",
        };
    }

    public static bool TryParseEndpoint(string text, out IPEndPoint ep)
    {
        text = text.Trim();
        if (IPEndPoint.TryParse(text, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            ep = parsed.Port == 0 ? new IPEndPoint(parsed.Address, DefaultPort) : parsed;
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

    static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _eph.Dispose();
        lock (_gate)
            foreach (var p in _peers.Values) { p.SendKey.Dispose(); p.RecvKey.Dispose(); }
    }
}
