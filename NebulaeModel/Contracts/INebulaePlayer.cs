#region

using NebulaeModel.Networking;

#endregion

namespace NebulaeModel.Contracts;

internal interface INebulaePlayer
{
    INebulaeConnection Connection { get; set; }
    IPlayerData Data { get; set; }
    ushort Id { get; }

    void SendPacket<T>(T packet) where T : class, new();

    void LoadUserData(IPlayerData data);
}
