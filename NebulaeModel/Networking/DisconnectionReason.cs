namespace NebulaeModel.Networking;

internal enum DisconnectionReason
{
    // Websocket Error Codes
    Normal = 1000,
    ProtocolError = 1002,
    InvalidData = 1007,

    // Nebulae Specific Error Codes
    HostStillLoading = 2000,
    ClientRequestedDisconnect = 2001,
    ModVersionMismatch = 2002,
}
