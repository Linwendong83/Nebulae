#region

using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Combat;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(SkillAudioLogic))]
internal class SkillAudioLogic_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SkillAudioLogic), nameof(SkillAudioLogic.AddPlayerAudio))]
    public static void AddPlayerAudio_Prefix(int protoId, ref SkillSFXHolder __result)
    {
        if (BattleImpactCapture.SuppressNativeAudio) { __result.multiplier = 0; return; }
        if (!Multiplayer.IsActive) return;
        var playerId = protoId == 405 ? CombatTargetContext.TargetPlayerId() : CombatManager.PlayerId;
        if (playerId == Multiplayer.Session.LocalPlayer.Id) return;

        // Remote player: Set multiplier by distance from the main player
        ref var ptr = ref Multiplayer.Session.Combat.Players[0]; // main player
        if (!Multiplayer.Session.Combat.IndexByPlayerId.TryGetValue(playerId, out var index)) { __result.multiplier = 0; return; }

        if (ptr.planetId > 0 && ptr.planetId == Multiplayer.Session.Combat.Players[index].planetId)
        {
            var dist = (ptr.position - Multiplayer.Session.Combat.Players[index].position).magnitude;
            __result.multiplier = (120f - dist) / 120f;
            if (__result.multiplier < 0)
            {
                __result.multiplier = 0;
            }
        }
        else
        {
            // Mute attacks in space
            __result.multiplier = 0;
        }
    }
}
