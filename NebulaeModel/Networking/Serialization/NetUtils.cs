#region

using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

#endregion

namespace NebulaeModel.Networking.Serialization;

/// <summary>
///     Some specific network utilities
/// </summary>
internal static class NetUtils
{
    public static IPEndPoint MakeEndPoint(string hostStr, int port)
    {
        return new IPEndPoint(ResolveAddress(hostStr), port);
    }

    /// <summary>
    ///     Normalize a configured host. Removes surrounding whitespace and a matching pair of
    ///     IPv6 brackets, so that "example.com", " 1.2.3.4 " and "[::1]" all come back as the
    ///     plain host string. Does not resolve anything.
    /// </summary>
    public static string NormalizeHost(string host)
    {
        var result = host?.Trim() ?? string.Empty;
        if (result.Length > 1 && result[0] == '[' && result[result.Length - 1] == ']')
        {
            result = result.Substring(1, result.Length - 2);
        }
        if (result.Length == 0)
        {
            throw new ArgumentException("Invalid host: " + host);
        }
        return result;
    }

    /// <summary>
    ///     Build the websocket url for a host that stays a host: the name is kept for the Host
    ///     header and for the TLS server name, and is resolved by the socket layer when the
    ///     connection is actually opened. Does not resolve anything.
    /// </summary>
    public static string MakeWebSocketUrl(string protocol, string host, int port, string path = "/socket")
    {
        return $"{protocol}://{FormatHost(host)}:{port}{path}";
    }

    private static string FormatHost(string host)
    {
        return host.Contains(':') && host[0] != '[' ? $"[{host}]" : host;
    }

    private static IPAddress ResolveAddress(string hostStr)
    {
        if (hostStr == "localhost")
        {
            return IPAddress.Loopback;
        }

        if (!IPAddress.TryParse(hostStr, out var ipAddress))
        {
            // We can assume true because the version of unity is new enough (2018.4)
            ipAddress = ResolveAddress(hostStr, AddressFamily.InterNetworkV6) ??
                        ResolveAddress(hostStr, AddressFamily.InterNetwork);
        }
        if (ipAddress == null)
        {
            throw new ArgumentException("Invalid address: " + hostStr);
        }

        return ipAddress;
    }

    private static IPAddress ResolveAddress(string hostStr, AddressFamily addressFamily)
    {
        var addresses = ResolveAddresses(hostStr);
        return addresses.FirstOrDefault(ip => ip.AddressFamily == addressFamily);
    }

    private static IPAddress[] ResolveAddresses(string hostStr)
    {
#if NETSTANDARD || NETCOREAPP
        var hostTask = Dns.GetHostEntryAsync(hostStr);
        hostTask.GetAwaiter().GetResult();
        var host = hostTask.Result;
#else
        var host = Dns.GetHostEntry(hostStr);
#endif
        return host.AddressList;
    }
}
