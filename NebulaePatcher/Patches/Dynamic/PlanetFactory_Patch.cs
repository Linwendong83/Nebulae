#region

using System.Collections.Generic;
using HarmonyLib;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Factory;
using NebulaeModel.Packets.Factory.Assembler;
using NebulaeModel.Packets.Factory.Foundation;
using NebulaeModel.Packets.Factory.Inserter;
using NebulaeModel.Packets.Factory.Miner;
using NebulaeModel.Packets.Factory.PowerExchanger;
using NebulaeModel.Packets.Factory.PowerGenerator;
using NebulaeModel.Packets.Factory.RayReceiver;
using NebulaeModel.Packets.Factory.Tank;
using NebulaeModel.Packets.Logistics;
using NebulaeModel.Packets.Planet;
using NebulaeWorld;
using NebulaeWorld.Factory;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(PlanetFactory))]
internal class PlanetFactory_patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetFactory.RemovePrebuildWithComponents))]
    public static void RemovePrebuildWithComponents_Postfix(PlanetFactory __instance, int id)
    {
        if (Multiplayer.IsActive) Multiplayer.Session.BuildDispatch.TargetRemoved(__instance, id);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetFactory.FlushPools))]
    public static bool FlushPools_Prefix()
    {
        // In vanilla, FlushPools is triggered by unload planet event which will happen at different time for each player
        // So this optimize pool function is disabled in multiplayer to keep pool in sync
        return !Multiplayer.IsActive;
    }


    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetFactory.AddPrebuildData))]
    public static void AddPrebuildData_Postfix(PlanetFactory __instance, ref int __result)
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }

        // If the host game called the method, we need to compute the PrebuildId ourself.
        // PacketAuthor is the player who placed it; the host executes that request locally.
        if (Multiplayer.Session.LocalPlayer.IsHost)
        {
            var author = Multiplayer.Session.Factories.PacketAuthor == SessionSentinels.AUTHOR_NONE
                ? Multiplayer.Session.LocalPlayer.Id
                : Multiplayer.Session.Factories.PacketAuthor;
            Multiplayer.Session.Factories.SetPrebuildRequest(__instance.planetId, __result, (ushort)author);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetFactory.BuildFinally))]
    public static bool BuildFinally_Prefix(PlanetFactory __instance, int prebuildId)
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }

        if (Multiplayer.Session.LocalPlayer.IsHost)
        {
            if (!Multiplayer.Session.Factories.ContainsPrebuildRequest(__instance.planetId, prebuildId))
            {
                // This prevents duplicating the entity when multiple players trigger the BuildFinally for the same entity at the same time.
                return false;
            }

            // Remove the prebuild request from the list since we will now convert it to a real building
            Multiplayer.Session.Factories.RemovePrebuildRequest(__instance.planetId, prebuildId);
        }

        if (!Multiplayer.Session.LocalPlayer.IsHost && Multiplayer.Session.Factories.IsIncomingRequest.Value)
        {
            return Multiplayer.Session.LocalPlayer.IsHost || Multiplayer.Session.Factories.IsIncomingRequest.Value;
        }
        var author = Multiplayer.Session.Factories.PacketAuthor == SessionSentinels.AUTHOR_NONE
            ? Multiplayer.Session.LocalPlayer.Id
            : Multiplayer.Session.Factories.PacketAuthor;
        var entityId = Multiplayer.Session.LocalPlayer.IsHost ? FactoryManager.GetNextEntityId(__instance) : -1;
        Multiplayer.Session.Network.SendPacket(new BuildEntityRequest(__instance.planetId, prebuildId, author, entityId));

        return Multiplayer.Session.LocalPlayer.IsHost || Multiplayer.Session.Factories.IsIncomingRequest.Value;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetFactory.UpgradeFinally))]
    public static bool UpgradeFinally_Prefix(PlanetFactory __instance, int objId, ItemProto replace_item_proto)
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }

        if (objId == 0 || replace_item_proto == null)
        {
            return false;
        }

        if (Multiplayer.Session.LocalPlayer.IsHost || !Multiplayer.Session.Factories.IsIncomingRequest.Value)
        {
            Multiplayer.Session.Network.SendPacket(new UpgradeEntityRequest(__instance.planetId, objId,
                FactoryManager.GetObjectProtoId(__instance, objId),
                replace_item_proto.ID,
                Multiplayer.Session.Factories.PacketAuthor == -1
                    ? Multiplayer.Session.LocalPlayer.Id
                    : Multiplayer.Session.Factories.PacketAuthor));
        }

        return Multiplayer.Session.LocalPlayer.IsHost || Multiplayer.Session.Factories.IsIncomingRequest.Value;
    }


    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetFactory.PasteBuildingSetting))]
    public static void PasteBuildingSetting_Prefix(int objectId)
    {
        if (Multiplayer.IsActive && !Multiplayer.Session.Factories.IsIncomingRequest.Value)
        {
            Multiplayer.Session.Network.SendPacketToLocalStar(new PasteBuildingSettingUpdate(objectId,
                BuildingParameters.clipboard, GameMain.localPlanet?.id ?? -1));
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetFactory.FlattenTerrainReform))]
    public static void FlattenTerrainReform_Prefix(Vector3 center, float radius, int reformSize,
        bool veinBuried, float fade0)
    {
        if (Multiplayer.IsActive && !Multiplayer.Session.Factories.IsIncomingRequest.Value)
        {
            var packet = new FoundationBuildUpdatePacket(center, radius, reformSize, veinBuried, fade0);
            if (Multiplayer.Session.LocalPlayer.IsHost)
                Multiplayer.Session.Network.SendPacketToLocalStar(packet);
            else Multiplayer.Session.Network.SendPacket(packet);
        }
        if (Multiplayer.IsActive) Multiplayer.Session.Vegetation.EnterBulkAction();
    }

    [HarmonyFinalizer]
    [HarmonyPatch(nameof(PlanetFactory.FlattenTerrainReform))]
    public static void FlattenTerrainReform_Finalizer()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Vegetation.LeaveBulkAction();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetFactory.RestoreTerrainReform))]
    public static void RestoreTerrainReform_Prefix(Vector3 center, float radius, int reformSize,
        bool veinBuried, float fade0)
    {
        if (Multiplayer.IsActive && !Multiplayer.Session.Factories.IsIncomingRequest.Value)
        {
            var packet = new FoundationBuildUpdatePacket(center, radius, reformSize, veinBuried, fade0, true);
            if (Multiplayer.Session.LocalPlayer.IsHost)
                Multiplayer.Session.Network.SendPacketToLocalStar(packet);
            else Multiplayer.Session.Network.SendPacket(packet);
        }
        if (Multiplayer.IsActive) Multiplayer.Session.Vegetation.EnterBulkAction();
    }

    [HarmonyFinalizer]
    [HarmonyPatch(nameof(PlanetFactory.RestoreTerrainReform))]
    public static void RestoreTerrainReform_Finalizer()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Vegetation.LeaveBulkAction();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetFactory.PlanetReformAll))]
    public static void PlanetReformAll_Prefix(PlanetFactory __instance, int type, int color, bool bury)
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }
        if (!Multiplayer.Session.Planets.IsIncomingRequest)
        {
            Multiplayer.Session.Network.SendPacketToLocalStar(new PlanetReformPacket(__instance.planetId, true, type, color,
                bury));
        }
        // Stop VegeMinedPacket from sending
        Multiplayer.Session.Planets.EnableVeinPacket = false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetFactory.PlanetReformAll))]
    public static void PlanetReformAll_Postfix()
    {
        if (Multiplayer.IsActive)
        {
            Multiplayer.Session.Planets.EnableVeinPacket = true;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetFactory.PlanetReformRevert))]
    public static void PlanetReformRevert_Prefix(PlanetFactory __instance)
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }
        if (!Multiplayer.Session.Planets.IsIncomingRequest)
        {
            Multiplayer.Session.Network.SendPacketToLocalStar(new PlanetReformPacket(__instance.planetId, false));
        }
        // Stop VegeMinedPacket from sending
        Multiplayer.Session.Planets.EnableVeinPacket = false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetFactory.PlanetReformRevert))]
    public static void PlanetReformRevert_Postfix()
    {
        if (Multiplayer.IsActive)
        {
            Multiplayer.Session.Planets.EnableVeinPacket = true;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetFactory.AddVegeData))]
    public static void AddVegeData_Postfix(PlanetFactory __instance, VegeData vege)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.Planets.IsIncomingRequest ||
            Multiplayer.Session.Vegetation.IsBulkAction)
        {
            return;
        }
        using BinaryUtils.Writer writer = new();
        vege.Export(writer.BinaryWriter);
        var packet = new VegeAddPacket(__instance.planetId, false, writer.CloseAndGetBytes(),
            Multiplayer.Session.Vegetation.IsPlanting);
        if (Multiplayer.Session.LocalPlayer.IsHost)
            Multiplayer.Session.Network.SendPacketToLocalStar(packet);
        else Multiplayer.Session.Network.SendPacket(packet);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetFactory.AddVeinData))]
    public static void AddVeinData_Postfix(PlanetFactory __instance, VeinData vein)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.Planets.IsIncomingRequest)
        {
            return;
        }
        using BinaryUtils.Writer writer = new();
        vein.Export(writer.BinaryWriter);
        var packet = new VegeAddPacket(__instance.planetId, true, writer.CloseAndGetBytes());
        if (Multiplayer.Session.LocalPlayer.IsHost)
            Multiplayer.Session.Network.SendPacketToLocalStar(packet);
        else Multiplayer.Session.Network.SendPacket(packet);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetFactory.RemoveVegeWithComponents))]
    public static void RemoveVegeWithComponents_Postfix(PlanetFactory __instance, int id)
    {
        if (Multiplayer.IsActive && !Multiplayer.Session.Planets.IsIncomingRequest &&
            !Multiplayer.Session.Vegetation.IsBulkAction &&
            Multiplayer.Session.Planets.EnableVeinPacket)
        {
            var packet = new VegeMinedPacket(__instance.planetId, id, 0, false,
                Multiplayer.Session.Vegetation.IsDirectAction);
            if (Multiplayer.Session.LocalPlayer.IsHost)
                Multiplayer.Session.Network.SendPacketToLocalStar(packet);
            else Multiplayer.Session.Network.SendPacket(packet);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetFactory.RemoveVeinWithComponents))]
    public static void RemoveVeinWithComponents_Postfix(PlanetFactory __instance, int id)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.Planets.IsIncomingRequest ||
            !Multiplayer.Session.Planets.EnableVeinPacket)
        {
            return;
        }
        if (Multiplayer.Session.LocalPlayer.IsHost)
        {
            Multiplayer.Session.Network.SendPacketToStar(new VegeMinedPacket(__instance.planetId, id, 0, true),
                __instance.planet.star.id);
        }
        else Multiplayer.Session.Network.SendPacket(new VegeMinedPacket(__instance.planetId, id, 0, true));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetFactory.WriteExtraInfoOnEntity))]
    public static void WriteExtraInfoOnEntity_Postfix(PlanetFactory __instance, int entityId, string info)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.Factories.IsIncomingRequest.Value)
        {
            return;
        }
        Multiplayer.Session.Network.SendPacketToLocalStar(
            new ExtraInfoUpdatePacket(__instance.planetId, entityId, info));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetFactory.WriteExtraInfoOnPrebuild))]
    public static void WriteExtraInfoOnPrebuild_Postfix(PlanetFactory __instance, int prebuildId, string info)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.Factories.IsIncomingRequest.Value)
        {
            return;
        }
        Multiplayer.Session.Network.SendPacketToLocalStar(
            new ExtraInfoUpdatePacket(__instance.planetId, -prebuildId, info));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetFactory.EnableEntityWarning))]
    public static void EnableEntityWarning_Postfix(PlanetFactory __instance, int entityId)
    {
        if (!Multiplayer.IsActive || entityId <= 0 || __instance.entityPool[entityId].id != entityId)
        {
            return;
        }
        if (Multiplayer.Session.LocalPlayer.IsClient)
        {
            //Because WarningSystem.NewWarningData is blocked on client, we give it a dummy warningId
            __instance.entityPool[entityId].warningId = 1;
        }
        Multiplayer.Session.Network.SendPacketToLocalStar(
            new EntityWarningSwitchPacket(__instance.planetId, entityId, true));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetFactory.DisableEntityWarning))]
    public static void DisableEntityWarning_Postfix(PlanetFactory __instance, int entityId)
    {
        if (Multiplayer.IsActive && entityId > 0 && __instance.entityPool[entityId].id == entityId)
        {
            Multiplayer.Session.Network.SendPacketToLocalStar(new EntityWarningSwitchPacket(__instance.planetId, entityId,
                false));
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetFactory.EntityFastTakeOut))]
    public static void EntityFastTakeOut_Postfix(PlanetFactory __instance, int entityId)
    {
        // belt, splitter, monitor, piler: handle by BeltFastTakeOut
        // storage: sync in StorageComponent.TakeItemFromGrid
        // powerNode, powerCon, powerAcc: no fill in interaction

        if (!Multiplayer.IsActive || entityId <= 0 || __instance.entityPool[entityId].id != entityId)
        {
            return;
        }
        var entityData = __instance.entityPool[entityId];

        if (entityData.dispenserId > 0)
        {
            var dispenserId = entityData.dispenserId;
            var dispenserPool = __instance.transport.dispenserPool;
            Multiplayer.Session.Network.SendPacketToLocalStar(new DispenserStorePacket(__instance.planetId,
                in dispenserPool[dispenserId]));
        }
        if (entityData.inserterId > 0)
        {
            var inserterId = entityData.inserterId;
            var inserterPool = __instance.factorySystem.inserterPool;
            Multiplayer.Session.Network.SendPacketToLocalStar(new InserterItemUpdatePacket(in inserterPool[inserterId],
                __instance.planetId));
        }
        if (entityData.minerId > 0)
        {
            var minerId = entityData.minerId;
            Multiplayer.Session.Network.SendPacketToLocalStar(new MinerStoragePickupPacket(minerId, __instance.planetId));
        }
        if (entityData.powerExcId > 0)
        {
            var powerExcId = entityData.powerExcId;
            var excPool = __instance.powerSystem.excPool;
            Multiplayer.Session.Network.SendPacketToLocalStar(new PowerExchangerStorageUpdatePacket(powerExcId,
                excPool[powerExcId].emptyCount, excPool[powerExcId].fullCount, __instance.planetId,
                excPool[powerExcId].fullInc));
        }
        if (entityData.powerGenId > 0)
        {
            var powerGenId = entityData.powerGenId;
            var genPool = __instance.powerSystem.genPool;
            Multiplayer.Session.Network.SendPacketToLocalStar(new PowerGeneratorFuelUpdatePacket(powerGenId,
                genPool[powerGenId].fuelId, genPool[powerGenId].fuelCount, genPool[powerGenId].fuelInc,
                __instance.planetId));
            if (genPool[powerGenId].productId > 0)
            {
                Multiplayer.Session.Network.SendPacketToLocalStar(
                    new PowerGeneratorProductUpdatePacket(in genPool[powerGenId], __instance.planetId));
            }
        }
        if (entityData.stationId > 0)
        {
            var stationId = entityData.stationId;
            var stationComponent = __instance.transport.stationPool[stationId];
            SendStationStorageBatch(__instance.planetId, stationComponent, __instance.gameData.history.logisticShipWarpDrive);
        }
        if (entityData.tankId <= 0)
        {
            return;
        }

        var tankId = entityData.tankId;
        var tankPool = __instance.factoryStorage.tankPool;
        Multiplayer.Session.Network.SendPacketToLocalStar(new TankStorageUpdatePacket(in tankPool[tankId],
            __instance.planetId));
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetFactory.EntityFastFillIn))]
    public static void EntityFastFillIn_Postfix(PlanetFactory __instance, int entityId, bool __runOriginal)
    {
        if (!__runOriginal) return;
        // belt, splitter, monitor, miner, fractionator, piler: handle by BeltFastFillIn
        // storage: sync in StorageComponent.AddItemStacked
        // inserter, powerNode, powerCon, powerAcc: no fill in interaction

        if (!Multiplayer.IsActive || entityId <= 0 || __instance.entityPool[entityId].id != entityId)
        {
            return;
        }
        var entityData = __instance.entityPool[entityId];

        if (entityData.tankId > 0)
        {
            var tankId = entityData.tankId;
            var tankPool = __instance.factoryStorage.tankPool;
            Multiplayer.Session.Network.SendPacketToLocalStar(new TankStorageUpdatePacket(in tankPool[tankId],
                __instance.planetId));
        }
        if (entityData.dispenserId > 0)
        {
            var dispenserId = entityData.dispenserId;
            var dispenserPool = __instance.transport.dispenserPool;
            var courierCount = dispenserPool[dispenserId].workCourierCount + dispenserPool[dispenserId].idleCourierCount;
            Multiplayer.Session.Network.SendPacketToLocalStar(new DispenserSettingPacket(__instance.planetId, dispenserId,
                EDispenserSettingEvent.SetCourierCount, courierCount));
        }
        if (entityData.stationId > 0)
        {
            var stationId = entityData.stationId;
            var stationComponent = __instance.transport.stationPool[stationId];
            SendStationStorageBatch(__instance.planetId, stationComponent, __instance.gameData.history.logisticShipWarpDrive);
        }
        if (entityData.powerGenId > 0)
        {
            var powerGenId = entityData.powerGenId;
            var genPool = __instance.powerSystem.genPool;
            Multiplayer.Session.Network.SendPacketToLocalStar(new PowerGeneratorFuelUpdatePacket(powerGenId,
                genPool[powerGenId].fuelId, genPool[powerGenId].fuelCount, genPool[powerGenId].fuelInc,
                __instance.planetId));
            if (genPool[powerGenId].gamma)
            {
                Multiplayer.Session.Network.SendPacketToLocalStar(new RayReceiverChangeLensPacket(powerGenId,
                    genPool[powerGenId], __instance.planetId));
            }
        }
        if (entityData.powerExcId > 0)
        {
            var powerExcId = entityData.powerExcId;
            var excPool = __instance.powerSystem.excPool;
            Multiplayer.Session.Network.SendPacketToLocalStar(new PowerExchangerStorageUpdatePacket(powerExcId,
                excPool[powerExcId].emptyCount, excPool[powerExcId].fullCount, __instance.planetId,
                excPool[powerExcId].fullInc));
        }
        if (entityData.spraycoaterId <= 0)
        {
            return;
        }
        var spraycoaterId = entityData.spraycoaterId;
        var spraycoaterPool = __instance.cargoTraffic.spraycoaterPool;
        Multiplayer.Session.Network.SendPacketToLocalStar(
            new SprayerStorageUpdatePacket(spraycoaterPool[spraycoaterId], __instance.planetId));
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetFactory.EntityAutoReplenishIfNeeded))]
    [HarmonyPatch(nameof(PlanetFactory.StationAutoReplenishIfNeeded))]
    public static bool EntityAutoReplenishIfNeeded_Prefix(PlanetFactory __instance, int entityId,
        ref (int, int, int, int) __state)
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }

        // Don't auto replenish if it is from other player's packet
        if (Multiplayer.Session.Factories.IsIncomingRequest.Value &&
            Multiplayer.Session.Factories.PacketAuthor != Multiplayer.Session.LocalPlayer.Id)
        {
            return false;
        }
        if (Multiplayer.Session.StationsUI.IsIncomingRequest.Value)
        {
            return false;
        }

        __state.Item1 = 1;
        ref var ptr = ref __instance.entityPool[entityId];
        if (ptr.dispenserId > 0)
        {
            var dispenserComponent = __instance.transport.dispenserPool[ptr.dispenserId];
            __state.Item2 = dispenserComponent.idleCourierCount;
        }
        if (ptr.stationId <= 0)
        {
            return true;
        }
        var stationComponent = __instance.transport.stationPool[ptr.stationId];
        __state.Item3 = stationComponent.idleDroneCount;
        __state.Item4 = stationComponent.idleShipCount;
        return true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetFactory.EntityAutoReplenishIfNeeded))]
    [HarmonyPatch(nameof(PlanetFactory.StationAutoReplenishIfNeeded))]
    public static void EntityAutoReplenishIfNeeded_Postfix(PlanetFactory __instance, int entityId,
        ref (int, int, int, int) __state)
    {
        if (__state.Item1 != 1)
        {
            return;
        }

        ref var ptr = ref __instance.entityPool[entityId];
        if (ptr.dispenserId > 0)
        {
            var dispenserComponent = __instance.transport.dispenserPool[ptr.dispenserId];
            if (__state.Item2 != dispenserComponent.idleCourierCount)
            {
                Multiplayer.Session.Network.SendPacketToLocalStar(
                    new DispenserSettingPacket(__instance.planetId,
                        ptr.dispenserId,
                        EDispenserSettingEvent.SetCourierCount,
                        dispenserComponent.workCourierCount + dispenserComponent.idleCourierCount));
            }
        }
        if (ptr.stationId <= 0)
        {
            return;
        }
        var stationComponent = __instance.transport.stationPool[ptr.stationId];
        if (__state.Item3 != stationComponent.idleDroneCount)
        {
            Multiplayer.Session.Network.SendPacket(
                new StationUI(__instance.planetId,
                    stationComponent.id,
                    stationComponent.gid,
                    StationUI.EUISettings.SetDroneCount,
                    stationComponent.idleDroneCount + stationComponent.workDroneCount));

            if (Multiplayer.Session.LocalPlayer.IsClient)
            {
                // Revert drone count until host verify
                stationComponent.idleDroneCount = __state.Item3;
            }
        }

        if (__state.Item4 == stationComponent.idleShipCount)
        {
            return;
        }
        Multiplayer.Session.Network.SendPacket(
            new StationUI(__instance.planetId,
                stationComponent.id,
                stationComponent.gid,
                StationUI.EUISettings.SetShipCount,
                stationComponent.idleShipCount + stationComponent.workShipCount));

        if (Multiplayer.Session.LocalPlayer.IsClient)
        {
            // Revert drone count until host verify
            stationComponent.idleShipCount = __state.Item4;
        }
    }

    #region Combat

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetFactory.KillEnemyFinally))]
    public static bool KillEnemyFinally_Prefix(int enemyId)
    {
        if (!Multiplayer.IsActive || enemyId <= 0)
        {
            return true;
        }
        if (Multiplayer.Session.IsServer)
        {
            // Host runs the death rule once; the replica lifecycle carries the removal.
            return true;
        }
        if (Multiplayer.Session.Combat.IsIncomingRequest.Value)
        {
            return true;
        }

        // Keep the enemy targetable until the host confirms its death.
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetFactory.RemoveEnemyFinal))]
    public static bool RemoveEnemyFinal_Prefix()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer)
        {
            return true;
        }
        // Only execute RemoveEnemyFinal when server approve on client
        // Factories.IsIncomingRequest is for RemoveBasePit called on putting down Geothermal Power Station
        return Multiplayer.Session.Combat.IsIncomingRequest.Value || Multiplayer.Session.Factories.IsIncomingRequest.Value;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetFactory.KillEntityFinally))]
    public static bool KillEntityFinally_Prefix(int objId)
    {
        if (!Multiplayer.IsActive || objId <= 0)
        {
            return true;
        }

        if (Multiplayer.Session.IsClient) // Let server decide when to kill entity
        {
            return Multiplayer.Session.Factories.IsIncomingRequest.Value;
        }

        // Host death transactions publish removals through the replica lifecycle.
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetFactory.ReconstructTargetFinally))]
    public static void ReconstructTargetFinally_Prefix(PlanetFactory __instance, int prebuildId)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.Factories.IsIncomingRequest.Value) return;

        // Broadcast the change of isDestroyed state to other players
        var packet = new PrebuildReconstructPacket(__instance.planetId, prebuildId);
        if (Multiplayer.Session.IsServer)
        {
            var starId = __instance.planet.star.id;
            Multiplayer.Session.Network.SendPacketToStar(packet, starId);
        }
        else
        {
            Multiplayer.Session.Network.SendPacket(packet);
        }
    }

    #endregion

    /// <summary>Reports a station's storage and drone, ship and warper counts in one packet.</summary>
    /// <remarks>Fast-take-out and fast-fill-in read the same state and share this helper.</remarks>
    private static void SendStationStorageBatch(int planetId, StationComponent stationComponent, bool warpDriveResearched)
    {
        var storage = stationComponent.storage;
        var storageIdx = new List<int>(storage.Length);
        var itemCount = new List<int>(storage.Length);
        var itemInc = new List<int>(storage.Length);
        for (var i = 0; i < storage.Length; i++)
        {
            if (storage[i].itemId <= 0)
            {
                continue;
            }
            storageIdx.Add(i);
            itemCount.Add(storage[i].count);
            itemInc.Add(storage[i].inc);
        }

        var settingIndex = new List<byte>(3);
        var settingValue = new List<int>(3);
        var isCollector = stationComponent.isCollector || stationComponent.isVeinCollector;
        if (!isCollector)
        {
            settingIndex.Add((byte)StationUI.EUISettings.SetDroneCount);
            settingValue.Add(stationComponent.idleDroneCount + stationComponent.workDroneCount);
        }
        if (stationComponent.isStellar && !isCollector)
        {
            settingIndex.Add((byte)StationUI.EUISettings.SetShipCount);
            settingValue.Add(stationComponent.idleShipCount + stationComponent.workShipCount);
            if (warpDriveResearched)
            {
                settingIndex.Add((byte)StationUI.EUISettings.SetWarperCount);
                settingValue.Add(stationComponent.warperCount);
            }
        }

        var packet = new StationStorageUIBatchPacket(planetId, stationComponent.id, stationComponent.gid,
            storageIdx.ToArray(), itemCount.ToArray(), itemInc.ToArray(),
            settingIndex.ToArray(), settingValue.ToArray());
        if (Multiplayer.Session.IsServer)
        {
            var starId = GameMain.galaxy.PlanetById(planetId).star.id;
            Multiplayer.Session.Network.SendPacketToStar(packet, starId);
        }
        else
        {
            Multiplayer.Session.Network.SendPacket(packet);
        }
    }

}
