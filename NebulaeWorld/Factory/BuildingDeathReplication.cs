using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Factory;
using NebulaeWorld.Authority;

namespace NebulaeWorld.Factory;

internal static class BuildingDeathReplication
{
    private sealed class State
    {
        public long Sent, Applied;
        public readonly Dictionary<int, BuildingDeathPacket> Pending = new();
        public readonly Dictionary<(PoolKind Kind, int Id), long> Retired = new();
    }
    private static readonly ConditionalWeakTable<PlanetFactory, State> states = new();
    private static readonly AccessTools.FieldRef<PlanetFactory, int> prebuildRecycleCursor =
        AccessTools.FieldRefAccess<PlanetFactory, int>("prebuildRecycleCursor");
    private static readonly AccessTools.FieldRef<PlanetFactory, int[]> prebuildRecycle =
        AccessTools.FieldRefAccess<PlanetFactory, int[]>("prebuildRecycle");

    public static bool IsRetired(PlanetFactory factory, in ObjectKey key) => factory != null &&
        states.TryGetValue(factory, out var state) && state.Retired.TryGetValue((key.Kind, key.NativeId), out var generation) &&
        key.Generation <= generation;

    public static void Capture(PlanetFactory factory, int id)
    {
        var proto = FactoryManager.GetObjectProtoId(factory, id);
        if (proto <= 0) return;
        var packet = new BuildingDeathPacket
        {
            PlanetId = factory.planetId,
            ObjectId = id,
            ProtoId = proto,
            Wreckage = BuildingDeathVisuals.Capture(factory, id)
        };
        var runtime = Multiplayer.Session.AuthorityRuntime;
        if (id > 0)
        {
            if (runtime?.FactoryCombatHost?.TryGetEntityKey(factory, id, out var key) == true)
                packet.EntityGeneration = key.Generation;
            packet.TurretGeneration = Multiplayer.Session.Turrets.HostView?.Generation(factory.planetId, id) ?? 0;
        }
        states.GetOrCreateValue(factory).Pending[id] = packet;
    }

    public static void Publish(PlanetFactory factory, int prebuild, int oldObject)
    {
        var state = states.GetOrCreateValue(factory);
        if (!state.Pending.TryGetValue(oldObject, out var packet)) return;
        state.Pending.Remove(oldObject);
        packet.Sequence = ++state.Sent;
        packet.Prebuild = prebuild > 0 ? DeathPrebuildCodec.Encode(factory.prebuildPool[prebuild]) : Array.Empty<byte>();
        packet.Connections = new int[16];
        packet.ExtraInfo = prebuild > 0 ? factory.ReadExtraInfoOnPrebuild(prebuild) ?? "" : "";
        if (prebuild > 0)
        {
            Array.Copy(factory.prebuildConnPool, prebuild * 16, packet.Connections, 0, 16);
            packet.PrebuildGeneration = Multiplayer.Session.BuildDispatch.Instances.Get(factory, prebuild);
        }
        Multiplayer.Session.Network.SendPacketToStar(packet, factory.planet.star.id);
    }

    public static void Apply(PlanetFactory factory, BuildingDeathPacket packet)
    {
        if (packet.PlanetId != factory.planetId || packet.Sequence <= 0 || packet.ObjectId == 0 ||
            packet.ObjectId == int.MinValue || packet.Connections?.Length != 16)
            throw new InvalidDataException("Invalid building death operation.");
        var state = states.GetOrCreateValue(factory);
        if (packet.Sequence <= state.Applied) return;
        var prebuild = DeathPrebuildCodec.Decode(packet.Prebuild);
        var wreckage = BuildingDeathVisuals.Decode(packet.Wreckage);
        if (FactoryManager.GetObjectProtoId(factory, packet.ObjectId) != packet.ProtoId)
            throw new InvalidOperationException($"Building death identity mismatch planet={packet.PlanetId} object={packet.ObjectId}; reconnect to reload the factory.");
        if (prebuild.id > 0 && (packet.PrebuildGeneration <= 0 ||
            prebuild.id != (prebuildRecycleCursor(factory) > 0 ? prebuildRecycle(factory)[prebuildRecycleCursor(factory) - 1] : factory.prebuildCursor)))
            throw new InvalidOperationException($"Building death prebuild allocator mismatch planet={packet.PlanetId}; reconnect to reload the factory.");

        using var incoming = Multiplayer.Session.Factories.IsIncomingRequest.On();
        using var operation = new FactoryOperationScope();
        Multiplayer.Session.Factories.EventFactory = factory;
        Multiplayer.Session.Factories.TargetPlanet = factory.planetId;
        if (GameMain.gpuiManager != null) GameMain.gpuiManager.specifyPlanet = factory.planet;

        factory.BeforeKillObject(packet.ObjectId);
        BuildingDeathVisuals.Play(factory, packet.ObjectId, wreckage);
        // Native death allocates the replacement before freeing the old slot (including
        // stacked prebuilds). Preserve that order so the subsequent allocator agrees.
        if (prebuild.id > 0)
        {
            var id = factory.AddPrebuildDataWithComponents(prebuild);
            if (id != prebuild.id) throw new InvalidOperationException("Destroyed prebuild allocation failed.");
            Multiplayer.Session.BuildDispatch.Instances.Apply(new PrebuildInstancePacket
            { PlanetId = factory.planetId, PrebuildId = id, Generation = packet.PrebuildGeneration });
        }
        var splitterPriorities = new List<(int Id, int Slot, int Filter)>();
        var oldBelt = packet.ObjectId > 0 ? factory.entityPool[packet.ObjectId].beltId : 0;
        for (var slot = 0; slot < 16; slot++)
        {
            factory.ReadObjectConn(packet.ObjectId, slot, out var output, out var other, out var otherSlot);
            if (prebuild.id > 0 && oldBelt > 0 && other > 0 && factory.entityPool[other].splitterId > 0)
            {
                var splitter = factory.entityPool[other].splitterId;
                ref var s = ref factory.cargoTraffic.splitterPool[splitter];
                var priority = slot == 0 && output && s.inPriority && s.input0 == oldBelt;
                var filter = slot == 1 && !output && s.output0 == oldBelt ? s.outFilter : 0;
                if (priority || filter > 0) splitterPriorities.Add((splitter, otherSlot, filter == 0 ? s.outFilterPreset : filter));
            }
            if (other > 0 && packet.ObjectId > 0) factory.ApplyEntityDisconnection(other, packet.ObjectId, otherSlot, slot);
            if (other != 0) factory.ClearObjectConn(other, otherSlot);
            factory.ClearObjectConn(packet.ObjectId, slot);
        }
        if (prebuild.id > 0)
        {
            Array.Copy(packet.Connections, 0, factory.prebuildConnPool, prebuild.id * 16, 16);
            for (var slot = 0; slot < 16; slot++)
            {
                factory.ReadObjectConn(-prebuild.id, slot, out var output, out var other, out var otherSlot);
                if (other != 0) factory.WriteObjectConn(other, otherSlot, !output, -prebuild.id, slot);
            }
            factory.WriteExtraInfoOnPrebuild(prebuild.id, packet.ExtraInfo ?? "");
        }
        foreach (var priority in splitterPriorities)
            factory.cargoTraffic.splitterPool[priority.Id].SetPriority(priority.Slot, true, priority.Filter);
        if (factory.planet.factoryLoaded && GameMain.mainPlayer?.controller != null)
        {
            GameMain.mainPlayer.controller.actionBuild.NotifyKilled(packet.ObjectId, -prebuild.id);
            GameMain.mainPlayer.controller.actionInspect.NotifyObjectKill(packet.ObjectId > 0 ? EObjectType.None : EObjectType.Prebuild, Math.Abs(packet.ObjectId));
        }
        if (packet.ObjectId > 0)
        {
            // Release binding-owned stats before native removal. Later lifecycle packets
            // must neither release them twice nor write stale HP into a reused entity slot.
            Retire(factory, state, PoolKind.Entity, packet.ObjectId, packet.EntityGeneration);
            Retire(factory, state, PoolKind.Turret, packet.ObjectId, packet.TurretGeneration);
            factory.RemoveEntityWithComponents(packet.ObjectId, false);
        }
        else
        {
            factory.prebuildPool[-packet.ObjectId].ruinId = 0; // The new prebuild retains the host ruin.
            factory.RemovePrebuildWithComponents(-packet.ObjectId);
        }
        // Ruins/drop rewards are independently server-owned. Replaying KillEntityFinally
        // here would create duplicate ruins and repeat resource side effects.
        factory.OnKillObject(prebuild.id, packet.ObjectId);
        state.Applied = packet.Sequence;
    }

    private static void Retire(PlanetFactory factory, State state, PoolKind kind, int id, long generation)
    {
        if (generation <= 0) return;
        var runtime = Multiplayer.Session.AuthorityRuntime;
        var scope = new ScopeKey(kind, factory.planetId);
        var key = ObjectKey.Create(runtime.Identity.Epoch, kind, factory.planetId, id, generation);
        runtime.ApplyContext.Run(new ApplyScope(scope), () => runtime.WorldReplica.MirrorObserver.OnMemberRemoved(scope, key));
        state.Retired[(kind, id)] = generation;
    }
}
