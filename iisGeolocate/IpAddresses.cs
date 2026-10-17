using System.Net;
using System.Net.Sockets;

namespace iisGeolocate;

public static class IpAddresses
{
    /// <summary>
    ///     Parses an IPv4 address in dotted decimal form (four parts) or an IPv6 address (optionally in brackets, with
    ///     a zone index). IPAddress.TryParse alone accepts things like "1" or "1.2" as IPv4 addresses.
    /// </summary>
    public static bool TryParse(string value, out IPAddress address)
    {
        address = null;

        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return false;
        }

        if (value.IndexOf(':') >= 0)
        {
            if (value.StartsWith("[") && value.EndsWith("]"))
            {
                value = value.Substring(1, value.Length - 2);
            }

            return IPAddress.TryParse(value, out address) && address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        var parts = value.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 3)
            {
                return false;
            }

            foreach (var c in part)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            if (int.Parse(part) > 255)
            {
                return false;
            }
        }

        return IPAddress.TryParse(value, out address);
    }

    /// <summary>
    ///     False for addresses that cannot be geolocated: loopback, private, shared (CGNAT), link local, multicast,
    ///     documentation, benchmarking, unspecified and reserved ranges. IPv4 mapped IPv6 addresses are checked as IPv4.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var b = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0 || //this network
                     b[0] == 10 ||
                     b[0] == 100 && (b[1] & 0xC0) == 64 || //100.64.0.0/10 shared (CGNAT)
                     b[0] == 127 ||
                     b[0] == 169 && b[1] == 254 ||
                     b[0] == 172 && (b[1] & 0xF0) == 16 ||
                     b[0] == 192 && b[1] == 0 && b[2] == 0 || //IETF protocol assignments
                     b[0] == 192 && b[1] == 0 && b[2] == 2 || //documentation
                     b[0] == 192 && b[1] == 168 ||
                     b[0] == 198 && (b[1] & 0xFE) == 18 || //benchmarking
                     b[0] == 198 && b[1] == 51 && b[2] == 100 ||
                     b[0] == 203 && b[1] == 0 && b[2] == 113 ||
                     b[0] >= 224); //multicast, reserved, broadcast
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6Any.Equals(address))
        {
            return false;
        }

        return !(b[0] == 0xFF || //multicast
                 (b[0] & 0xFE) == 0xFC || //unique local fc00::/7
                 b[0] == 0xFE && (b[1] & 0xC0) == 0x80 || //link local fe80::/10
                 b[0] == 0xFE && (b[1] & 0xC0) == 0xC0 || //site local fec0::/10
                 b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8); //documentation 2001:db8::/32
    }
}
