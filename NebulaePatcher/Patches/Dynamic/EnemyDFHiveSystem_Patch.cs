#region

using HarmonyLib;
using NebulaeModel.Utils;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(EnemyDFHiveSystem))]
internal class EnemyDFHiveSystem_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.isAlive), MethodType.Getter)]
    public static bool IsAlive_Prefix(EnemyDFHiveSystem __instance, ref bool __result)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        var facts = Multiplayer.Session.AuthorityRuntime.DarkFogReplica;
        if (facts == null || !facts.HiveAlive.TryGetValue(__instance.hiveAstroId, out var alive)) return true;
        __result = alive;
        return false;
    }
    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.ActiveAllUnit))]
    public static bool ActiveAllUnit_Prefix() => !Multiplayer.IsActive || Multiplayer.Session.IsServer;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.AssaultingWavesDetermineAI))]
    public static bool AssaultingWavesDetermineAI_Prefix() => !Multiplayer.IsActive || Multiplayer.Session.IsServer;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.DeactivateUnit))]
    public static bool DeactivateUnit_Prefix(EnemyDFHiveSystem __instance, int unitId)
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient) return Multiplayer.Session.Enemies.IsIncomingRequest;

        var enemyId = __instance.units.buffer[unitId].enemyId;
        if (enemyId == 0)
        {
            return false;
        }
        ref var ptr = ref __instance.sector.enemyPool[enemyId];
        if (ptr.id != 0 && ptr.id == enemyId)
        {
            var port = (int)ptr.port;
            var formId = 8113 - ptr.protoId;
            if (__instance.hiveAstroId != ptr.originAstroId) // Assert.True(__instance.hiveAstroId == ptr.originAstroId)
            {
                return false;
            }
            var enemyFormation = __instance.forms[formId];
            if (enemyFormation.units[port] > 1) // Assert.True(enemyFormation.units[port] > 1
            {
                enemyFormation.units[port] = 1;
            }
            __instance.sector.RemoveEnemyFinal(enemyId);
        }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.ExecuteDeferredEnemyChange))]
    public static bool ExecuteDeferredEnemyChange_Prefix(EnemyDFHiveSystem __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        if (Multiplayer.Session.IsClient)
        {
            __instance._add_relay_list?.Clear();
            __instance._add_tinder_list?.Clear();
            __instance._rmv_id_list?.Clear();
            __instance._add_bidx_list?.Clear();
            return false;
        }

        if (__instance._add_relay_list != null && __instance._add_relay_list.Count > 0)
        {
            foreach (var dockIndex in __instance._add_relay_list)
            {
                ref var ptr = ref __instance.relayDocks[dockIndex % __instance.relayDocks.Length];
                var enemyId = __instance.sector.CreateEnemyFinal(__instance, 8116, __instance.hiveAstroId, ptr.pos, ptr.rot);
                var dfRelayId = __instance.sector.enemyPool[enemyId].dfRelayId;
                var dfrelayComponent = __instance.relays.buffer[dfRelayId];
                if (dfrelayComponent != null)
                {
                    dfrelayComponent.SetDockIndex(dockIndex);
                    __instance.AddIdleRelay(dfRelayId);
                }
            }
            __instance._add_relay_list.Clear();
        }
        if (__instance._add_tinder_list?.Count > 0)
        {
            foreach (var dockIndex in __instance._add_tinder_list)
            {
                ref var ptr2 = ref __instance.tinderDocks[dockIndex % __instance.tinderDocks.Length];
                var enemyId = __instance.sector.CreateEnemyFinal(__instance, 8119, __instance.hiveAstroId, ptr2.pos, ptr2.rot);
                var dfTinderId = __instance.sector.enemyPool[enemyId].dfTinderId;
                ref var tinder = ref __instance.tinders.buffer[dfTinderId];
                if (dfTinderId > 0)
                {
                    tinder.SetDockIndex(__instance, dockIndex);
                    __instance.AddIdleTinder(dfTinderId);
                }
            }
            __instance._add_tinder_list.Clear();
            __instance._add_tinder_list = null;
        }
        if (!__instance.realized)
        {
            return false;
        }
        if (__instance._rmv_id_list?.Count > 0)
        {
            foreach (var enemyId in __instance._rmv_id_list)
            {
                __instance.sector.RemoveEnemyFinal(enemyId);
            }
            __instance._rmv_id_list.Clear();
        }
        if (__instance._add_bidx_list?.Count > 0)
        {
            foreach (var builderIndex in __instance._add_bidx_list)
            {
                __instance.sector.CreateEnemyFinal(__instance, builderIndex, false);
            }
            __instance._add_bidx_list.Clear();
        }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.ExecuteDeferredUnitFormation))]
    public static bool ExecuteDeferredUnitFormation_Prefix(EnemyDFHiveSystem __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        if (Multiplayer.Session.IsClient)
        {
            __instance._initiate_unit_list?.Clear();
            __instance._add_tinder_list?.Clear();
            return false;
        }

        __instance._initiate_unit_list?.Clear();
        if (__instance._activate_unit_list?.Count > 0)
        {
            foreach (var item in __instance._activate_unit_list)
            {
                int unitId = __instance.ActivateUnit(item.formId, item.port, item.gameTick);
                if (unitId > 0)
                {
                    ref var ptr = ref __instance.units.buffer[unitId];
                    if (ptr.id == unitId)
                    {
                        ptr.stateTick = item.stateTick;
                        ptr.behavior = item.behavior;
                    }
                }
            }
            __instance._activate_unit_list.Clear();
        }
        if (__instance._deactivate_unit_list?.Count > 0)
        {
            foreach (var unitId in __instance._deactivate_unit_list)
            {
                __instance.DeactivateUnit(unitId);
            }
            __instance._deactivate_unit_list.Clear();
        }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.GameTickLogic))]
    public static bool GameTickLogic_Prefix(EnemyDFHiveSystem __instance) => !Multiplayer.IsActive || Multiplayer.Session.IsServer ||
        NebulaeWorld.Combat.NativeEnemySimulation.CanRun(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.KeyTickLogic))]
    public static bool KeyTickLogic_Prefix() => !Multiplayer.IsActive || Multiplayer.Session.IsServer;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.DecisionAI))]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.InterLearningFromLocalSystem))]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.InterLearningFromOtherSystem))]
    public static bool DecisionAI_Prefix() => !Multiplayer.IsActive || Multiplayer.Session.IsServer;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.DecisionAI))]
    public static void DecisionAI_Postfix(EnemyDFHiveSystem __instance, long time)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsServer || __instance.isLocal || !__instance.realized || !__instance.isAlive) return;
        foreach (var player in Multiplayer.Session.Combat.Players)
            if (player.isAlive && player.starId == __instance.starData.id)
            {
                var skill = __instance.sector.skillSystem;
                var aggressive = __instance.history.combatSettings.aggressiveLevel;
                if (__instance.history.dfTruceTimer > 0) aggressive = EAggressiveLevel.Passive;
                __instance.UpdateHatred(time, aggressive, skill.maxHatredSpaceTmp * 2, (int)(50f * skill.enemyAggressiveHatredCoefTmp));
                break;
            }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.LaunchLancerAssault))]
    public static bool LaunchLancerAssault_Prefix(int targetAstroId)
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient) return Multiplayer.Session.Enemies.IsIncomingRequest;

        Multiplayer.Session.Enemies.SendAstroMessage("Space hive is attacking".Translate(), targetAstroId);
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.NotifyRelayKilled))]
    public static bool NotifyRelayKilled_Prefix(EnemyDFHiveSystem __instance, ref EnemyData enemy)
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsServer)
        {
            Multiplayer.Session.Enemies.SendAstroMessage("DF relay killed on".Translate(), enemy.astroId);
            return true;
        }

        var dfrelayComponent = __instance.relays.buffer[enemy.dfRelayId];
        if (dfrelayComponent?.id == enemy.dfRelayId)
        {
            __instance.relayNeutralizedCounter++;
            // Client will wait for server to send RealizePlanetBase packet
        }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.Realize))]
    public static bool Realize_Prefix()
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient) return Multiplayer.Session.Enemies.IsIncomingRequest;

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.OpenPreview))]
    public static bool OpenPreview_Prefix(EnemyDFHiveSystem __instance)
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsServer) Multiplayer.Session.Enemies.OpenLocalHivePreview(__instance);
        else __instance.isPreview = true;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.ClosePreview))]
    public static bool ClosePreview_Prefix(EnemyDFHiveSystem __instance)
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsServer) Multiplayer.Session.Enemies.CloseLocalHivePreview(__instance);
        else __instance.isPreview = false;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.KillCorruptedUnits))]
    public static bool KillCorruptedUnits_Prefix()
    {
        // Don't run enemy position check on client
        return !Multiplayer.IsActive || Multiplayer.Session.IsServer;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.UpdateHatred))]
    public static bool UpdateHatred_Prefix()
    {
        if (!Multiplayer.IsActive) return true;

        return Multiplayer.Session.IsServer;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.UnderAttack),
        [typeof(VectorLF3), typeof(float)], [ArgumentType.Ref, ArgumentType.Normal])]
    public static bool UnderAttack_Prefix(EnemyDFHiveSystem __instance, ref VectorLF3 centerUPos, float radius)
    {
        if (!Multiplayer.IsActive) return true;
        if (Multiplayer.Session.IsClient) return false;

        if (!__instance.realized)
        {
            return false;
        }
        __instance.sector.InverseTransformToAstro_ref(__instance.hiveAstroId, ref centerUPos, out var localPos);
        var enemyData = default(EnemyData);
        var formTicks = (int)((__instance.starData.seed + GameMain.gameTick) % 1512000L);
        var pos = VectorLF3.zero;
        var vel = Vector3.zero;
        var rot = Quaternion.identity;
        for (var formId = 0; formId < __instance.forms.Length; formId++)
        {
            var enemyFormation = __instance.forms[formId];
            for (var portId = 1; portId <= enemyFormation.portCount; portId++)
            {
                if (enemyFormation.units[portId] == 1)
                {
                    enemyData.protoId = (short)(8113 - formId);
                    enemyData.port = (short)portId;
                    GalaxyLimits.SetSpaceFormationOwner(ref enemyData, __instance.hiveAstroId - 1000000);
                    enemyData.Formation(formTicks, (float)__instance.orbitRadius, ref pos, ref rot, ref vel);
                    var dx = localPos.x - pos.x;
                    var dy = localPos.y - pos.y;
                    var dz = localPos.z - pos.z;
                    if (dx * dx + dy * dy + dz * dz <= (double)(radius * radius))
                    {
                        __instance.ActivateUnitDeferred(formId, portId, GameMain.gameTick, EEnemyBehavior.KeepForm, 600);
                    }
                }
            }
        }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyDFHiveSystem.SensorLogic))]
    public static bool SensorLogic_Prefix(EnemyDFHiveSystem __instance, EAggressiveLevel aggressiveLevel)
    {
        if (!Multiplayer.IsActive) return true;

        // Clients derive warning colors separately and never accumulate hive hatred.
        if (Multiplayer.Session.IsClient) return false;
        var starId = __instance.starData.id;
        var skills = __instance.sector.skillSystem;
        foreach (var player in Multiplayer.Session.Combat.Players)
        {
            if (player.starId != starId || !player.isAlive || player.planetId > 0) continue;
            if (!NebulaeWorld.Combat.HiveSensor.Detect(__instance, player.uPosition, aggressiveLevel, true, out var hatred)) continue;
            __instance.hatred.HateTarget(ETargetType.Player, player.id,
                (int)((double)skills.maxHatredSpaceTmp * hatred * skills.enemyAggressiveHatredCoefTmp),
                skills.maxHatredSpaceHiveTmp / 10, EHatredOperation.Add);
        }
        if (Multiplayer.Session.Combat.ActivedStars.Contains(starId))
            for (var i = 1; i < __instance.sector.craftCursor; i++)
            {
                ref var craft = ref __instance.sector.craftPool[i];
                if (craft.id != i || craft.astroId != __instance.starData.astroId || craft.isInvincible) continue;
                __instance.sector.TransformFromAstro(craft.astroId, out var position, craft.pos);
                if (!NebulaeWorld.Combat.HiveSensor.Detect(__instance, position, aggressiveLevel, false, out var hatred)) continue;
                __instance.hatred.HateTarget(ETargetType.Craft, i,
                    (int)((double)skills.maxHatredSpaceTmp * hatred * 0.6 * skills.enemyAggressiveHatredCoefTmp),
                    skills.maxHatredSpaceHiveTmp / 10, EHatredOperation.Add);
            }
        return false;
    }
}
