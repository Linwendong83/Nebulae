using UnityEngine;

namespace NebulaeWorld.Combat;

/// <summary>Ground turret rules with explicit session targets, independent of the camera player.</summary>
internal static class GroundTurretCombat
{
    internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);

    public static bool TryPlayer(PlanetFactory factory, int id, out CombatManager.PlayerPosition player)
    {
        player = default;
        var players = Multiplayer.Session?.Combat?.Players;
        if (factory == null || id <= 0 || players == null) return false;
        foreach (var candidate in players)
            if (candidate.id == id)
            {
                if (!candidate.isAlive || candidate.planetId != factory.planetId ||
                    !Finite(candidate.skillTargetL) || !Finite(candidate.velocityL) || !Finite(candidate.velocityL.sqrMagnitude)) return false;
                player = candidate;
                return true;
            }
        return false;
    }

    private static HatredList Hatred(DFGBaseComponent component)
    {
        if (component == null) return default;
        component.hatredLock.Enter();
        try { return component.hatred; }
        finally { component.hatredLock.Exit(); }
    }

    private static HatredTarget At(in HatredList list, int index) => index switch
    {
        0 => list.max, 1 => list.h1, 2 => list.h2, 3 => list.h3,
        4 => list.h4, 5 => list.h5, 6 => list.h6, _ => list.min
    };

    private static ref HatredTarget Slot(ref HatredList list, int index)
    {
        switch (index)
        {
            case 0: return ref list.max; case 1: return ref list.h1; case 2: return ref list.h2; case 3: return ref list.h3;
            case 4: return ref list.h4; case 5: return ref list.h5; case 6: return ref list.h6; default: return ref list.min;
        }
    }

    public static void PruneHatred(DFGBaseComponent component, PlanetFactory factory)
    {
        if (component == null || factory == null || Multiplayer.Session.IsClient) return;
        component.hatredLock.Enter();
        try
        {
            var changed = false;
            for (var i = 0; i < 8; i++)
            {
                ref var entry = ref Slot(ref component.hatred, i);
                if (entry.target == 0) continue;
                if (entry.targetType is not (ETargetType.None or ETargetType.Craft or ETargetType.Player)) continue;
                if (TryPosition(factory, new SkillTargetLocal { type = entry.targetType, id = entry.objectId }, out _)) continue;
                entry = default; changed = true;
            }
            if (changed) component.hatred.Arrange();
        }
        finally { component.hatredLock.Exit(); }
    }

    public static int RetaliationPlayer(PlanetFactory factory, DFGBaseComponent component)
    {
        if (factory?.enemyPool == null || component == null || component.enemyId <= 0 ||
            component.enemyId >= factory.enemyPool.Length || factory.enemyPool[component.enemyId].id != component.enemyId ||
            !factory.enemyPool[component.enemyId].counterAttack) return 0;
        var hatred = Hatred(component);
        var selected = 0; var value = 0;
        for (var i = 0; i < 8; i++)
        {
            var entry = At(hatred, i);
            if (entry.targetType != ETargetType.Player || entry.value <= 0 ||
                !TryPlayer(factory, entry.objectId, out _)) continue;
            if (entry.value > value || entry.value == value && entry.objectId < selected)
            { selected = entry.objectId; value = entry.value; }
        }
        return selected;
    }

    public static bool IsCounterAttack(PlanetFactory factory, DFGBaseComponent component, SkillTargetLocal target) =>
        target.type == ETargetType.Player && TryPlayer(factory, target.id, out _) && RetaliationPlayer(factory, component) == target.id;

    private static bool TryPosition(PlanetFactory factory, SkillTargetLocal target, out Vector3 position)
    {
        position = default;
        var id = target.id;
        if (factory == null || id <= 0) return false;
        switch (target.type)
        {
            case ETargetType.Player:
                if (!TryPlayer(factory, id, out var player)) return false;
                position = player.skillTargetL; break;
            case ETargetType.None:
                if (factory.entityPool == null || id >= factory.entityPool.Length || factory.entityPool[id].id != id) return false;
                position = factory.entityPool[id].pos; break;
            case ETargetType.Craft:
                if (factory.craftPool == null || id >= factory.craftPool.Length || factory.craftPool[id].id != id || factory.craftPool[id].isInvincible) return false;
                position = factory.craftPool[id].pos; break;
            case ETargetType.Vegetable:
                if (factory.vegePool == null || id >= factory.vegePool.Length || factory.vegePool[id].id != id) return false;
                position = factory.vegePool[id].pos; break;
            case ETargetType.Vein:
                if (factory.veinPool == null || id >= factory.veinPool.Length || factory.veinPool[id].id != id) return false;
                position = factory.veinPool[id].pos; break;
            default: return false;
        }
        return Finite(position);
    }

    public static bool InRange(in DFGTurretComponent turret, PlanetFactory factory, SkillTargetLocal target,
        float sensorRange, float attackRange, DFGBaseComponent component, out float distance)
    {
        distance = 0;
        if (!Finite(turret.muzzleWPos) || !TryPosition(factory, target, out var position)) return false;
        distance = (position - turret.muzzleWPos).sqrMagnitude;
        if (!Finite(distance)) return false;
        if (target.type == ETargetType.Player && (Multiplayer.Session.IsClient
                ? Multiplayer.Session.DarkFogTurrets?.CounterAttackFor(factory, target.id, turret.enemyId) == true
                : RetaliationPlayer(factory, component) == target.id)) return true;
        var range = target.type == ETargetType.Player ? sensorRange : attackRange;
        return Finite(range) && range >= 0 && distance <= range * range;
    }

    public static void Clear(ref DFGTurretComponent turret)
    {
        turret.target = default; turret.targetInShootRange = false; turret.state = EDFTurretState.Searching;
        if (!Finite(turret.localDir)) turret.localDir = Vector3.forward;
    }

    private static bool Select(ref DFGTurretComponent turret, PlanetFactory factory, SkillTargetLocal target)
    {
        if (!TryPosition(factory, target, out var position)) { Clear(ref turret); return false; }
        var direction = position - turret.enemyPos;
        Maths.QInvRotate_ref(ref turret.enemyRot, ref direction, ref direction);
        direction.y -= turret.muzzleY;
        if (!Finite(direction)) { Clear(ref turret); return false; }
        var length = direction.magnitude;
        if (!Finite(turret.localDir) || turret.localDir.sqrMagnitude < 1e-12f) turret.localDir = length > 1e-6f ? direction : Vector3.forward;
        else if (length > 1e-6f) turret.localDir = turret.localDir.normalized * length;
        turret.target = target; turret.state = EDFTurretState.Aiming; turret.targetInShootRange = false;
        return true;
    }

    public static bool Search(ref DFGTurretComponent turret, PlanetFactory factory, DFGBaseComponent component,
        EAggressiveLevel level, int threadIdx)
    {
        if (Multiplayer.Session.IsClient || NativeDarkFogTurrets.Active) return false;
        if (level <= EAggressiveLevel.Dummy || !Finite(turret.muzzleWPos) || !Finite(turret.realAttactRange) ||
            turret.realAttactRange < 0 || !Finite(turret.sensorRange) || turret.sensorRange < 0)
        { Clear(ref turret); return false; }
        var counter = RetaliationPlayer(factory, component);
        if (level > EAggressiveLevel.Passive && counter > 0)
            return Select(ref turret, factory, new SkillTargetLocal { type = ETargetType.Player, id = counter });
        SkillTargetLocal selected = default;
        if (level == EAggressiveLevel.Passive)
        {
            var hatred = Hatred(component); var value = 0;
            for (var i = 0; i < 8; i++)
            {
                var entry = At(hatred, i);
                if (entry.value <= value || entry.target == 0) continue;
                var candidate = new SkillTargetLocal { type = entry.targetType, id = entry.objectId };
                if (!InRange(turret, factory, candidate, turret.sensorRange, turret.realAttactRange, component, out _)) continue;
                value = entry.value; selected = candidate;
            }
        }
        else
        {
            var distance = float.MaxValue;
            var players = Multiplayer.Session.Combat?.Players;
            if (players != null) foreach (var player in players)
            {
                if (!TryPlayer(factory, player.id, out var valid)) continue;
                var next = (valid.skillTargetL - turret.muzzleWPos).sqrMagnitude;
                if (!Finite(next) || next > turret.sensorRange * turret.sensorRange && next > turret.realAttactRange * turret.realAttactRange) continue;
                if (next < distance || next == distance && player.id < selected.id)
                { selected = new SkillTargetLocal { type = ETargetType.Player, id = player.id }; distance = next; }
            }
            if (selected.id > 0 && distance <= turret.realAttactRange * turret.realAttactRange) return Select(ref turret, factory, selected);
            SearchHash(turret, factory, factory.hashSystemStatic, false, threadIdx, ref selected, ref distance);
            SearchHash(turret, factory, factory.hashSystemDynamic, true, threadIdx, ref selected, ref distance);
        }
        if (selected.id > 0) return Select(ref turret, factory, selected);
        Clear(ref turret); return false;
    }

    private static void SearchHash(in DFGTurretComponent turret, PlanetFactory factory, HashSystem hash, bool craft,
        int threadIdx, ref SkillTargetLocal selected, ref float distance)
    {
        if (hash == null) return;
        int count; int[] buckets;
        if (threadIdx >= 0 && threadIdx < 1024)
            buckets = hash.GetBucketIdxesInAreaParallel(turret.muzzleWPos, turret.realAttactRange, threadIdx, out count);
        else
        {
            hash.GetBucketIdxesInArea(turret.muzzleWPos, turret.realAttactRange);
            buckets = hash.activeBuckets; count = hash.activeBucketsCount; hash.ClearActiveBuckets();
        }
        for (var i = 0; i < count; i++)
        {
            var bucket = buckets[i]; var offset = hash.bucketOffsets[bucket];
            for (var j = 0; j < hash.bucketCursors[bucket]; j++)
            {
                var packed = hash.hashPool[offset + j];
                if (packed == 0 || packed >> 28 != (craft ? 6 : 0)) continue;
                var candidate = new SkillTargetLocal { type = craft ? ETargetType.Craft : ETargetType.None, id = packed & 0xFFFFFFF };
                if (!TryPosition(factory, candidate, out var position)) continue;
                var next = (position - turret.muzzleWPos).sqrMagnitude;
                if (!Finite(next) || next > turret.realAttactRange * turret.realAttactRange || next >= distance) continue;
                selected = candidate; distance = next;
            }
        }
    }

    public static void Aim(ref DFGTurretComponent turret, PlanetFactory factory, DFGBaseComponent component,
        EnemyBuilderComponent[] builders, EAggressiveLevel level, int threadIdx)
    {
        turret.targetInShootRange = false;
        if (!Finite(turret.localDir)) turret.localDir = Vector3.forward;
        var client = Multiplayer.Session.IsClient;
        if (!Finite(turret.muzzleWPos) || !Finite(turret.realAttactRange) || turret.realAttactRange < 0 ||
            !Finite(turret.sensorRange) || turret.sensorRange < 0) { Clear(ref turret); return; }
        if (level <= EAggressiveLevel.Dummy) { Clear(ref turret); return; }
        if (client && !NativeDarkFogTurrets.Active) return;
        var counter = client ? 0 : RetaliationPlayer(factory, component);
        var valid = TryPosition(factory, turret.target, out var position);
        if (!client && (!valid || (position - turret.muzzleWPos).sqrMagnitude > turret.realAttactRange * turret.realAttactRange ||
            level > EAggressiveLevel.Passive && counter > 0 && (turret.target.type != ETargetType.Player || turret.target.id != counter)))
        {
            Search(ref turret, factory, component, level, threadIdx);
            valid = TryPosition(factory, turret.target, out position);
        }
        if (!valid) { Clear(ref turret); return; }
        var retaliation = turret.target.type == ETargetType.Player && (client
            ? Multiplayer.Session.DarkFogTurrets?.CounterAttackFor(factory, turret.target.id, turret.enemyId) == true
            : counter == turret.target.id);
        var direction = position - turret.enemyPos;
        Maths.QInvRotate_ref(ref turret.enemyRot, ref direction, ref direction);
        direction.y -= turret.muzzleY;
        var distance = direction.sqrMagnitude;
        if (!Finite(direction) || !Finite(distance) || distance <= 1e-12f) return;
        if (!Finite(turret.localDir) || turret.localDir.sqrMagnitude <= 1e-12f) turret.localDir = direction;
        else turret.localDir = turret.localDir.normalized * Mathf.Sqrt(distance);
        var angle = Vector3.Angle(direction, turret.localDir);
        if (turret.type == EDFTurretType.Laser)
            turret.localDir = angle < 1e-5f ? direction : Vector3.Slerp(turret.localDir, direction, 1f / Mathf.Max(1f, angle));
        var end = position;
        var model = turret.target.type == ETargetType.None ? factory.entityPool[turret.target.id].modelIndex :
            turret.target.type == ETargetType.Craft ? factory.craftPool[turret.target.id].modelIndex : -1;
        if (model >= 0 && SkillSystem.RoughHeightByModelIndex != null && model < SkillSystem.RoughHeightByModelIndex.Length)
            end += position.normalized * (SkillSystem.RoughHeightByModelIndex[model] * 0.5f);
        var ray = (end - turret.muzzleWPos).normalized;
        if (!Finite(ray) || ray.sqrMagnitude <= 1e-12f) return;
        var dot = Vector3.Dot(turret.muzzleWPos, ray);
        turret.targetInShootRange = (distance <= turret.realAttactRange * turret.realAttactRange || retaliation) &&
            (turret.type != EDFTurretType.Laser || angle < 2f) &&
            (!Phys.RayCastSphere(turret.muzzleWPos, ray, 200f, Vector3.zero, 197.6f) || distance <= dot * dot);
        if (!turret.targetInShootRange || turret.fire > 0 || turret.heat < 0 || component == null ||
            builders == null || turret.builderId < 0 || turret.builderId >= builders.Length || builders[turret.builderId].state < 3 ||
            factory.enemyPool == null || turret.enemyId <= 0 || turret.enemyId >= factory.enemyPool.Length || factory.enemyPool[turret.enemyId].id != turret.enemyId) return;
        if (turret.type == EDFTurretType.Plasma && turret.target.type == ETargetType.Player)
        {
            if (!TryPlayer(factory, turret.target.id, out var player)) return;
            var delta = end - turret.muzzleWPos;
            var intercept = delta + player.velocityL * (delta.magnitude / 2.5f / 60f);
            if (!Finite(intercept) || !Finite(intercept.sqrMagnitude) || intercept.sqrMagnitude <= 1e-12f)
            { turret.targetInShootRange = false; return; }
        }
        var modelIndex = factory.enemyPool[turret.enemyId].modelIndex;
        var prefabs = SpaceSector.PrefabDescByModelIndex;
        if (prefabs == null || modelIndex < 0 || modelIndex >= prefabs.Length || prefabs[modelIndex] == null ||
            turret.type is not (EDFTurretType.Laser or EDFTurretType.Plasma)) return;
        var prefab = prefabs[modelIndex];
        if (builders[turret.builderId].energy <= prefab.dfTurretAttackEnergy) return;
        builders[turret.builderId].energy -= prefab.dfTurretAttackEnergy;
        turret.level = component.evolve.level;
        if (turret.type == EDFTurretType.Laser) turret.Shoot_Laser(factory, prefab, ref end);
        else if (turret.type == EDFTurretType.Plasma) turret.Shoot_Plasma(factory, prefab, ref end, ref ray);
    }

    public static Vector3 TargetVelocity(SkillSystem skills, ref DFGTurretComponent turret, PlanetFactory factory) =>
        !Multiplayer.IsActive ? skills.playerVelocityL : TryPlayer(factory, turret.target.id, out var player) ? player.velocityL : Vector3.zero;
}
