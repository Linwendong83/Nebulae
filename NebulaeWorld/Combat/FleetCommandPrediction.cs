using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using UnityEngine;

namespace NebulaeWorld.Combat;

/// <summary>Immediate fleet command pose and render-only launch previews. No craft or stock allocation.</summary>
internal sealed class FleetCommandPrediction : IDisposable
{
    private sealed class Preview
    {
        public int Port, Model, Instance;
        public Vector3 Offset;
    }
    private sealed class Command
    {
        public long Sequence, Tick, AppliedTick;
        public ulong Connection;
        public CombatModuleComponent Module;
        public int Index, Planet, Star, FleetId;
        public bool Recall, Accepted, BeforeCommand, NewerOrder;
        public bool? CommandMode;
        public FleetComponent Before;
        public GPUInstancingManager GroundModels;
        public SSGPUInstancingManager SpaceModels;
        public readonly List<Preview> Models = new();
    }
    private readonly Dictionary<long, Command> pending = new();
    internal sealed class Order
    {
        public CombatModuleComponent Module;
        public int Index, Planet, Star;
        public ulong Connection;
        public long Sequence, Tick, AppliedTick;
        public bool Accepted;
        public FleetComponent Before, After;
    }
    private readonly Dictionary<(bool Space, int Index), Order> orders = new();
    private readonly ConcurrentQueue<(ulong Connection, long Sequence, CommandResultCode Code, long Tick)> results = new();
    public int PendingCount => pending.Count + orders.Count;
    public int PreviewCount => pending.Values.Sum(x => x.Models.Count);
    public bool HasMode(CombatModuleComponent module, int index, bool mode) => pending.Values.Any(c =>
        c.Module.isSpace == module.isSpace && c.Index == index && !c.Recall && (c.CommandMode ?? true) == mode);
    public void PreserveOrder(CombatModuleComponent module, int index)
    {
        foreach (var c in pending.Values)
            if (c.Module.isSpace == module.isSpace && c.Index == index) c.NewerOrder = true;
    }
    public Order CaptureOrder(CombatModuleComponent module, int index, long sequence, in FleetComponent before)
    {
        PreserveOrder(module, index);
        var key = (module.isSpace, index);
        var rollback = orders.TryGetValue(key, out var older) ? older.Before : before;
        var order = new Order
        {
            Module = module,
            Index = index,
            Sequence = sequence,
            Before = rollback,
            Connection = Multiplayer.Session.Authority.Connection.Value,
            Tick = GameMain.gameTick,
            Planet = GameMain.localPlanet?.id ?? 0,
            Star = GameMain.localStar?.id ?? 0
        };
        orders[key] = order;
        return order;
    }
    public void Begin(CombatModuleComponent module, int index, bool recall, long sequence, bool? commandMode = null)
    {
        if (index < 0 || index >= module.fleetCount) return;
        orders.Remove((module.isSpace, index));
        foreach (var old in pending.Values.Where(x => x.Module.isSpace == module.isSpace && x.Index == index).ToArray()) Remove(old, false);
        var command = new Command
        {
            Sequence = sequence,
            Tick = GameMain.gameTick,
            Module = module,
            Index = index,
            Planet = GameMain.localPlanet?.id ?? 0,
            Star = GameMain.localStar?.id ?? 0,
            Recall = recall,
            CommandMode = commandMode,
            BeforeCommand = module.moduleFleets[index].inCommand,
            Connection = Multiplayer.Session.Authority.Connection.Value,
            FleetId = module.moduleFleets[index].fleetId
        };
        pending[sequence] = command;
        module.moduleFleets[index].inCommand = commandMode ?? (recall ? command.BeforeCommand : true);
        var pool = Pool(command);
        if (command.FleetId > 0 && pool != null && command.FleetId < pool.cursor && pool.buffer[command.FleetId].id == command.FleetId)
        {
            command.Before = pool.buffer[command.FleetId];
            Apply(command, pool);
        }
        if (!recall && commandMode == null) CreatePreview(command);
    }
    private static DataPool<FleetComponent> Pool(Command c) => c.Module.isSpace ? GameMain.spaceSector?.combatSpaceSystem?.fleets :
        GameMain.galaxy?.PlanetById(c.Module.moduleFleets[c.Index].fleetAstroId > 0 ? c.Module.moduleFleets[c.Index].fleetAstroId : c.Planet)?.factory?.combatGroundSystem?.fleets;
    private static void Apply(Command c, DataPool<FleetComponent> pool)
    {
        c.Module.moduleFleets[c.Index].inCommand = c.CommandMode ?? (c.Recall ? c.BeforeCommand : true);
        var id = c.Module.moduleFleets[c.Index].fleetId;
        if (id <= 0 || pool == null || id >= pool.cursor || pool.buffer[id].id != id) return;
        if (c.NewerOrder) return;
        if (c.CommandMode.HasValue) FleetCommandMode.Apply(c.Module, c.Index, c.CommandMode.Value);
        else if (c.Recall) pool.buffer[id].dispatch = false;
        else pool.buffer[id].CancelOrder();
    }
    private static void CreatePreview(Command c)
    {
        var player = GameMain.mainPlayer;
        if (!player.mecha.CheckLaunchFleetCondition(c.Module.isSpace) || !c.Module.moduleEnabled || !c.Module.moduleFleets[c.Index].fleetEnabled) return;
        c.GroundModels = c.Module.isSpace ? null : GameMain.localPlanet?.factory?.planet.factoryModel?.gpuiManager;
        c.SpaceModels = c.Module.isSpace ? GameMain.spaceSector?.model?.gpuiManager : null;
        if (c.GroundModels == null && c.SpaceModels == null) return;
        var fighters = c.Module.moduleFleets[c.Index].fighters;
        for (var i = 0; i < fighters.Length; i++)
        {
            var f = fighters[i]; var item = LDB.items.Select(f.itemId);
            if (f.count <= 0 || f.craftId > 0 || item == null) continue;
            var model = item.ModelIndex;
            var preview = new Preview { Port = i, Model = model, Offset = c.Module.moduleFleetPoses[c.Index].position + f.formDesc.formPos };
            preview.Instance = c.Module.isSpace ? c.SpaceModels.AddModel(model, 0, c.Star * 100, VectorLF3.zero, Quaternion.identity) :
                c.GroundModels.AddModel(model, 0, player.position, (GameMain.localPlanet == null ? player.uRotation : Quaternion.Inverse(GameMain.localPlanet.runtimeRotation) * player.uRotation));
            c.Models.Add(preview);
        }
        UpdatePreview(c);
    }
    private static void UpdatePreview(Command c)
    {
        var player = GameMain.mainPlayer;
        var progress = Mathf.Clamp01((GameMain.gameTick - c.Tick + 1) / 20f);
        var localRotation = (GameMain.localPlanet == null ? player.uRotation : Quaternion.Inverse(GameMain.localPlanet.runtimeRotation) * player.uRotation);
        foreach (var model in c.Models.ToArray())
        {
            var fighters = c.Module.moduleFleets[c.Index].fighters;
            object world = c.Module.isSpace ? GameMain.spaceSector : GameMain.localPlanet?.factory;
            if (model.Port >= fighters.Length || c.Accepted && fighters[model.Port].count <= 0 || c.Accepted && world != null && NativeCraftSimulation.AppliedTick(world, fighters[model.Port].craftId) >= 0 &&
                NativeCraftSimulation.OwnerMecha(c.Module.isSpace, c.Planet, fighters[model.Port].craftId) == player.mecha)
            { RemoveModel(c, model); c.Models.Remove(model); continue; }
            var offset = model.Offset * progress;
            if (c.Module.isSpace)
            {
                var scale = FleetPose.Scale(GameMain.spaceSector, ref player.uPosition, player);
                var u = player.uPosition + (VectorLF3)(player.uRotation * (offset * scale));
                GameMain.spaceSector.InverseTransformToAstro(c.Star * 100, u, player.uRotation, out var pos, out var rot);
                var renderer = c.SpaceModels.objectRenderers[model.Model];
                ref var instance = ref renderer.instPool[model.Instance];
                instance.posx = (float)pos.x; instance.posy = (float)pos.y; instance.posz = (float)pos.z;
                instance.rotx = rot.x; instance.roty = rot.y; instance.rotz = rot.z; instance.rotw = rot.w;
                GameMain.spaceSector.model.craftDirty = true;
            }
            else c.GroundModels.AlterModel(model.Model, model.Instance, 0, player.position + localRotation * (offset + Vector3.up * (10 * progress)), localRotation);
        }
    }
    private static void RemoveModel(Command c, Preview p)
    {
        if (c.SpaceModels?.objectRenderers != null && p.Model < c.SpaceModels.objectRenderers.Length && c.SpaceModels.objectRenderers[p.Model]?.instPool != null)
            c.SpaceModels.RemoveModel(p.Model, p.Instance);
        else if (c.GroundModels?.objectRenderers != null && p.Model < c.GroundModels.objectRenderers.Length && c.GroundModels.objectRenderers[p.Model]?.instPool != null)
            c.GroundModels.RemoveModel(p.Model, p.Instance);
    }
    public void Receive(AuthorityCommandResultPacket packet) => results.Enqueue((packet.ConnectionEpoch, packet.Sequence, (CommandResultCode)packet.ResultCode, packet.AppliedHostTick));
    public void Tick()
    {
        while (results.TryDequeue(out var result))
        {
            if (pending.TryGetValue(result.Sequence, out var c) && c.Connection == result.Connection)
            {
                if (result.Code == CommandResultCode.Applied) { c.Accepted = true; c.AppliedTick = result.Tick; }
                else if (result.Code != CommandResultCode.RejectedDuplicate) Remove(c, true);
            }
            foreach (var pair in orders.ToArray())
                if (pair.Value.Sequence == result.Sequence && pair.Value.Connection == result.Connection)
                {
                    var order = pair.Value;
                    if (result.Code == CommandResultCode.Applied) { order.Accepted = true; order.AppliedTick = result.Tick; }
                    else if (result.Code != CommandResultCode.RejectedDuplicate)
                    { RestoreOrder(order); orders.Remove(pair.Key); }
                }
        }
        foreach (var c in pending.Values.ToArray())
        {
            if (c.Connection != Multiplayer.Session.Authority.Connection.Value || c.Star != (GameMain.localStar?.id ?? 0) ||
                !c.Module.isSpace && c.Planet != (GameMain.localPlanet?.id ?? 0) || !GameMain.mainPlayer.isAlive || GameMain.gameTick - c.Tick > 600)
            { Remove(c, true); continue; }
            // Each combat snapshot replaces the module object. A command belongs to the player's
            // module slot, not the old deserialized object (which would never receive craft IDs).
            c.Module = c.Module.isSpace ? GameMain.mainPlayer.mecha.spaceCombatModule : GameMain.mainPlayer.mecha.groundCombatModule;
            if (c.Index >= c.Module.fleetCount || c.Index >= c.Module.moduleFleets.Length) { Remove(c, false); continue; }
            if (!c.Module.moduleEnabled || !c.Module.moduleFleets[c.Index].fleetEnabled)
            {
                c.Module.moduleFleets[c.Index].inCommand = false;
                Remove(c, false);
                continue;
            }
            var pool = Pool(c); var fleetId = c.Module.moduleFleets[c.Index].fleetId;
            object world = c.Module.isSpace ? GameMain.spaceSector : GameMain.galaxy?.PlanetById(c.Planet)?.factory;
            var confirmed = c.Accepted && (fleetId == 0 && (c.Recall || c.CommandMode.HasValue &&
                Multiplayer.Session.CombatAuthority.LastAppliedCommand >= c.Sequence) || pool != null && fleetId > 0 && fleetId < pool.cursor &&
                NativeCraftSimulation.AppliedTick(world, pool.buffer[fleetId].craftId) >= c.AppliedTick);
            if (!confirmed) Apply(c, pool);
            UpdatePreview(c);
            if (confirmed && c.Models.Count == 0 && Multiplayer.Session.CombatAuthority.LastAppliedCommand >= c.Sequence) Remove(c, false);
        }
        foreach (var pair in orders.ToArray())
        {
            var order = pair.Value;
            if (order.Connection != Multiplayer.Session.Authority.Connection.Value || order.Star != (GameMain.localStar?.id ?? 0) ||
                !pair.Key.Space && order.Planet != (GameMain.localPlanet?.id ?? 0) || !GameMain.mainPlayer.isAlive || GameMain.gameTick - order.Tick > 600)
            { orders.Remove(pair.Key); continue; }
            var module = pair.Key.Space ? GameMain.mainPlayer.mecha.spaceCombatModule : GameMain.mainPlayer.mecha.groundCombatModule;
            if (order.Index >= module.fleetCount || order.Index >= module.moduleFleets.Length ||
                !module.moduleEnabled || !module.moduleFleets[order.Index].fleetEnabled)
            { orders.Remove(pair.Key); continue; }
            if (!FleetCommandMode.TryGetPool(module, order.Index, out var pool)) continue;
            ref var fleet = ref pool.buffer[module.moduleFleets[order.Index].fleetId];
            if (fleet.id != order.Before.id || fleet.craftId != order.Before.craftId) { orders.Remove(pair.Key); continue; }
            object world = pair.Key.Space ? GameMain.spaceSector : GameMain.galaxy?.PlanetById(order.Planet)?.factory;
            if (order.Accepted && NativeCraftSimulation.AppliedTick(world, fleet.craftId) >= order.AppliedTick &&
                Multiplayer.Session.CombatAuthority.LastAppliedCommand >= order.Sequence)
            { orders.Remove(pair.Key); continue; }
            CopyOrder(ref fleet, order.After);
        }
    }
    private static void CopyOrder(ref FleetComponent fleet, in FleetComponent source)
    { fleet.dispatch = source.dispatch; fleet.target = source.target; fleet.targetPos = source.targetPos; }
    private static void RestoreOrder(Order order)
    {
        var mecha = GameMain.mainPlayer?.mecha;
        if (mecha == null || order.Connection != Multiplayer.Session.Authority.Connection.Value) return;
        var module = order.Module.isSpace ? mecha.spaceCombatModule : mecha.groundCombatModule;
        if (!FleetCommandMode.TryGetPool(module, order.Index, out var pool)) return;
        ref var fleet = ref pool.buffer[module.moduleFleets[order.Index].fleetId];
        if (fleet.id == order.Before.id && fleet.craftId == order.Before.craftId) CopyOrder(ref fleet, order.Before);
    }
    private void Remove(Command c, bool rollback)
    {
        if (rollback && c.Connection == Multiplayer.Session?.Authority.Connection.Value && GameMain.mainPlayer?.mecha is { } mecha)
            c.Module = c.Module.isSpace ? mecha.spaceCombatModule : mecha.groundCombatModule;
        if (rollback && c.Index < c.Module.moduleFleets.Length) c.Module.moduleFleets[c.Index].inCommand = c.BeforeCommand;
        if (rollback && c.Before.id > 0 && c.Index < c.Module.moduleFleets.Length)
        {
            var pool = Pool(c);
            if (pool != null && c.Before.id < pool.cursor && pool.buffer[c.Before.id].craftId == c.Before.craftId)
            {
                pool.buffer[c.Before.id].dispatch = c.Before.dispatch;
                pool.buffer[c.Before.id].target = c.Before.target;
                pool.buffer[c.Before.id].targetPos = c.Before.targetPos;
            }
        }
        foreach (var model in c.Models) RemoveModel(c, model);
        c.Models.Clear(); pending.Remove(c.Sequence);
    }
    public void Dispose()
    {
        foreach (var c in pending.Values.ToArray()) Remove(c, false);
        orders.Clear();
        while (results.TryDequeue(out _)) { }
    }
}
