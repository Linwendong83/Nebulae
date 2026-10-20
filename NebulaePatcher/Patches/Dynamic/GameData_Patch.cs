#region

using System;
using System.IO;
using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Logistics;
using NebulaeModel.Packets.Players;
using NebulaeWorld;
using NebulaeWorld.Planet;
using UnityEngine;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(GameData))]
internal class GameData_Patch
{
    [HarmonyPostfix, HarmonyPatch(nameof(GameData.GetOrCreateFactory))]
    public static void FactoryPresence_Postfix(PlanetFactory __result)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsServer && Multiplayer.Session.IsGameLoaded && __result != null)
            Multiplayer.Session.Planets.PublishFactoryPresence();
    }
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GameData.GetOrCreateFactory))]
    public static bool GetOrCreateFactory_Prefix(GameData __instance, ref PlanetFactory __result, PlanetData planet)
    {
        // We want the original method to run on the host client or in single player games
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return true;
        }

        // Get the recieved bytes from the remote server that we will import
        if (!Multiplayer.Session.Planets.PendingFactories.TryGetValue(planet.id, out var factoryBytes))
        {
            if (planet.factory == null) throw new InvalidOperationException($"No authoritative factory snapshot for planet {planet.id}");
            return true;
        }

        var manager = Multiplayer.Session.Planets;
        manager.Loads.SetPhase(planet.id, NebulaeModel.DataStructures.FactoryLoadPhase.Importing);
        var previous = planet.factory;
        var previousIndex = planet.factoryIndex;
        var previousCount = __instance.factoryCount;
        var factoryIndex = previous == null ? previousCount : previousIndex;
        var imported = new PlanetFactory();
        try
        {
            planet.factoryIndex = factoryIndex;
            __instance.factories[factoryIndex] = imported;
            if (previous == null) __instance.factoryCount++;
            using var reader = new BinaryUtils.Reader(factoryBytes);
            var importStarted = NebulaeModel.DataStructures.LoadingTimings.Now;
            imported.Import(factoryIndex, __instance, reader.BinaryReader.BaseStream, reader.BinaryReader);
            manager.Timings.Record("factory-import", NebulaeModel.DataStructures.LoadingTimings.Elapsed(importStarted));
            planet.factory = imported;
            if (manager.PendingBuildAssignments.TryGetValue(planet.id, out var assignments))
                Multiplayer.Session.BuildDispatch.ImportSnapshot(imported, assignments);
            // Initial FactoryProductionStat for other in-game stats checking functions
            if (GameMain.statistics.production.factoryStatPool[factoryIndex] == null)
            {
                GameMain.statistics.production.factoryStatPool[factoryIndex] = new FactoryProductionStat();
                GameMain.statistics.production.factoryStatPool[factoryIndex].Init();
                //Skip the part of setting firstCreateIds
            }
            __instance.galaxy.astrosFactory[planet.id] = imported;
            __result = imported;
            manager.PendingFactories.Remove(planet.id);
            manager.PendingBuildAssignments.Remove(planet.id);
            manager.Loads.SetPhase(planet.id, NebulaeModel.DataStructures.FactoryLoadPhase.Modeling);
        }
        catch
        {
            // Do not leave a failed import registered as a cached, usable factory.
            __instance.factories[factoryIndex] = previous;
            __instance.factoryCount = previousCount;
            planet.factory = previous;
            planet.factoryIndex = previousIndex;
            __instance.galaxy.astrosFactory[planet.id] = previous;
            throw;
        }

        // Do not run the original method
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(GameData.OnActivePlanetLoaded))]
    public static bool OnActivePlanetLoaded_Prefix(GameData __instance, PlanetData planet)
    {
        // NOTE: this is part of the weird planet movement fix, see ArrivePlanet() patch for more information

        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return true;
        }
        if (planet != null && planet == __instance.localPlanet)
        {
            if (planet.factoryLoaded)
            {
                __instance.OnActivePlanetFactoryLoaded(planet);
            }
            else
            {
                // triggers data request to server
                planet.onFactoryLoaded -= __instance.OnActivePlanetFactoryLoaded;
                planet.onFactoryLoaded += __instance.OnActivePlanetFactoryLoaded;
                planet.LoadFactory();
                // lets player walk on empty planet without awkward graphic glitches
                if (planet.physics == null)
                {
                    planet.physics = new PlanetPhysics(planet);
                    planet.physics.Init();
                }
            }
            RefreshMissingMeshes();
        }
        if (planet != null)
        {
            planet.onLoaded -= __instance.OnActivePlanetLoaded;
        }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(GameData.OnActivePlanetFactoryLoaded))]
    public static bool OnActivePlanetFactoryLoaded_Prefix(GameData __instance, PlanetData planet)
    {
        if (!Multiplayer.IsActive)
        {
            return true;
        }
        RefreshMissingMeshes();
        if (Multiplayer.Session.LocalPlayer.IsHost)
        {
            return true;
        }
        if (planet != null && planet == __instance.localPlanet)
        {
            if (GameMain.gameTick == 0L && DSPGame.SkipPrologue)
            {
                InitLandingPlace(__instance, planet);
            }

            planet.onFactoryLoaded -= __instance.OnActivePlanetFactoryLoaded;
            Multiplayer.Session.Planets.ScheduleMeshRepair(planet);

            Multiplayer.Session.Trashes.Refresh();
            Multiplayer.Session.BuildDispatch.RefreshFactoryQueues(planet.factory);
            Multiplayer.Session.Combat.OnFactoryLoadFinished(planet.factory);
            Multiplayer.Session.Enemies.OnFactoryLoadFinished(planet.factory);

            // Rebuild markerCursor and recycle list so MarkerRenderer iterates pre-existing beacons.
            // Same pattern as galacticTransport.Arragement() below (and vanilla GameData.Import line 936).
            GameMain.data.galacticDigital?.Arragement();

            if (PlanetManager.PreservedDashboardData != null && PlanetManager.PreservedDashboardData.Length > 0 && GameMain.data?.statistics?.charts != null)
            {
                try
                {
                    using var ms = new MemoryStream(PlanetManager.PreservedDashboardData);
                    using var reader = new BinaryReader(ms);
                    GameMain.data.statistics.charts.Import(reader);
                    PlanetManager.PreservedDashboardData = null;
                }
                catch (Exception e)
                {
                    Log.Warn($"Failed to restore preserved dashboard: {e}");
                }
            }
        }

        // call this here as it would not be called normally on the client, but its needed to set GameMain.data.galacticTransport.stationCursor
        // Arragement() updates galacticTransport.stationCursor
        // galacticTransport.shipRenderer.Update() can then update galacticTransport.shipRenderer.shipCount
        // galacticTransport.shipRenderer.Draw() can then render ships
        GameMain.data.galacticTransport.Arragement();
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(GameData.SetForNewGame))]
    public static void SetForNewGame_Postfix(GameData __instance)
    {
        // Restore the server-assigned birth point or the returning player's saved location.
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return;
        }

        //Update player's position before searching for closest planet (GS2: Modeler.ModelingCoroutine)
        Multiplayer.Session.State.ApplyBirthData(__instance);
        __instance.mainPlayer.uPosition = new VectorLF3(Multiplayer.Session.LocalPlayer.Data.UPosition.x,
            Multiplayer.Session.LocalPlayer.Data.UPosition.y, Multiplayer.Session.LocalPlayer.Data.UPosition.z);

        if (Multiplayer.Session.LocalPlayer.Data.LocalPlanetId != -1)
        {
            var planet = __instance.galaxy.PlanetById(Multiplayer.Session.LocalPlayer.Data.LocalPlanetId);
            __instance.ArrivePlanet(planet);
        }
        else
        {
            StarData nearestStar = null;
            PlanetData nearestPlanet = null;
            GameMain.data.GetNearestStarPlanet(ref nearestStar, ref nearestPlanet);
            if (nearestStar == null)
            {
                // We are not in a planetary system and thus do not have a star, return.
                return;
            }

            __instance.ArriveStar(nearestStar);
        }
    }

    [HarmonyPostfix, HarmonyPriority(Priority.High)]
    [HarmonyPatch(nameof(GameData.NewGame))]
    public static void NewGame_Postfix(GameData __instance)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost) return;

        // Overwrite from binaryData in GlobalGameDataResponse
        var start = NebulaeModel.DataStructures.LoadingTimings.Now;
        Multiplayer.Session.State.OverwriteGlobalGameData(__instance);
        Multiplayer.Session.Planets.Timings.Record("world-import", NebulaeModel.DataStructures.LoadingTimings.Elapsed(start));
    }





    private static void InitLandingPlace(GameData gameData, PlanetData planet)
    {
        var birthPoint = planet.birthPoint;
        var quaternion = Maths.SphericalRotation(birthPoint, 0f);
        gameData.mainPlayer.transform.localPosition = birthPoint;
        gameData.mainPlayer.transform.localRotation = quaternion;
        gameData.mainPlayer.transform.localScale = Vector3.one;
        gameData.mainPlayer.uPosition = (Vector3)planet.uPosition + planet.runtimeRotation * birthPoint;
        gameData.mainPlayer.uRotation = planet.runtimeRotation * quaternion;
        gameData.mainPlayer.uVelocity = VectorLF3.zero;
        gameData.mainPlayer.controller.velocityOnLanding = Vector3.zero;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(GameData.LeaveStar))]
    public static void LeaveStar_Prefix()
    {
        //Client should unload all factories once they leave the star system
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return;
        }
        PlanetManager.UnloadAllFactories();
        if (!Multiplayer.Session.IsInLobby)
        {
            Multiplayer.Session.Network.SendPacket(new PlayerUpdateLocalStarId(Multiplayer.Session.LocalPlayer.Id, -1));
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(GameData.ArriveStar))]
    public static void ArriveStar_Postfix(StarData star)
    {
        // ArriveStar calls LeaveStar internally. Publish the destination only after that
        // departure; otherwise its -1 packet overwrites the new star on the host.
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost || Multiplayer.Session.IsInLobby || star == null)
        {
            return;
        }
        Multiplayer.Session.Network.SendPacket(new PlayerUpdateLocalStarId(Multiplayer.Session.LocalPlayer.Id, star.id));
        if (Multiplayer.Session.JoinSync.CanInteract)
            Multiplayer.Session.Network.SendPacket(new ILSArriveStarPlanetRequest(star.id));
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(GameData.LeavePlanet))]
    public static void LeavePlanet_Prefix()
    {
        //Players should clear the list of drone orders of other players when they leave the planet
        if (Multiplayer.IsActive)
        {
            Multiplayer.Session.Planets.CancelFactory(GameMain.localPlanet);
            Multiplayer.Session.BuildDispatch.AbortLocalConstructionDrones(GameMain.localPlanet?.factory);
            Multiplayer.Session.BuildDispatch.ReleaseLocalPlayerTargets(GameMain.localPlanet?.id ?? -1);
            Multiplayer.Session.Trashes.Refresh();
            Multiplayer.Session.PowerTowers.ClearLocalState();
            Multiplayer.Session.Enemies.OnLeavePlanet();
        }
    }

    private static void RefreshMissingMeshes()
    {
        PlanetManager.RefreshMeshes(GameMain.localPlanet);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(GameData.CreateDysonSphere))]
    public static bool CreateDysonSphere_Prefix(GameData __instance, int starIndex, ref DysonSphere __result)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer) return true;


        if ((ulong)starIndex >= (ulong)((long)__instance.galaxy.starCount))
        {
            __result = null;
            return false;
        }
        if (__instance.dysonSpheres[starIndex] != null)
        {
            __result = __instance.dysonSpheres[starIndex];
            return false;
        }

        // Create a dummy dyson sphere and prevent sending packets
        Multiplayer.Session.DysonSpheres.InBlueprint = true;
        __instance.dysonSpheres[starIndex] = new DysonSphere();
        __instance.dysonSpheres[starIndex].Init(__instance, __instance.galaxy.stars[starIndex]);
        __instance.dysonSpheres[starIndex].ResetNew();
        Multiplayer.Session.DysonSpheres.InBlueprint = false;

        if (Multiplayer.Session.DysonSpheres.RequestingIndex == -1)
        {
            // If client is not requesting yet, request the target sphere from server
            Multiplayer.Session.DysonSpheres.RequestDysonSphere(starIndex, false);
        }
        __result = __instance.dysonSpheres[starIndex];
        return false;
    }
}
