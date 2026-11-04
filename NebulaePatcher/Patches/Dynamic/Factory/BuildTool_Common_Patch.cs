using NebulaeModel.Networking;
#region

using System;
using System.Collections.Generic;
using HarmonyLib;
using NebulaeModel.Packets.Factory;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic.Factory;

[HarmonyPatch]
internal class BuildTool_Common_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BuildTool_Click), nameof(BuildTool_Click.CreatePrebuilds))]
    [HarmonyPatch(typeof(BuildTool_Path), nameof(BuildTool_Path.CreatePrebuilds))]
    [HarmonyPatch(typeof(BuildTool_Addon), nameof(BuildTool_Addon.CreatePrebuilds))]
    [HarmonyPatch(typeof(BuildTool_Inserter), nameof(BuildTool_Inserter.CreatePrebuilds))]
    [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CreatePrebuilds))]
    public static bool CreatePrebuilds_Prefix(BuildTool __instance)
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }

        // Host will just broadcast event to other players
        if (Multiplayer.Session.LocalPlayer.IsHost)
        {
            var planetId = Multiplayer.Session.Factories.EventFactory?.planetId ?? GameMain.localPlanet?.id ?? -1;
            var authorId = Multiplayer.Session.Factories.PacketAuthor == SessionSentinels.AUTHOR_NONE
                ? Multiplayer.Session.LocalPlayer.Id
                : Multiplayer.Session.Factories.PacketAuthor;
            var prebuildId = Multiplayer.Session.Factories.GetNextPrebuildId(planetId);
            var toolType = __instance.GetType().ToString();
            // A client's blueprint request is already encoded, and that payload is exactly the list
            // the host is building from, so forward it instead of serializing the blueprint again.
            // Other tools can have their previews fixed up by the host, so only blueprints relay raw.
            var relayed = toolType == typeof(BuildTool_BlueprintPaste).ToString()
                ? Multiplayer.Session.Factories.RelayedPrebuildRequest : null;
            var request = relayed != null && relayed.PlanetId == planetId && relayed.BuildToolType == toolType &&
                relayed.BuildPreviewData is { Length: > 0 } payload
                ? CreatePrebuildsRequest.FromEncodedPayload(planetId, payload, authorId, toolType, prebuildId)
                : new CreatePrebuildsRequest(planetId, PreviewRange(__instance), authorId, toolType, prebuildId);
            Multiplayer.Session.Network.SendPacketToStar(request,
                GameMain.galaxy.PlanetById(planetId).star.id);
        }

        //If client builds, he need to first send request to the host and wait for reply
        if (Multiplayer.Session.LocalPlayer.IsHost || Multiplayer.Session.Factories.IsIncomingRequest.Value)
        {
            return true;
        }
        {
            var previews = PreviewRange(__instance);
            if (!Multiplayer.Session.BuildTools.InitialCheck(previews[0].lpos))
            {
                return false;
            }
            var authorId = Multiplayer.Session.Factories.PacketAuthor == SessionSentinels.AUTHOR_NONE
                ? Multiplayer.Session.LocalPlayer.Id
                : Multiplayer.Session.Factories.PacketAuthor;
            Multiplayer.Session.Network.SendPacket(new CreatePrebuildsRequest(GameMain.localPlanet?.id ?? -1, previews,
                authorId, __instance.GetType().ToString(), -1));
            return false;
        }
    }

    // Blueprint pools retain inactive capacity. Serialize the active range directly, and do
    // not materialize it at all when applying a host event or forwarding encoded previews.
    internal static IList<BuildPreview> PreviewRange(BuildTool tool) => tool is BuildTool_BlueprintPaste blueprint
        ? new ArraySegment<BuildPreview>(blueprint.bpPool, 0, blueprint.bpCursor)
        : tool.buildPreviews;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BuildTool_Click), nameof(BuildTool_Click.CheckBuildConditions))]
    [HarmonyPatch(typeof(BuildTool_Path), nameof(BuildTool_Path.CheckBuildConditions))]
    [HarmonyPatch(typeof(BuildTool_Addon), nameof(BuildTool_Addon.CheckBuildConditions))]
    [HarmonyPatch(typeof(BuildTool_Inserter), nameof(BuildTool_Inserter.CheckBuildConditions))]
    [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CheckBuildConditions))]
    public static bool CheckBuildConditions(ref bool __result)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.Factories.IsIncomingRequest.Value)
        {
            return true;
        }
        __result = true;
        return false;
    }
}
