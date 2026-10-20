#region

using System.IO;
using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;
using NebulaeWorld.GameStates;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(SkillSystem))]
internal class SkillSystem_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.Import))]
    public static bool Import_Prefix(SkillSystem __instance, BinaryReader r)
    {
        if (!SpaceSectorBootstrap.IsImporting) return true;

        if (r.ReadInt32() != 3) throw new InvalidDataException("Invalid sector bootstrap skill data.");
        __instance.combatStats.Import(r);
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            SkillTarget skillTarget;
            skillTarget.id = r.ReadInt32();
            skillTarget.astroId = r.ReadInt32();
            skillTarget.type = (ETargetType)r.ReadInt32();
            __instance.removedSkillTargets.Add(skillTarget);
        }
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(SkillSystem.CollectPlayerStates))]
    public static void CollectPlayerStates_Postfix(SkillSystem __instance)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsServer) return;

        // Set those flags to false so AddSpaceEnemyHatred can add threat correctly for client's skill in host
        __instance.playerIsSailing = false;
        __instance.playerIsWarping = false;
        // Set this flag to true so AddSpaceEnemyHatred can add threat correctly from craft/skill of other players even if host is dead (dedicated server)
        __instance.playerAlive = true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(SkillSystem.AfterTick))]
    public static void AfterTick_Postfix(SkillSystem __instance)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsServer) return;

        // Restore the modified player states
        __instance.CollectPlayerStates();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.MechaEnergyShieldResist),
        [typeof(SkillTarget), typeof(int)],
        [ArgumentType.Normal, ArgumentType.Ref])]
    [HarmonyPatch(nameof(SkillSystem.MechaEnergyShieldResist),
        [typeof(SkillTargetLocal), typeof(int), typeof(int)],
        [ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Ref])]
    public static bool MechaEnergyShieldResist_Prefix(SkillSystem __instance, ref bool __result, ref int damage)
    {
        ScopeStack<PlayerSkillScope>.Push(new PlayerSkillScope(__instance));
        if (!Multiplayer.IsActive) return true;
        __result = false;
        var target = NebulaeWorld.Combat.CombatTargetContext.TargetMecha ?? __instance.mecha;
        if (NebulaeWorld.Combat.CombatTargetContext.PlayerIdFor(target) == 0 || target.player?.isAlive != true)
        {
            damage = 0;
            return false;
        }
        // Run both native steps: retaliation hatred, then shield absorption. Sweep collision
        // getters alone do not change SkillSystem.mecha, so install the actual collision target.
        if (Multiplayer.Session.IsClient)
        {
            __result = Multiplayer.Session.PlayerWeapons.Resources.Recovery.Absorb(target, ref damage);
            return false;
        }
        __instance.mecha = target;
        return true;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(nameof(SkillSystem.MechaEnergyShieldResist), [typeof(SkillTarget), typeof(int)], [ArgumentType.Normal, ArgumentType.Ref])]
    [HarmonyPatch(nameof(SkillSystem.MechaEnergyShieldResist), [typeof(SkillTargetLocal), typeof(int), typeof(int)], [ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Ref])]
    public static System.Exception ShieldResist_Finalizer(System.Exception __exception)
    {
        ScopeStack<PlayerSkillScope>.Pop();
        return __exception;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.DamageObject))]
    public static bool DamageObject_Prefix()
    {
        if (WorldSimulation.IsClient) return true; // The transpiler returns a safe, empty native reference.
        if (!AuthorityRuleGuard.AllowHostRule())
        {
            return false;
        }

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.DamageGroundObjectByLocalCaster))]
    public static bool DamageGroundObjectByLocalCaster_Prefix()
    {
        if (WorldSimulation.IsClient) return true;
        if (!AuthorityRuleGuard.AllowHostRule())
        {
            return false;
        }

        return true;
    }

    /// <summary>The remote-caster ground damage entry.</summary>
    /// <remarks>
    /// This path writes <c>EntityData.constructStatId</c> and starts the damage-to-repair chain for a caster on
    /// another astro. The prefix refuses client calls outside replica apply and leaves damage arguments
    /// unchanged.
    /// </remarks>
    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.DamageGroundObjectByRemoteCaster))]
    public static bool DamageGroundObjectByRemoteCaster_Prefix()
    {
        if (WorldSimulation.IsClient) return true;
        return AuthorityRuleGuard.AllowHostRule();
    }

    [HarmonyPrefix, HarmonyPatch(nameof(SkillSystem.AddCombatStatHPIncoming))]
    public static bool IncomingDamage_Prefix(ref int __result)
    {
        if (!WorldSimulation.IsClient) return true;
        __result = 0;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(SkillSystem.AddGroundEnemyHatred), new[] { typeof(DFGBaseComponent), typeof(EnemyData), typeof(ETargetType), typeof(int) },
        new[] { ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal })]
    [HarmonyPatch(nameof(SkillSystem.AddGroundEnemyHatred), new[] { typeof(DFGBaseComponent), typeof(EnemyData), typeof(ETargetType), typeof(int), typeof(int) },
        new[] { ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal })]
    [HarmonyPatch(nameof(SkillSystem.AddSpaceEnemyHatred), new[] { typeof(EnemyDFHiveSystem), typeof(EnemyData), typeof(ETargetType), typeof(int), typeof(int) },
        new[] { ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal })]
    [HarmonyPatch(nameof(SkillSystem.AddSpaceEnemyHatred), new[] { typeof(EnemyDFHiveSystem), typeof(EnemyData), typeof(ETargetType), typeof(int), typeof(int), typeof(int) },
        new[] { ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal })]
    public static bool Hatred_Prefix() => !WorldSimulation.IsClient;
}
