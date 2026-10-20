using System.Net.Sockets;
using System.Reflection;

namespace NebulaeModel.Networking;

internal static class TcpSocketOptions
{
    internal static bool TryEnableNoDelay(object owner, out bool previous)
    {
        previous = false;
        if (owner == null) return false;
        for (var type = owner.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField("_tcpClient", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field?.GetValue(owner) is not TcpClient client) continue;
            previous = client.NoDelay; client.NoDelay = true; return true;
        }
        return false;
    }
}
