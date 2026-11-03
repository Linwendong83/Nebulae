using System;
using System.Reflection;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace NebulaeModel.Logger;

internal static class SocketLogPolicy
{
    // The library uses Error/Fatal for failed connections too. Only known transport
    // log sites are warnings; callback failures and unfamiliar sites remain errors.
    internal static bool IsTransportFailure(MethodBase caller)
    {
        if (caller == null) return false;
        if (caller.DeclaringType == typeof(WebSocket))
        {
            switch (caller.Name)
            {
                case "accept":
                case "acceptHandshake":
                case "InternalAccept":
                case "connect":
                case "sendBytes":
                case "sendHandshakeRequest":
                case "validateSecWebSocketExtensionsServerHeader":
                case "processPingFrame":
                case "processUnsupportedFrame":
                    return true;
                case "send":
                    // This overload logs only a closing connection. The other send
                    // overload catches compression/stream errors and stays Error.
                    var parameters = caller.GetParameters();
                    return parameters.Length == 4 && parameters[0].ParameterType.FullName == "WebSocketSharp.Fin";
                case "Send":
                    // Only the byte[] cache overload logs a closing connection.
                    var arguments = caller.GetParameters();
                    return arguments.Length == 3 && arguments[1].ParameterType == typeof(byte[]);
            }
        }

        // Match the enclosing method, never compiler-assigned closure numbers.
        // receiveRequest itself is the server's listener loop and must stay Error.
        return IsLambdaIn(caller, typeof(WebSocket), "startReceiving") ||
               IsLambdaIn(caller, typeof(WebSocketServer), "receiveRequest");
    }

    private static bool IsLambdaIn(MethodBase caller, Type owner, string method)
    {
        if (!caller.Name.StartsWith("<" + method + ">", StringComparison.Ordinal)) return false;
        var type = caller.DeclaringType;
        while (type != null && type != owner) type = type.DeclaringType;
        return type == owner;
    }
}
