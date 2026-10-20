using System;
using System.Net;
using System.Globalization;

namespace NebulaeModel.Networking;

internal readonly struct ServerAddress
{
    public string Host { get; }
    public int Port { get; }
    public string Protocol { get; }

    private ServerAddress(string host, int port, string protocol)
    {
        Host = host;
        Port = port;
        Protocol = protocol;
    }

    public static bool TryParse(string input, int defaultPort, out ServerAddress address)
    {
        address = default;
        if (defaultPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(input)) return false;
        var value = input.Trim();
        if (value.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0) return false;
        var protocol = "ws";
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            protocol = value.Substring(0, scheme).ToLowerInvariant();
            if (protocol != "ws" && protocol != "wss") return false;
            value = value.Substring(scheme + 3);
        }
        if (value.EndsWith("/socket", StringComparison.OrdinalIgnoreCase))
            value = value.Substring(0, value.Length - 7);
        if (value.Contains("/")) return false;
        if (!value.StartsWith("[", StringComparison.Ordinal) && IPAddress.TryParse(value, out var rawIp) &&
            rawIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            value = "[" + value + "]";
        if (!Uri.TryCreate($"{protocol}://{value}", UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/") return false;
        var host = uri.Host.Trim('[', ']');
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return false;
        if (IPAddress.TryParse(host, out var ipAddress)) host = ipAddress.ToString();
        var closeBracket = value.StartsWith("[", StringComparison.Ordinal) ? value.IndexOf(']') : -1;
        var lastColon = value.LastIndexOf(':');
        var explicitPort = lastColon > closeBracket &&
                           (closeBracket >= 0 || value.IndexOf(':') == lastColon);
        var port = defaultPort;
        if (explicitPort && (!int.TryParse(value.Substring(lastColon + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out port) || port == 0)) return false;
        if (port is < 1 or > 65535) return false;
        address = new ServerAddress(host, port, protocol);
        return true;
    }
}
