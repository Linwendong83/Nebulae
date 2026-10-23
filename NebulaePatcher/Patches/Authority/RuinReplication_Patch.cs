using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class RuinReplication_Patch
{
    [HarmonyPrefix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.RemoveRuinWithComponet))]
    public static bool Remove(PlanetFactory __instance, int id)
    {
        if (!Multiplayer.IsActive) return true;
        if (id <= 0 || __instance.ruinPool == null || id >= __instance.ruinCursor || id >= __instance.ruinPool.Length) return false;
        if (Multiplayer.Session.IsServer)
        {
            if (__instance.ruinPool[id].id == id) Multiplayer.Session.AuthorityRuntime?.RuinHost?.Retire(__instance.planetId, id);
            return true;
        }
        return Multiplayer.Session.AuthorityRuntime?.ApplyContext.IsActiveOnCurrentThread == true ||
            Multiplayer.Session.Factories.IsIncomingRequest.Value;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(RuinData), nameof(RuinData.UpdateLifeTime))]
    public static bool Lifetime(ref bool __result)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;
        // Expiry and permanent pit removal are both published by the host.
        __result = true; return false;
    }
}
