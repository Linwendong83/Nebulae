#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Planet;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaeNetwork.PacketProcessors.Planet;

// Processes events for adding vegetation or veins in sandbox mode
internal class VegeAddProcessor : PacketProcessor<VegeAddPacket>
{
    protected override void ProcessPacket(VegeAddPacket packet, NebulaeConnection conn)
    {
        if (packet.Data == null || packet.Data.Length == 0 || packet.Data.Length > 2048 ||
            packet.IsPlanting && packet.IsVein) return;
        var planet = GameMain.galaxy.PlanetById(packet.PlanetId);
        if (planet?.factory == null) return;
        if (packet.IsRejected)
        {
            if (IsHost || !packet.IsPlanting || packet.IsVein) return;
            VegeData predicted = new();
            using (BinaryUtils.Reader reader = new(packet.Data, 2, byteHeader: true))
            {
                predicted.Import(reader.BinaryReader);
                reader.RequireEnd();
            }
            var pool = planet.factory.vegePool;
            if (predicted.id > 0 && predicted.id < planet.factory.vegeCursor && pool[predicted.id].id == predicted.id &&
                pool[predicted.id].protoId == predicted.protoId && pool[predicted.id].pos == predicted.pos)
                using (Multiplayer.Session.Planets.IsIncomingRequest.On()) planet.factory.RemoveVegeWithComponents(predicted.id);
            return;
        }
        ushort author = 0;
        if (IsHost)
        {
            var player = Players.Get(conn, EConnectionStatus.Connected);
            if (player == null || player.Data.LocalPlanetId != packet.PlanetId) return;
            author = player.Id;
            if (!Multiplayer.Session.Vegetation.AcceptCommand(author, packet.CommandSequence)) return;
            if (packet.IsPlanting)
            {
                VegeData planted = new();
                using (BinaryUtils.Reader reader = new(packet.Data, 2, byteHeader: true))
                {
                    planted.Import(reader.BinaryReader);
                    reader.RequireEnd();
                }
                var collection = Multiplayer.Session.Vegetation.GetRemote(author);
                if (LDB.veges.Select(planted.protoId) == null || collection == null ||
                    collection.RemoveVegeFromPlayer(planted.protoId, 1) != 1)
                {
                    packet.IsRejected = true;
                    conn.SendPacket(packet);
                    return;
                }
            }
        }
        using (Multiplayer.Session.Planets.IsIncomingRequest.On())
        {
            using var context = new NebulaeWorld.Factory.FactoryOperationScope();
            var factory = planet?.factory;
            if (factory == null)
            {
                return;
            }
            Multiplayer.Session.Factories.TargetPlanet = packet.PlanetId;
            Multiplayer.Session.Factories.EventFactory = factory;
            Multiplayer.Session.Factories.AddPlanetTimer(packet.PlanetId);
            var pData = GameMain.gpuiManager.specifyPlanet;
            GameMain.gpuiManager.specifyPlanet = planet;

            if (packet.IsVein)
            {
                VeinData veinData = new();
                using (BinaryUtils.Reader reader = new(packet.Data, 1, byteHeader: true))
                {
                    veinData.Import(reader.BinaryReader);
                    reader.RequireEnd();
                }

                // Modify from PlayerAction_Plant.PlantVeinFinally
                factory.AssignGroupIndexForNewVein(ref veinData);
                var veinId = planet.factory.AddVeinData(veinData);
                var rot = Quaternion.FromToRotation(Vector3.up, veinData.pos.normalized);
                var veinPool = planet.factory.veinPool;
                if (planet == GameMain.localPlanet)
                {
                    // Only add model on local planet
                    veinPool[veinId].modelId =
                        planet.factoryModel.gpuiManager.AddModel(veinData.modelIndex, veinId, veinData.pos, rot);
                }

                var veinProto = LDB.veins.Select((int)veinData.type);
                var colliders = veinProto.prefabDesc.colliders;
                var num2 = 0;
                while (colliders != null && num2 < colliders.Length)
                {
                    veinPool[veinId].colliderId = planet.physics.AddColliderData(colliders[num2]
                        .BindToObject(veinId, veinPool[veinId].colliderId, EObjectType.Vein, veinData.pos, rot));
                    num2++;
                }
                factory.RefreshVeinMiningDisplay(veinId, 0, 0);
                factory.RecalculateVeinGroup(veinData.groupIndex);
            }
            else
            {
                VegeData vegeData = new();
                using (BinaryUtils.Reader reader = new(packet.Data, 2, byteHeader: true))
                {
                    vegeData.Import(reader.BinaryReader);
                    reader.RequireEnd();
                }

                // Modify from PlayerAction_Plant.PlantVegeFinally
                var num = planet.factory.AddVegeData(vegeData);
                var vegePool = planet.factory.vegePool;
                if (planet == GameMain.localPlanet)
                {
                    // Only add model on local planet
                    vegePool[num].modelId =
                        planet.factoryModel.gpuiManager.AddModel(vegeData.modelIndex, num, vegeData.pos, vegeData.rot);
                }

                var vegeProto = LDB.veges.Select(vegeData.protoId);
                var colliders = vegeProto.prefabDesc.colliders;
                var num2 = 0;
                while (colliders != null && num2 < colliders.Length)
                {
                    vegePool[num].colliderId = planet.physics.AddColliderData(colliders[num2].BindToObject(num,
                        vegePool[num].colliderId, EObjectType.Vegetable, vegeData.pos, vegeData.rot, Vector3.one));
                    num2++;
                }
                planet.physics.SetPlanetPhysicsColliderDirty();
            }

            GameMain.gpuiManager.specifyPlanet = pData;
            Multiplayer.Session.Factories.TargetPlanet = SessionSentinels.PLANET_NONE;
            Multiplayer.Session.Factories.EventFactory = null;
        }
        if (IsHost)
        {
            Multiplayer.Session.Server.SendPacketToStarExclude(packet, planet.star.id, conn);
            if (packet.IsPlanting)
                Multiplayer.Session.Vegetation.CaptureRemote(author);
        }
    }
}
