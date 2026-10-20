using System;

namespace NebulaeModel.Networking;

/// <summary>Version rules shared by the Nebulae handshake and server list.</summary>
internal static class NebulaeVersion
{
    public static string Normalize(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return string.Empty;
        var metadataIndex = version.IndexOf('+');
        return (metadataIndex >= 0 ? version.Substring(0, metadataIndex) : version).Trim();
    }

    public static bool IsCompatible(string hostVersion, string clientVersion)
    {
        var host = Normalize(hostVersion);
        var client = Normalize(clientVersion);
        return host.Length > 0 && client.Length > 0 &&
               string.Equals(host, client, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsKnownStatusVersion(string version)
    {
        var normalized = Normalize(version);
        return normalized.Length > 0;
    }
}
