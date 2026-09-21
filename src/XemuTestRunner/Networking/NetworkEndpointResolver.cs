using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using XemuTestRunner.Config;

namespace XemuTestRunner.Networking;

public sealed record AdvertisedEndpoint(
    string ListenAddress,
    string AdvertisedAddress,
    string Url,
    IReadOnlyList<string> Candidates);

public static class NetworkEndpointResolver
{
    public static AdvertisedEndpoint Resolve(HttpOptions options)
    {
        var listen = string.IsNullOrWhiteSpace(options.BindAddress)
            ? "0.0.0.0"
            : options.BindAddress.Trim();

        if (!string.IsNullOrWhiteSpace(options.AdvertiseAddress))
        {
            var advertised = options.AdvertiseAddress.Trim();
            return new(
                listen,
                advertised,
                BuildUrl(advertised, options.Port),
                [advertised]);
        }

        if (!IsWildcard(listen))
            return new(listen, listen, BuildUrl(listen, options.Port), [listen]);

        var candidates = GetCandidates();
        var selected = candidates.FirstOrDefault() ?? "127.0.0.1";
        return new(
            listen,
            selected,
            BuildUrl(selected, options.Port),
            candidates.Count == 0 ? [selected] : candidates);
    }

    private static List<string> GetCandidates()
    {
        var ranked = new List<(int Rank, string Address)>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            IPInterfaceProperties properties;
            try { properties = nic.GetIPProperties(); }
            catch { continue; }

            var hasGateway = properties.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.Any.Equals(g.Address));

            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(address) ||
                    address.Equals(IPAddress.Any))
                    continue;

                var bytes = address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                    continue;

                var preferredInterface = nic.NetworkInterfaceType is
                    NetworkInterfaceType.Ethernet or
                    NetworkInterfaceType.Wireless80211;
                var rank = (hasGateway ? 0 : 10) +
                           (preferredInterface ? 0 : 5) +
                           (IsPrivate(address) ? 0 : 1);
                ranked.Add((rank, address.ToString()));
            }
        }

        if (ranked.Count == 0)
        {
            try
            {
                foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address))
                        ranked.Add((50, address.ToString()));
                }
            }
            catch { }
        }

        return ranked
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Address, StringComparer.Ordinal)
            .Select(item => item.Address)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsWildcard(string value) =>
        value is "*" or "0.0.0.0" or "::" ||
        IPAddress.TryParse(value, out var parsed) &&
        (parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any));

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 ||
               b[0] == 172 && b[1] is >= 16 and <= 31 ||
               b[0] == 192 && b[1] == 168;
    }

    private static string BuildUrl(string host, int port)
    {
        if (IPAddress.TryParse(host, out var parsed) &&
            parsed.AddressFamily == AddressFamily.InterNetworkV6)
            host = $"[{host}]";
        return $"http://{host}:{port}";
    }
}
