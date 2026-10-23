using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PeerDM7.Core;

namespace PeerDM7.Net;

public sealed class Beacon
{
    [JsonPropertyName("app")] public string App { get; set; } = "PeerDM7";
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("t")] public string Type { get; set; } = "hello";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
}

/// <summary>
/// UDP broadcast discovery. Every few seconds each copy shouts "hello" on the LAN; anyone who
/// hears it answers with a unicast "reply" to the sender. Windows Firewall lets unicast answers
/// to our own broadcasts through by default, so a PC that cannot accept inbound connections
/// still learns about the others and can dial out to them.
/// </summary>
public sealed class Discovery : IDisposable
{
    public const int Port = 47007;
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    readonly Func<Beacon> _beacon;
    readonly CancellationTokenSource _cts = new();
    UdpClient? _udp;

    public event Action<Beacon, IPEndPoint>? Heard;
    public bool BoundToWellKnownPort { get; private set; }

    public Discovery(Func<Beacon> beacon) => _beacon = beacon;

    public void Start()
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            BoundToWellKnownPort = true;
        }
        catch (SocketException ex)
        {
            // Someone else holds the port exclusively. We can still broadcast and hear replies.
            Log.Warn($"UDP {Port} unavailable ({ex.SocketErrorCode}), discovery will only hear replies");
            _udp.Dispose();
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        }
        _udp.EnableBroadcast = true;

        // Stop ICMP "port unreachable" from killing the receive loop.
        try { _udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, [0], null); } catch { }

        _ = ReceiveLoop(_cts.Token);
        _ = AnnounceLoop(_cts.Token);
    }

    /// <summary>Shout now instead of waiting for the next tick, e.g. after a name change.</summary>
    public void AnnounceNow() => _ = AnnounceOnce("hello");

    /// <summary>Directed hello for people on another subnet, added by address.</summary>
    public void Poke(IPAddress address) => _ = Send(_beacon().WithType("hello"), new IPEndPoint(address, Port));

    async Task AnnounceLoop(CancellationToken ct)
    {
        // A burst on start-up makes people appear almost immediately.
        for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
        {
            await AnnounceOnce("hello");
            await Task.Delay(700, ct).ContinueWith(_ => { });
        }
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await AnnounceOnce("hello");
        }
        catch (OperationCanceledException) { }
    }

    async Task AnnounceOnce(string type)
    {
        var beacon = _beacon().WithType(type);
        foreach (var target in BroadcastTargets())
            await Send(beacon, new IPEndPoint(target, Port));
    }

    async Task Send(Beacon beacon, IPEndPoint to)
    {
        var udp = _udp;
        if (udp is null) return;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(beacon);
            await udp.SendAsync(bytes, to);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Interfaces come and go (Wi-Fi drops, VPN up). Next tick will try again.
        }
    }

    async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await _udp!.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            Beacon? b;
            try
            {
                if (r.Buffer.Length > 2048) continue;
                b = JsonSerializer.Deserialize<Beacon>(r.Buffer);
            }
            catch { continue; }
            if (b is null || b.App != "PeerDM7" || b.Id.Length == 0 || b.Port is <= 0 or > 65535) continue;

            var me = _beacon();
            if (b.Id == me.Id) continue;

            Heard?.Invoke(b, r.RemoteEndPoint);
            if (b.Type == "hello")
                await Send(me.WithType("reply"), r.RemoteEndPoint);
        }
    }

    public static IEnumerable<IPAddress> BroadcastTargets()
    {
        var seen = new HashSet<IPAddress> { IPAddress.Broadcast };
        yield return IPAddress.Broadcast;
        foreach (var (address, mask) in LocalIPv4())
        {
            var a = address.GetAddressBytes();
            var m = mask.GetAddressBytes();
            var b = new byte[4];
            for (var i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
            var bc = new IPAddress(b);
            if (seen.Add(bc)) yield return bc;
        }
    }

    public static IEnumerable<(IPAddress Address, IPAddress Mask)> LocalIPv4()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { yield break; }

        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch { continue; }
            foreach (var u in props.UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (u.IPv4Mask is null || u.IPv4Mask.Equals(IPAddress.Any)) continue;
                // 169.254.x.x means the adapter never got a real address.
                if (u.Address.GetAddressBytes()[0] == 169 && u.Address.GetAddressBytes()[1] == 254) continue;
                yield return (u.Address, u.IPv4Mask);
            }
        }
    }

    /// <summary>The address most likely to be the one people should type: the adapter with a gateway.</summary>
    public static IPAddress? PrimaryAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var props = nic.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                    continue;
                var a = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                if (a is not null) return a.Address;
            }
        }
        catch { }
        return LocalIPv4().Select(x => x.Address).FirstOrDefault();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
    }
}

static class BeaconExtensions
{
    // Beacons are tiny mutable DTOs; copy before changing the type so the shared one stays intact.
    public static Beacon WithType(this Beacon b, string type) => new()
    {
        App = b.App,
        Version = b.Version,
        Type = type,
        Id = b.Id,
        Name = b.Name,
        Port = b.Port,
    };
}
