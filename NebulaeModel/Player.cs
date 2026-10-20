#region

using NebulaeModel.Contracts;
using NebulaeModel.Networking;

#endregion

namespace NebulaeModel;

internal class NebulaePlayer : INebulaePlayer
{
    public NebulaePlayer(INebulaeConnection connection, IPlayerData data)
    {
        Connection = connection;
        Data = data;
    }

    public INebulaeConnection Connection { get; set; }
    public IPlayerData Data { get; set; }
    public ushort Id => Data.PlayerId;

    public void SendPacket<T>(T packet) where T : class, new()
    {
        Connection.SendPacket(packet);
    }

    public void LoadUserData(IPlayerData data)
    {
        var localId = Id;
        Data = data;
        Data.PlayerId = localId;
    }
}
