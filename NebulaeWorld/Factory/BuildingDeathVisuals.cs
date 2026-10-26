using System;
using System.IO;
using UnityEngine;

namespace NebulaeWorld.Factory;

/// <summary>Portable inputs to the visual half of KillEntityFinally. Never creates ruins or drops.</summary>
internal static class BuildingDeathVisuals
{
    [ThreadStatic] private static DeathScope currentDeath;

    private sealed class DeathScope : IDisposable
    {
        internal readonly ImpactData Impact;
        private readonly DeathScope previous;
        internal DeathScope(ImpactData impact) { Impact = impact; previous = currentDeath; currentDeath = this; }
        public void Dispose() => currentDeath = previous;
    }

    // One native death may also destroy stacked buildings. Each uses the same killing impact.
    internal static IDisposable BeginDeath(ImpactData impact) => new DeathScope(impact);
    internal static ImpactData CurrentImpact => currentDeath?.Impact ?? default;

    internal struct Wreckage
    {
        internal int ModelIndex, StateIndex;
        internal WreckageHandler.ETransformType Transform;
        internal Vector3 Data;
        internal ImpactData Impact;
    }

    internal static byte[] Capture(PlanetFactory factory, int objectId)
    {
        if (objectId <= 0) return Array.Empty<byte>(); // Destroyed prebuilds do not spawn wreckage in vanilla.
        ref var entity = ref factory.entityPool[objectId];
        var model = LDB.models.Select(entity.modelIndex);
        if (model == null || model.RuinType == ERuinType.None) return Array.Empty<byte>();
        var prefab = PlanetFactory.PrefabDescByModelIndex[model.RuinId != 0 ? model.RuinId : model.ID]?.wreckagePrefab;
        if (prefab is null) return Array.Empty<byte>();
        var visual = new Wreckage { ModelIndex = entity.modelIndex, Transform = prefab.transformType, Impact = CurrentImpact };
        if (entity.powerExcId > 0)
        {
            var state = factory.powerSystem.excPool[entity.powerExcId].state;
            visual.StateIndex = state > .5f ? 1 : state < -.5f ? 2 : 0;
        }
        if (visual.Transform == WreckageHandler.ETransformType.HasTarget)
        {
            visual.Data = model.prefabDesc.isTurret ? factory.defenseSystem.turrets.buffer[entity.turretId].localDir :
                model.prefabDesc.isEjector ? factory.factorySystem.ejectorPool[entity.ejectorId].localDir : Vector3.forward;
        }
        else if (visual.Transform == WreckageHandler.ETransformType.OrbitalSubstation)
        {
            ref var node = ref factory.powerSystem.nodePool[entity.powerNodeId];
            var animation = factory.entityAnimPool[objectId];
            if (Math.Abs(4f - animation.time) < .005f) visual.StateIndex = 1;
            visual.Data = new Vector3(animation.time, animation.working_length, factory.powerSystem.netPool[node.networkId].energyServed);
        }
        return Encode(visual);
    }

    internal static byte[] Encode(Wreckage visual)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(visual.ModelIndex); writer.Write(visual.StateIndex); writer.Write((int)visual.Transform);
        Write(writer, visual.Data);
        writer.Write(visual.Impact.mass); Write(writer, visual.Impact.point); Write(writer, visual.Impact.velocity);
        return stream.ToArray();
    }

    internal static Wreckage Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return default;
        if (bytes.Length != 52) throw new InvalidDataException("Invalid building wreckage payload.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        var visual = new Wreckage
        {
            ModelIndex = reader.ReadInt32(),
            StateIndex = reader.ReadInt32(),
            Transform = (WreckageHandler.ETransformType)reader.ReadInt32(),
            Data = Read(reader),
            Impact = new ImpactData { mass = reader.ReadSingle(), point = Read(reader), velocity = Read(reader) }
        };
        if (visual.ModelIndex <= 0 || visual.StateIndex < 0 || visual.Transform < WreckageHandler.ETransformType.None ||
            visual.Transform > WreckageHandler.ETransformType.OrbitalSubstation || visual.Impact.mass < 0 ||
            !Finite(visual.Data) || !Finite(visual.Impact.point) || !Finite(visual.Impact.velocity) ||
            float.IsNaN(visual.Impact.mass) || float.IsInfinity(visual.Impact.mass))
            throw new InvalidDataException("Invalid building wreckage inputs.");
        return visual;
    }

    internal static void Play(PlanetFactory factory, int objectId, Wreckage visual)
    {
        if (objectId <= 0 || visual.ModelIndex == 0 || !factory.planet.factoryLoaded || factory.planet.factoryModel is null) return;
        try
        {
            ref var entity = ref factory.entityPool[objectId];
            if (entity.id != objectId || entity.modelIndex != visual.ModelIndex) return;
            var model = LDB.models.Select(visual.ModelIndex);
            if (model == null) return;
            var prefab = PlanetFactory.PrefabDescByModelIndex[model.RuinId != 0 ? model.RuinId : model.ID];
            if (prefab?.wreckageFragments == null || visual.StateIndex >= prefab.wreckageFragments.Length) return;
            if (visual.Transform == WreckageHandler.ETransformType.None)
                factory.planet.factoryModel.AddWreckage(model, visual.StateIndex, ref entity.pos, ref entity.rot, ref visual.Impact);
            else
                factory.planet.factoryModel.AddWreckage(model, visual.StateIndex, ref entity.pos, ref entity.rot,
                    visual.Data, ref visual.Impact, visual.Transform);
        }
        catch (Exception error)
        {
            NebulaeModel.Logger.Log.Warn($"[factory] building death presentation failed planet={factory.planetId} object={objectId}: {error.Message}");
        }
    }

    private static void Write(BinaryWriter writer, Vector3 v) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
    private static Vector3 Read(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    private static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) &&
        !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
}
