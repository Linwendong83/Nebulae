#region

using NebulaeModel.Networking;
using NebulaeWorld;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics.ControlPanel;

#endregion

namespace NebulaeNetwork.PacketProcessors.Logistics.ControlPanel;
internal class LCPObjectEntryRequestProcessor : PacketProcessor<LCPObjectEntryRequest>
{
    protected override void ProcessPacket(LCPObjectEntryRequest packet, NebulaeConnection conn)
    {
        if (IsClient) return;
        SendEntry(conn, packet);
    }

    internal static void SendEntry(INebulaeConnection conn, LCPObjectEntryRequest packet)
    {
        var factory = GameMain.galaxy.PlanetById(packet.AstroId)?.factory;
        if (factory == null) return;

        var isInit = false;
        var objId = packet.ObjId;
        if (objId < 0)
        {
            objId = -objId;
            isInit = true;
        }
        if (objId <= 0 || objId >= factory.entityPool.Length) return;
        ref var entity = ref factory.entityPool[objId];
        if (entity.id != objId) return;

        switch ((EControlPanelEntryType)packet.EntryType)
        {
            case EControlPanelEntryType.InterstellarStation:
            case EControlPanelEntryType.OrbitCollector:
            case EControlPanelEntryType.LocalStation:
                if (entity.stationId == 0) return;
                if (isInit)
                {
                    LCPObjectEntryEntityInfo.Instance.Set(packet.Index, entity.protoId, entity.stationId, factory.ReadExtraInfoOnEntity(objId));
                    LCPObjectEntryEntityInfo.Instance.Generation = packet.Generation;
                    LCPObjectEntryEntityInfo.Instance.AstroId = packet.AstroId; LCPObjectEntryEntityInfo.Instance.ObjId = objId;
                    Multiplayer.Session.FacilityDirectory.SendChanged(conn, LCPObjectEntryEntityInfo.Instance, packet.Index);
                }
                var station = factory.transport.stationPool[entity.stationId];
                LCPStationEntryUpdate.Instance.Set(packet.Index, station, factory);
                LCPStationEntryUpdate.Instance.Generation = packet.Generation;
                LCPStationEntryUpdate.Instance.AstroId = packet.AstroId; LCPStationEntryUpdate.Instance.ObjId = objId;
                Multiplayer.Session.FacilityDirectory.SendChanged(conn, LCPStationEntryUpdate.Instance, packet.Index);
                break;

            case EControlPanelEntryType.VeinCollector:
                if (entity.stationId == 0) return;
                if (isInit)
                {
                    LCPObjectEntryEntityInfo.Instance.Set(packet.Index, entity.protoId, entity.stationId, factory.ReadExtraInfoOnEntity(objId));
                    LCPObjectEntryEntityInfo.Instance.Generation = packet.Generation;
                    LCPObjectEntryEntityInfo.Instance.AstroId = packet.AstroId; LCPObjectEntryEntityInfo.Instance.ObjId = objId;
                    Multiplayer.Session.FacilityDirectory.SendChanged(conn, LCPObjectEntryEntityInfo.Instance, packet.Index);
                }
                var veinCollector = factory.transport.stationPool[entity.stationId];
                LCPAdvancedMinerEntryUpdate.Instance.Set(packet.Index, veinCollector, factory);
                LCPAdvancedMinerEntryUpdate.Instance.Generation = packet.Generation;
                LCPAdvancedMinerEntryUpdate.Instance.AstroId = packet.AstroId; LCPAdvancedMinerEntryUpdate.Instance.ObjId = objId;
                Multiplayer.Session.FacilityDirectory.SendChanged(conn, LCPAdvancedMinerEntryUpdate.Instance, packet.Index);
                break;

            case EControlPanelEntryType.Dispenser:
                if (entity.dispenserId == 0) return;
                if (isInit)
                {
                    LCPObjectEntryEntityInfo.Instance.Set(packet.Index, entity.protoId, entity.dispenserId, factory.ReadExtraInfoOnEntity(objId));
                    LCPObjectEntryEntityInfo.Instance.Generation = packet.Generation;
                    LCPObjectEntryEntityInfo.Instance.AstroId = packet.AstroId; LCPObjectEntryEntityInfo.Instance.ObjId = objId;
                    Multiplayer.Session.FacilityDirectory.SendChanged(conn, LCPObjectEntryEntityInfo.Instance, packet.Index);
                }
                var dispenser = factory.transport.dispenserPool[entity.dispenserId];
                LCPDispenserEntryUpdate.Instance.Set(packet.Index, dispenser, factory);
                LCPDispenserEntryUpdate.Instance.Generation = packet.Generation;
                LCPDispenserEntryUpdate.Instance.AstroId = packet.AstroId; LCPDispenserEntryUpdate.Instance.ObjId = objId;
                Multiplayer.Session.FacilityDirectory.SendChanged(conn, LCPDispenserEntryUpdate.Instance, packet.Index);
                break;
        }
    }
}
