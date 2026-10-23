using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PeerDM7.Net;

/// <summary>What this PC looks like on the network: its addresses and the subnets around them.</summary>
public static class NetInfo
{
    /// <summary>Largest block we probe on one adapter. A /22 is 1022 addresses.</summary>
    const int MaxSweepHostBits = 10;

    public sealed record Lan(IPAddress Address, IPAddress Mask, bool HasGateway);

    public static List<Lan> Lans()
    {
        var list = new List<Lan>();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return list; }

        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch { continue; }
            var gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var u in props.UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (u.IPv4Mask is null || u.IPv4Mask.Equals(IPAddress.Any)) continue;
                var b = u.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue; // no DHCP lease
                list.Add(new Lan(u.Address, u.IPv4Mask, gateway));
            }
        }
        return list;
    }

    public static IPAddress? PrimaryAddress()
    {
        var lans = Lans();
        return (lans.FirstOrDefault(l => l.HasGateway) ?? lans.FirstOrDefault())?.Address;
    }

    public static IEnumerable<IPAddress> BroadcastTargets()
    {
        var seen = new HashSet<IPAddress> { IPAddress.Broadcast };
        yield return IPAddress.Broadcast;
        foreach (var lan in Lans())
        {
            var a = lan.Address.GetAddressBytes();
            var m = lan.Mask.GetAddressBytes();
            var b = new byte[4];
            for (var i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
            var bc = new IPAddress(b);
            if (seen.Add(bc)) yield return bc;
        }
    }

    /// <summary>
    /// Every host address on the real LANs (adapters with a gateway). Big subnets are narrowed to
    /// the /22 around our own address, where classmates in the same room usually are.
    /// </summary>
    public static List<IPAddress> SweepTargets()
    {
        var result = new List<IPAddress>();
        var lans = Lans();
        var real = lans.Where(l => l.HasGateway).ToList();
        foreach (var lan in real.Count > 0 ? real : lans)
        {
            var self = ToUInt(lan.Address);
            var maskBits = 32 - CountHostBits(ToUInt(lan.Mask));
            var hostBits = Math.Min(32 - maskBits, MaxSweepHostBits);
            if (hostBits < 2) continue;
            var blockMask = hostBits == 32 ? 0u : uint.MaxValue << hostBits;
            var network = self & blockMask;
            var size = 1u << hostBits;
            for (uint i = 1; i < size - 1; i++)
            {
                var ip = network | i;
                if (ip != self) result.Add(FromUInt(ip));
            }
        }
        return result;
    }

    static int CountHostBits(uint mask)
    {
        var n = 0;
        while (n < 32 && (mask & (1u << n)) == 0) n++;
        return n;
    }

    static uint ToUInt(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    static IPAddress FromUInt(uint v) =>
        new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
}
