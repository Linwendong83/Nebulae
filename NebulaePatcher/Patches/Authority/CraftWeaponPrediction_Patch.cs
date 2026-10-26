using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class CraftWeaponPrediction_Patch
{
    internal struct State
    {
        internal CraftWeaponPrediction.Capture Capture;
        internal bool Suppressed;
        internal int Fire0, Fire1;
    }
    internal static IEnumerable<MethodBase> TargetMethods() => AccessTools.GetDeclaredMethods(typeof(UnitComponent))
        .Where(m => m.Name is nameof(UnitComponent.RunBehavior_Engage_Ground) or nameof(UnitComponent.RunBehavior_Engage_Space));

    [HarmonyPrefix]
    private static void Prefix(ref UnitComponent __instance, object __0, PrefabDesc pdesc, ref CraftData craft,
        out State __state)
    {
        __state = default;
        if (!Multiplayer.IsActive || Multiplayer.Session.Authority.IsActive != true) return;
        if (WorldSimulation.IsClient && !NativeCraftSimulation.PredictsWeapons(NativeCraftSimulation.FleetPlanet, __instance.craftId))
        {
            __state.Suppressed = true; __state.Fire0 = __instance.fire0; __state.Fire1 = __instance.fire1;
            // Keep native movement and aiming intact while preventing unauthorised launches.
            // Replacing field stores inside these large methods corrupts their movement
            // locals under Unity Mono, even when the replacement writes the same value.
            __instance.fire0 = __instance.fire1 = int.MaxValue / 4;
            return;
        }
        var system = __0 is PlanetFactory factory ? factory.skillSystem : ((SpaceSector)__0).skillSystem;
        __state.Capture = Multiplayer.Session.BattleVisuals?.CraftWeapons.Begin(system, __instance, craft, pdesc, __0 is PlanetFactory);
    }
    [HarmonyFinalizer]
    private static Exception Finalizer(ref UnitComponent __instance, State __state, Exception __exception)
    {
        if (__state.Suppressed)
        {
            __instance.fire0 = __state.Fire0; __instance.fire1 = __state.Fire1;
            __instance.isShooting0 = __instance.isShooting1 = false; __instance.anim = 0;
        }
        Multiplayer.Session?.BattleVisuals?.CraftWeapons.End(__state.Capture);
        return __exception;
    }
}
