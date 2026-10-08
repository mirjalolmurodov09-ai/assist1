using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace ClassroomControl.StudentAgent.Infrastructure.Helpers;

public sealed record LocalAdapter(IPAddress Address, IPAddress Mask, string MacAddress);

public interface ILocalNetworkInfo
{
    /// <summary>The adapter the agent uses to reach the classroom LAN, or null when offline.</summary>
    LocalAdapter? GetPrimaryAdapter();
    IReadOnlyList<IPAddress> GetBroadcastTargets();
    /// <summary>True when the address is in one of this machine's own subnets.</summary>
    bool IsOnLocalSubnet(IPAddress address);
    /// <summary>Stable text describing current addresses; changes when the network changes.</summary>
    string GetNetworkSignature();
}

public sealed class LocalNetworkInfo : ILocalNetworkInfo
{
    private readonly AgentOptions _options;

    public LocalNetworkInfo(IOptions<AgentOptions> options) => _options = options.Value;

    public LocalAdapter? GetPrimaryAdapter()
    {
        LocalAdapter? best = null;
        var bestScore = -1;
        foreach (var nic in Candidates())
        {
            var props = nic.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var apipa = AddressPolicy.IsLinkLocal(ua.Address);
                var score = (props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) ? 2 : 0)
                            + (apipa ? 0 : 1);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new LocalAdapter(ua.Address, ua.IPv4Mask, FormatMac(nic.GetPhysicalAddress()));
                }
            }
        }
        return best;
    }

    public IReadOnlyList<IPAddress> GetBroadcastTargets()
    {
        if (_options.DiscoveryTargets.Length > 0)
            return _options.DiscoveryTargets.Select(IPAddress.Parse).ToList();

        var targets = new List<IPAddress>();
        foreach (var nic in Candidates())
        {
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var a = ua.Address.GetAddressBytes();
                var m = ua.IPv4Mask.GetAddressBytes();
                var b = new byte[4];
                for (var i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
                var broadcast = new IPAddress(b);
                if (!targets.Contains(broadcast)) targets.Add(broadcast);
            }
        }
        if (targets.Count > 0) targets.Add(IPAddress.Broadcast);
        return targets;
    }

    public bool IsOnLocalSubnet(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var target = address.GetAddressBytes();
        foreach (var nic in Candidates())
        {
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var a = ua.Address.GetAddressBytes();
                var m = ua.IPv4Mask.GetAddressBytes();
                var same = true;
                for (var i = 0; i < 4 && same; i++) same = (a[i] & m[i]) == (target[i] & m[i]);
                if (same) return true;
            }
        }
        return false;
    }

    public string GetNetworkSignature() => string.Join(';',
        Candidates().SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address.ToString())).Order(StringComparer.Ordinal));

    private static IEnumerable<NetworkInterface> Candidates() => NetworkInterface.GetAllNetworkInterfaces().Where(n =>
        n.OperationalStatus == OperationalStatus.Up
        && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel));

    private static string FormatMac(PhysicalAddress mac) =>
        string.Join('-', mac.GetAddressBytes().Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
}

/// <summary>Decides which Teacher addresses the agent is allowed to talk to: never public internet addresses.</summary>
public static class AddressPolicy
{
    public static bool IsPermitted(IPAddress address, bool allowLoopback)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(address)) return allowLoopback;
        return IsPrivate(address) || IsLinkLocal(address);
    }

    public static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public static bool IsLinkLocal(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }
}
