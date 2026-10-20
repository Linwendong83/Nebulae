#region

using System;

#endregion

namespace NebulaeModel.Contracts;

/// <summary>
///     Represents local player. Allows to send packets.
/// </summary>
internal interface ILocalPlayer : IDisposable
{
    bool IsInitialDataReceived { get; set; }
    bool IsHost { get; set; }
    bool IsClient { get; }
    bool IsNewPlayer { get; set; }
    ushort Id { get; }
    IPlayerData Data { get; set; }
}
