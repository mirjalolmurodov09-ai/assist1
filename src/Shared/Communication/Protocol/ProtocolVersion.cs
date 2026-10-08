using System.Globalization;

namespace ClassroomControl.Shared.Communication.Protocol;

/// <summary>Protocol version. Same major = compatible; the lower minor is negotiated.</summary>
public readonly record struct ProtocolVersion(int Major, int Minor) : IComparable<ProtocolVersion>
{
    public static ProtocolVersion Parse(string? text)
    {
        if (!TryParse(text, out var v))
            throw new FormatException($"Invalid protocol version '{text}'.");
        return v;
    }

    public static bool TryParse(string? text, out ProtocolVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('.');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) return false;
        version = new ProtocolVersion(major, minor);
        return true;
    }

    public static bool TryNegotiate(ProtocolVersion local, ProtocolVersion peer, out ProtocolVersion negotiated)
    {
        negotiated = default;
        if (local.Major != peer.Major) return false;
        negotiated = local.CompareTo(peer) <= 0 ? local : peer;
        return true;
    }

    public int CompareTo(ProtocolVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
}
