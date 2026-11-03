using NebulaeModel.Networking;
#region

using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Factory;
using NebulaeWorld;
using NebulaeWorld.Factory;

#endregion

namespace NebulaePatcher.Patches.Dynamic.Factory;

[HarmonyPatch(typeof(PlayerAction_Build))]
internal class PlayerAction_Build_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlayerAction_Build.DoDismantleObject))]
    public static bool DoDismantleObject_Prefix(PlayerAction_Build __instance, int objId)
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }

        var planetId = Multiplayer.Session.Factories.TargetPlanet != SessionSentinels.PLANET_NONE
            ? Multiplayer.Session.Factories.TargetPlanet
            : __instance.planet?.id ?? -1;

        // If the object is a prebuild, remove it from the prebuild request list
        if (Multiplayer.Session.LocalPlayer.IsHost && objId < 0)
        {
            if (!Multiplayer.Session.Factories.ContainsPrebuildRequest(planetId, -objId))
            {
                Log.Warn(
                    $"DestructFinally was called without having a corresponding PrebuildRequest for the prebuild {-objId} on the planet {planetId}");
                return false;
            }

            Multiplayer.Session.Factories.RemovePrebuildRequest(planetId, -objId);
        }


        if (Multiplayer.Session.LocalPlayer.IsHost || !Multiplayer.Session.Factories.IsIncomingRequest.Value)
        {
            // Replaying a client's request: rebroadcast the author's collection bytes instead of
            // serializing the host's copy once per dismantled building. Otherwise capture once per
            // author version; a burst repeats the same revision.
            var factories = Multiplayer.Session.Factories;
            byte[] collection;
            long revision;
            if (factories.RelayedCollection != null)
            {
                collection = factories.RelayedCollection;
                revision = factories.RelayedCollectionRevision;
            }
            else
            {
                (collection, revision) = Multiplayer.Session.Vegetation.CaptureRequest(
                    NebulaeWorld.Player.VegetationAccess.Collection(GameMain.mainPlayer));
            }
            Multiplayer.Session.Network.SendPacket(new DestructEntityRequest(planetId, objId,
                FactoryManager.GetObjectProtoId(__instance.factory, objId),
                Multiplayer.Session.Factories.PacketAuthor == SessionSentinels.AUTHOR_NONE
                    ? Multiplayer.Session.LocalPlayer.Id
                    : Multiplayer.Session.Factories.PacketAuthor)
            { CollectionBefore = collection, CollectionRevision = revision });
        }

        return Multiplayer.Session.LocalPlayer.IsHost || Multiplayer.Session.Factories.IsIncomingRequest.Value;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlayerAction_Build.SetFactoryReferences))]
    public static bool SetFactoryReferences_Prefix()
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }

        return !Multiplayer.Session.Factories.IsIncomingRequest.Value ||
               Multiplayer.Session.Factories.PacketAuthor == Multiplayer.Session.LocalPlayer.Id ||
               Multiplayer.Session.Factories.TargetPlanet == GameMain.localPlanet?.id;
    }
}
