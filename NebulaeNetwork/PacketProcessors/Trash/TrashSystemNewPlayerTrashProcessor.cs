
#region
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Trash;
using NebulaeWorld;
using NebulaeWorld.Trash;
using UnityEngine;

#endregion

namespace NebulaeNetwork.PacketProcessors.Trash;
internal class TrashSystemNewPlayerTrashProcessor : PacketProcessor<TrashSystemNewPlayerTrashPacket>
{
    protected override void ProcessPacket(TrashSystemNewPlayerTrashPacket packet, NebulaeConnection conn)
    {
        var player = GameMain.mainPlayer;
        if (packet.PlayerId != Multiplayer.Session.LocalPlayer.Id)
        {
            using (Multiplayer.Session.World.GetRemotePlayersModels(out var remotePlayersModels))
            {
                if (remotePlayersModels.TryGetValue(packet.PlayerId, out var remotePlayerModel))
                {
                    player = remotePlayerModel.PlayerInstance;
                }
                else
                {
                    return;
                }
            }
        }

        if (IsClient)
        {
            TrashManager.SetNextTrashId(packet.TrashId);
        }

        //Modify from AddTrash
        var trashSystem = GameMain.data.trashSystem;
        var vectorLF = Maths.QRotateLF(player.uRotation, new VectorLF3(0f, 1f, 0f));
        var vectorLF2 = player.uPosition;
        var trashObject = new TrashObject(packet.ItemId, packet.Count, packet.Inc, Vector3.zero, Quaternion.identity);
        var trashData = default(TrashData);
        trashData.landPlanetId = 0;
        trashData.nearPlanetId = 0;
        trashData.nearStarId = packet.NearStarId;
        var star = GameMain.galaxy.StarById(packet.NearStarId / 100);
        trashData.nearStarGravity = star != null ? trashSystem.GetStarGravity(packet.NearStarId / 100) : 0.0;
        trashData.life = packet.Life;
        trashData.lPos = Vector3.zero;
        trashData.lRot = Quaternion.identity;
        trashData.uPos = vectorLF2 + RandomTable.SphericNormal(ref trashSystem.randSeed, 0.3);
        trashData.uRot = Quaternion.LookRotation(RandomTable.SphericNormal(ref trashSystem.randSeed, 1.0).normalized, vectorLF);
        trashData.uVel = packet.UVel.ToVectorLF3();
        trashData.uAgl = RandomTable.SphericNormal(ref trashSystem.randSeed, 0.03);
        GameMain.gameScenario?.NotifyOnAddTrash(packet.ItemId, packet.Count);

        if (IsClient)
        {
            trashData.warningId = -1; // Wait until WarningDataPacket to assign warningId
            Multiplayer.Session.Trashes.ClientTrashCount++;
        }

        using (Multiplayer.Session.Trashes.IsIncomingRequest.On())
        {
            var trashId = trashSystem.container.NewTrash(trashObject, trashData);
            if (IsHost)
            {
                packet.TrashId = trashId;
                Server.SendPacket(packet);
                return;
            }
            if (trashId != packet.TrashId)
            {
                NebulaeModel.Logger.Log.Warn($"TrashSystemNewPlayerTrashPacket mismatch: {packet.TrashId} => {trashId}");
            }
        }
    }
}
