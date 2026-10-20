using System;
using System.IO;
using System.Linq;
using System.Reflection;
using WebSocketSharp;

namespace NebulaeModel.Networking;

// OrderedSendQueue already owns a background I/O lane. The library's public SendAsync
// adds a delegate BeginInvoke plus a second pool callback per message. Use the same
// library send implementation directly, retaining its boolean failure result.
internal static class BlockingWebSocketSender
{
    private static readonly MethodInfo send = typeof(WebSocket).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(method => method.Name == "send" && method.ReturnType == typeof(bool) && method.GetParameters().Length == 2 &&
            method.GetParameters()[0].ParameterType.IsEnum && method.GetParameters()[1].ParameterType == typeof(Stream));
    private static readonly object binary = Enum.Parse(send.GetParameters()[0].ParameterType, "Binary");

    internal static bool Send(WebSocket socket, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        return (bool)send.Invoke(socket, new[] { binary, (object)stream });
    }
}
