using System.Net;
using System.Net.Sockets;

namespace PZAdvancedServerManager.Core.Pz;

public sealed record ServerNetworkInfo(
    IReadOnlyList<string> Addresses,
    string BindAddress,
    int? DefaultPort,
    int? UdpPort,
    int? SteamPort1,
    int? SteamPort2,
    string RconHost,
    int RconPort,
    bool ConfigurationAvailable,
    bool? IsPublic,
    bool? IsOpen)
{
    public static ServerNetworkInfo Create(ServerConfigEntry profile, ServerConfigDocument? document)
    {
        var rconHost = profile.IsRemote
            ? string.IsNullOrWhiteSpace(profile.Remote!.RconHost) ? profile.Remote.Host : profile.Remote.RconHost
            : "127.0.0.1";
        var rconPort = profile.IsRemote ? profile.Remote!.RconPort : ParsePort(document?.Get("RCONPort")) ?? 27015;
        if (document is null)
            return new ServerNetworkInfo([rconHost], string.Empty, null, null, null, null, rconHost, rconPort, false, null, null);

        var defaultPort = ParsePort(document.Get("DefaultPort")) ?? 16261;
        var udpPort = ParsePort(document.Get("UDPPort")) ?? (defaultPort < 65535 ? defaultPort + 1 : null);
        var bindAddress = document.Get("IP").Trim();
        var addresses = profile.IsRemote
            ? RemoteAddresses(bindAddress, rconHost)
            : LocalAddresses(bindAddress);
        return new ServerNetworkInfo(
            addresses,
            bindAddress,
            defaultPort,
            udpPort,
            ParsePort(document.Get("SteamPort1")),
            ParsePort(document.Get("SteamPort2")),
            rconHost,
            rconPort,
            true,
            ParseBoolean(document.Get("Public")),
            ParseBoolean(document.Get("Open")));
    }

    private static IReadOnlyList<string> LocalAddresses(string bindAddress)
    {
        if (!string.IsNullOrWhiteSpace(bindAddress) && bindAddress is not "0.0.0.0" and not "::") return [bindAddress];
        try
        {
            return new[] { IPAddress.Loopback }
                .Concat(Dns.GetHostAddresses(Dns.GetHostName()))
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Where(address => !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (SocketException) { return [IPAddress.Loopback.ToString()]; }
    }

    private static IReadOnlyList<string> RemoteAddresses(string bindAddress, string rconHost)
        => new[] { bindAddress, rconHost }
            .Where(value => !string.IsNullOrWhiteSpace(value) && value is not "0.0.0.0" and not "::")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int? ParsePort(string? value)
        => int.TryParse(value, out var port) && port is >= 1 and <= 65535 ? port : null;

    private static bool? ParseBoolean(string? value)
        => bool.TryParse(value, out var parsed) ? parsed : null;
}
