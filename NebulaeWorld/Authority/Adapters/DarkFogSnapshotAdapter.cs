using System;
using System.Collections.Generic;
using System.IO;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;

namespace NebulaeWorld.Authority.Adapters;

/// <summary>Small UI facts independent of instantiated enemies and local preview lifetimes.</summary>
internal sealed class DarkFogSnapshotAdapter : IHostWorldView
{
    private readonly AuthorityEpoch epoch;
    private readonly Dictionary<ScopeKey, SlotGenerationTracker> trackers = new();
    private readonly Dictionary<ObjectKey, byte[]> publishedHives = new();
    private readonly Dictionary<ObjectKey, HiveFormationExportCache> formationExports = new();
    private readonly Dictionary<ObjectKey, byte[]> publishedBases = new();
    private readonly Dictionary<int, byte[]> baseRemovalFlags = new();
    private readonly List<ObjectKey> retired = [];

    // The hive summary is restated in full on every sample, and a sector holds one record per hive.
    // Encoding into a reused buffer and comparing bytes decides "unchanged" without allocating, and
    // is exactly the comparison the replicator would have made a moment later — the encoding is a
    // pure function of the hive, so identical bytes and an unchanged hive are the same statement.
    [ThreadStatic] private static MemoryStream hiveBuffer;
    [ThreadStatic] private static BinaryWriter hiveWriter;

    public DarkFogSnapshotAdapter(AuthorityEpoch epoch) => this.epoch = epoch;

    public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
    {
        if (scope.Kind != PoolKind.HiveSummary && scope.Kind != PoolKind.Base) return false;
        if (!trackers.TryGetValue(scope, out var tracker)) trackers[scope] = tracker = new SlotGenerationTracker();
        if (scope.Kind == PoolKind.HiveSummary)
        {
            var hives = GameMain.spaceSector?.dfHivesByAstro;
            if (hives == null) return false;
            for (var id = 1; id < hives.Length; id++)
                if (hives[id] != null) members.Add(ObjectKey.Create(epoch, scope.Kind, scope.Scope, id, tracker.ObserveOccupied(id)));
        }
        else
        {
            var bases = GameMain.galaxy?.PlanetById(scope.Scope)?.factory?.enemySystem?.bases;
            if (bases?.buffer == null) return false;
            baseRemovalFlags.TryGetValue(scope.Scope, out var flags);
            baseRemovalFlags[scope.Scope] = SampleRemovalFlags(GameMain.galaxy.PlanetById(scope.Scope).factory.enemySystem, flags);
            for (var id = 1; id < bases.cursor; id++)
                if (bases.buffer[id]?.id == id) members.Add(ObjectKey.Create(epoch, scope.Kind, scope.Scope, id, tracker.ObserveOccupied(id)));
        }
        tracker.EndScan();
        // A record whose identity moved on is no longer described by this scope: drop it so a later
        // sample of the same slot starts from a fresh reading rather than reusing a stale one.
        retired.Clear();
        foreach (var key in publishedHives.Keys)
            if (key.Kind == scope.Kind && key.Scope == scope.Scope &&
                (!tracker.IsAlive(key.NativeId) || tracker.GenerationOf(key.NativeId) != key.Generation)) retired.Add(key);
        foreach (var key in retired) { publishedHives.Remove(key); formationExports.Remove(key); }
        retired.Clear();
        foreach (var key in publishedBases.Keys)
            if (key.Kind == scope.Kind && key.Scope == scope.Scope &&
                (!tracker.IsAlive(key.NativeId) || tracker.GenerationOf(key.NativeId) != key.Generation)) retired.Add(key);
        foreach (var key in retired) publishedBases.Remove(key);
        return true;
    }

    public bool TryReadState(ObjectKey key, out byte[] state)
    {
        state = null;
        if (!key.Epoch.Equals(epoch)) return false;
        if (!trackers.TryGetValue(new ScopeKey(key.Kind, key.Scope), out var tracker) || tracker.GenerationOf(key.NativeId) != key.Generation) return false;
        if (key.Kind == PoolKind.HiveSummary) return TryReadHiveState(key, out state);
        if (key.Kind == PoolKind.Base) return TryReadBaseState(key, out state);
        return false;
    }

    private bool TryReadHiveState(ObjectKey key, out byte[] state)
    {
        state = null;
        var hives = GameMain.spaceSector?.dfHivesByAstro;
        if (hives == null || key.NativeId >= hives.Length || hives[key.NativeId] == null) return false;
        var hive = hives[key.NativeId];
        var stream = hiveBuffer ??= new MemoryStream(256);
        var writer = hiveWriter ??= new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        stream.SetLength(0);
        writer.Write(hive.starData.id); writer.Write(hive.hiveOrbitIndex);
        writer.Write(hive.realized); writer.Write(hive.isEmpty); writer.Write(hive.isAlive);
        writer.Write(hive.rootEnemyId);
        hive.evolve.Export(writer);
        writer.Write(hive.currentIncomingAssaultingUnitCount);
        writer.Write(hive.currentIncomingAssaultingUnitUPos.x);
        writer.Write(hive.currentIncomingAssaultingUnitUPos.y);
        writer.Write(hive.currentIncomingAssaultingUnitUPos.z);
        writer.Write(hive.totalAvailableBuildingCount); writer.Write(hive.totalUnitCount);
        var orbit = hive.hiveAstroOrbit;
        writer.Write(orbit.orbitRadius); writer.Write(orbit.orbitInclination); writer.Write(orbit.orbitLongitude);
        writer.Write(orbit.orbitalPeriod); writer.Write(orbit.orbitPhase);
        writer.Write(orbit.orbitRotation.x); writer.Write(orbit.orbitRotation.y); writer.Write(orbit.orbitRotation.z); writer.Write(orbit.orbitRotation.w);
        writer.Write(orbit.orbitNormal.x); writer.Write(orbit.orbitNormal.y); writer.Write(orbit.orbitNormal.z);
        writer.Write(hive.orbitRadius);
        if (!formationExports.TryGetValue(key, out var formations)) formationExports[key] = formations = new();
        for (var form = 0; form < 3; form++) formations.Write(writer, form, hive.forms[form].units);

        var length = (int)stream.Position;
        var buffer = stream.GetBuffer();
        if (publishedHives.TryGetValue(key, out var previous) && previous.Length == length &&
            SameBytes(previous, buffer, length))
        {
            // Unchanged: hand back the record already in flight so the replicator's own comparison
            // short-circuits on reference equality instead of walking the payload again.
            state = previous;
            return true;
        }
        state = new byte[length];
        Buffer.BlockCopy(buffer, 0, state, 0, length);
        publishedHives[key] = state;
        return true;
    }

    private bool TryReadBaseState(ObjectKey key, out byte[] state)
    {
        state = null;
        var bases = GameMain.galaxy?.PlanetById(key.Scope)?.factory?.enemySystem?.bases;
        if (bases?.buffer == null || key.NativeId >= bases.cursor || bases.buffer[key.NativeId]?.id != key.NativeId) return false;
        var item = bases.buffer[key.NativeId];
        var stream = hiveBuffer ??= new MemoryStream(512);
        var writer = hiveWriter ??= new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        stream.SetLength(0);
        WriteBase(writer, item, baseRemovalFlags[key.Scope][key.NativeId]);
        var length = (int)stream.Position;
        var buffer = stream.GetBuffer();
        if (publishedBases.TryGetValue(key, out var previous) && previous.Length == length && SameBytes(previous, buffer, length))
        {
            state = previous;
            return true;
        }
        state = new byte[length];
        Buffer.BlockCopy(buffer, 0, state, 0, length);
        publishedBases[key] = state;
        return true;
    }

    // One builder-pool pass per planet, instead of CheckBaseCanRemoved's full pass for every base.
    internal static byte[] SampleRemovalFlags(EnemyDFGroundSystem system, byte[] flags)
    {
        if (flags == null || flags.Length < system.bases.cursor) flags = new byte[system.bases.capacity];
        Array.Clear(flags, 0, flags.Length);
        for (var id = 1; id < system.bases.cursor; id++)
        {
            var item = system.bases.buffer[id];
            if (item?.id != id) continue;
            if (system.builders.buffer[item.builderId].sp != 0) flags[id] |= 1;
            for (var form = 0; form < 3; form++) if (item.forms[form].unitCount > 0) flags[id] |= 4;
        }
        for (var id = 1; id < system.builders.cursor; id++)
        {
            ref var builder = ref system.builders.buffer[id];
            if (builder.id != id) continue;
            ref var enemy = ref system.factory.enemyPool[builder.enemyId];
            if (enemy.owner > 0 && enemy.owner < flags.Length && enemy.dfGBaseId != enemy.owner) flags[enemy.owner] |= 2;
        }
        return flags;
    }

    // Same wire layout as GroundBaseStateCodec, written directly from native pools into a reused
    // buffer. Unchanged samples allocate neither a DTO, an evolution blob nor formation arrays.
    internal static void WriteBase(BinaryWriter writer, DFGBaseComponent item, byte removalFlags)
    {
        writer.Write(item.enemyId); writer.Write(item.ruinId);
        writer.Write(Multiplayer.Session?.AuthorityRuntime?.RuinHost?.Generation(item.groundSystem.factory.planetId, item.ruinId) ?? 0);
        writer.Write(item.groundSystem.builders.buffer[item.builderId].sp > 0);
        writer.Write(removalFlags); writer.Write(item.hiveAstroId); writer.Write(item.relayEnemyId);
        var stream = writer.BaseStream;
        var sizeOffset = stream.Position;
        writer.Write((ushort)0);
        var start = stream.Position;
        item.evolve.Export(writer);
        var end = stream.Position;
        if (end - start > GroundBaseStateCodec.MaxEvolutionBytes) throw new InvalidDataException("Invalid evolution size.");
        stream.Position = sizeOffset; writer.Write((ushort)(end - start)); stream.Position = end;
        writer.Write(item.currentIncomingAssaultingUnitCount);
        writer.Write(item.currentIncomingAssaultingUnitPos.x); writer.Write(item.currentIncomingAssaultingUnitPos.y); writer.Write(item.currentIncomingAssaultingUnitPos.z);
        writer.Write(item.totalAvailableBuildingCount); writer.Write(item.totalUnitCount);
        for (var form = 0; form < 3; form++)
        {
            var count = GroundBaseStateCodec.PortCount(form);
            writer.Write((ushort)count);
            var tags = item.forms[form].units;
            for (var port = 1; port <= count; port += 4)
            {
                if ((uint)tags[port] > 3 || (uint)tags[port + 1] > 3 || (uint)tags[port + 2] > 3 || (uint)tags[port + 3] > 3)
                    throw new InvalidDataException("Invalid formation tag.");
                writer.Write((byte)(tags[port] | tags[port + 1] << 2 | tags[port + 2] << 4 | tags[port + 3] << 6));
            }
        }
    }

    private static bool SameBytes(byte[] a, byte[] b, int length)
    {
        for (var i = 0; i < length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
