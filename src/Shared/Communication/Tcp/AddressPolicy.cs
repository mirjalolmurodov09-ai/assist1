using System.Net;
using System.Net.Sockets;

namespace ClassroomControl.Shared.Communication.Tcp;

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
