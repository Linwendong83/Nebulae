using System.Collections.Generic;
using NebulaeModel.DataStructures;
using NebulaeModel.Authority;
using NebulaeModel.Networking;
using System.Linq;
using NebulaeModel.Packets.Factory;

namespace NebulaeWorld.Factory;

internal sealed partial class BuildDispatchManager
{
    private readonly Dictionary<long, PrebuildItemRequiredUpdate> pendingSupply = new();
    private readonly Dictionary<ushort, long> supplySequences = new();
    private long nextSupply;
    private readonly Dictionary<(PlanetFactory Factory, int Storage), StorageComponent> suppliedStores = new();
    private readonly Dictionary<(PlanetFactory Factory, int Storage), long> storeTicks = new();
    private readonly Dictionary<(int Planet, int Storage), ConstructionStoragePacket> pendingStores = new();
    public bool SupplyPending => pendingSupply.Count != 0;

    public void StorageSupplied(PlanetFactory factory, StorageComponent storage) => suppliedStores[(factory, storage.id)] = storage;
    public void PublishConstructionStorage(PlanetFactory factory, long tick)
    {
        foreach (var pair in suppliedStores.Where(s => s.Key.Factory == factory).ToArray())
        {
            suppliedStores.Remove(pair.Key);
            var storage = pair.Value;
            if (Multiplayer.Session.AuthorityRuntime.FactoryCombatHost?.TryGetEntityKey(factory, storage.entityId, out var key) != true) continue;
            using var writer = new BinaryUtils.Writer(); storage.Export(writer.BinaryWriter);
            Multiplayer.Session.Network.SendPacketToStar(new ConstructionStoragePacket
            {
                PlanetId = factory.planetId,
                StorageId = storage.id,
                EntityId = storage.entityId,
                Generation = key.Generation,
                Tick = tick,
                State = writer.CloseAndGetBytes()
            }, factory.planet.star.id);
        }
    }
    public void ReceiveConstructionStorage(ConstructionStoragePacket packet)
    {
        if (packet.State == null || packet.State.Length > 1024 * 1024 || packet.StorageId <= 0) return;
        var pendingKey = (packet.PlanetId, packet.StorageId);
        if (!pendingStores.TryGetValue(pendingKey, out var previous) || packet.Tick > previous.Tick)
            pendingStores[pendingKey] = new ConstructionStoragePacket
            {
                PlanetId = packet.PlanetId,
                StorageId = packet.StorageId,
                EntityId = packet.EntityId,
                Generation = packet.Generation,
                Tick = packet.Tick,
                State = (byte[])packet.State.Clone()
            };
        FlushConstructionStorage();
    }
    private void FlushConstructionStorage()
    {
        foreach (var pair in pendingStores.ToArray())
            if (TryApplyConstructionStorage(pair.Value)) pendingStores.Remove(pair.Key);
    }
    private bool TryApplyConstructionStorage(ConstructionStoragePacket packet)
    {
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        if (factory == null || packet.StorageId >= factory.factoryStorage.storageCursor ||
            Multiplayer.Session.AuthorityRuntime.WorldReplica?.TryFindKey(PoolKind.Entity, packet.PlanetId, packet.EntityId, out var key) != true) return false;
        if (key.Generation > packet.Generation) return true;
        if (key.Generation != packet.Generation) return false;
        var storeKey = (factory, packet.StorageId);
        if (storeTicks.TryGetValue(storeKey, out var tick) && tick >= packet.Tick) return true;
        var storage = factory.factoryStorage.storagePool[packet.StorageId];
        if (storage?.entityId != packet.EntityId) return false;
        using var reader = new BinaryUtils.Reader(packet.State, 2);
        storage.Import(reader.BinaryReader); reader.RequireEnd(); storeTicks[storeKey] = packet.Tick;
        return true;
    }

    public void ReportSupply(PlanetFactory factory, PrebuildData site, int count, int inc)
    {
        var packet = new PrebuildItemRequiredUpdate(factory.planetId, site.id, count)
        {
            Instance = Instances.Get(factory, site.id),
            ItemId = site.protoId,
            Inc = inc,
            Supplier = Multiplayer.Session.LocalPlayer.Id,
            Sequence = ++nextSupply
        };
        if (Multiplayer.Session.IsServer)
        {
            packet.Committed = packet.Accepted = true;
            Multiplayer.Session.Network.SendPacketToStar(packet, factory.planet.star.id);
        }
        else
        {
            // Creation and its instance identity can straddle a client frame. Retry supply after
            // the identity arrives instead of creating an unanswerable reservation.
            if (packet.Instance <= 0)
            {
                factory.prebuildPool[site.id].itemRequired = count;
                GameMain.mainPlayer.TryAddItemToPackage(packet.ItemId, count, inc, true);
                return;
            }
            pendingSupply[packet.Sequence] = packet;
            Multiplayer.Session.Network.SendPacket(packet);
        }
    }
    public bool AcceptSupply(ushort player, PrebuildItemRequiredUpdate packet)
    {
        if (packet.Committed || packet.Supplier != player || packet.Sequence <= 0 || packet.Instance <= 0 ||
            packet.ItemCount <= 0 || packet.ItemCount > 1000 || packet.Inc < 0 || packet.Inc > (long)packet.ItemCount * 10 ||
            !ItemProto.constructableIdHash.Contains(packet.ItemId)) return false;
        if (supplySequences.TryGetValue(player, out var sequence) && packet.Sequence <= sequence) return false;
        supplySequences[player] = packet.Sequence;
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        packet.Committed = true;
        var supplier = Multiplayer.Session.Server.Players.Get(player);
        var sameInstance = factory != null && Instances.Get(factory, packet.PrebuildId) == packet.Instance;
        packet.Required = sameInstance ? factory.prebuildPool[packet.PrebuildId].itemRequired : -1;
        var range = Multiplayer.Session.CombatAuthority?.MechaFor(player)?.buildArea ??
            (remoteBuilders.TryGetValue(player, out var capability) ? capability.Range : GameMain.mainPlayer.mecha.buildArea);
        // Material was already reserved by native PlaceItems. A later movement/settings report
        // must not decide whether that reservation can commit.
        packet.Accepted = sameInstance &&
            supplier?.Data.LocalPlanetId == packet.PlanetId &&
            (factory.prebuildPool[packet.PrebuildId].pos - supplier.Data.LocalPlanetPosition.ToVector3()).sqrMagnitude <= range * range &&
            factory.prebuildPool[packet.PrebuildId].protoId == packet.ItemId &&
            factory.prebuildPool[packet.PrebuildId].itemRequired == packet.ItemCount && !factory.prebuildPool[packet.PrebuildId].isDestroyed;
        if (packet.Accepted) { packet.Required = 0; ApplySupply(packet); }
        return true;
    }
    public void ApplySupply(PrebuildItemRequiredUpdate packet)
    {
        if (!packet.Committed) return;
        var ownReservation = false;
        if (!Multiplayer.Session.IsServer && packet.Supplier == Multiplayer.Session.LocalPlayer.Id &&
            pendingSupply.TryGetValue(packet.Sequence, out var pending) && pending.Instance == packet.Instance &&
            pending.PlanetId == packet.PlanetId && pending.PrebuildId == packet.PrebuildId)
        {
            ownReservation = true;
            pendingSupply.Remove(packet.Sequence);
            if (!packet.Accepted)
                GameMain.mainPlayer.TryAddItemToPackage(pending.ItemId, pending.ItemCount, pending.Inc, true, 0, false);
        }
        if (!packet.Accepted && (!ownReservation || packet.Required <= 0)) return;
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        if (factory == null || Instances.Get(factory, packet.PrebuildId) != packet.Instance) return;
        ref var site = ref factory.prebuildPool[packet.PrebuildId];
        var required = packet.Accepted ? 0 : packet.Required;
        if (site.itemRequired == required) return;
        site.itemRequired = required;
        if (required > 0)
        {
            ResetSite(factory, site.id);
            factory.constructionSystem.RemoveBuildTargetFromModules(site.id);
        }
        factory.RefreshPrebuildWarning(site.id); factory.NotifyPrebuildChange(site.id, 3);
        if (factory.planet.factoryLoaded || factory.planet.factingCompletedStage >= 3) factory.AlterPrebuildModelState(site.id);
        if (required == 0) factory.constructionSystem.AddBuildTargetToModules(site.id, ref site.pos);
    }
}
