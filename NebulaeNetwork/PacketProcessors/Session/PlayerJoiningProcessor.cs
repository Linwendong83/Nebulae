#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Session;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Session;
internal class PlayerJoiningProcessor : PacketProcessor<PlayerJoining>
{
    protected override void ProcessPacket(PlayerJoining packet, NebulaeConnection conn)
    {
        Multiplayer.Session.NumPlayers = packet.NumPlayers;
        // While this client is still loading, the completion snapshot spawns the same model.
        // Announcing here as well would show that player joining twice.
        Multiplayer.Session.World.SpawnRemotePlayerModel(packet.PlayerData, Multiplayer.Session.IsGameLoaded);
        Multiplayer.Session.World.OnPlayerJoining(packet.PlayerData.Username);
    }
}
