#region

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Planet;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(PlanetModelingManager))]
internal class PlanetModelingManager_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetModelingManager.RequestLoadPlanetFactory))]
    public static bool RequestLoadPlanetFactory_Prefix(PlanetData planet)
    {
        // Run the original method if this is the master client or in single player games
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return true;
        }

        // Check to make sure it's not already loaded
        if (planet.factoryLoaded || planet.factoryLoading)
        {
            return false;
        }

        // if client is still in lobby we dont need the factory data at all, so dont request it.
        if (Multiplayer.Session.IsInLobby)
        {
            return false;
        }

        var manager = Multiplayer.Session.Planets;
        if (manager.Loads.Contains(planet.id))
        {
            manager.TryActivateFactory(planet);
            return false;
        }
        planet.factoryLoading = true;
        var begin = manager.Loads.Begin(planet.id, planet.factory == null);
        manager.BeginLoad(planet.id);

        // do this here to match the patch in GPUInstancingManager_Patch.cs
        // as we sync entity placement in realtime when players change something
        // we only need to request the full factory if we never received it before
        if (planet.factory != null)
        {
            manager.EnqueueFactory(planet);
            return false;
        }
        // Request factory
        if (begin)
        {
            Multiplayer.Session.Network.SendPacket(new FactoryLoadRequest(planet.id));
        }

        // Skip running the actual method
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetModelingManager.LoadingPlanetFactoryMain))]
    public static bool LoadingFactory_Prefix(PlanetData planet, out long __state)
    {
        __state = NebulaeModel.DataStructures.LoadingTimings.Now;
        if (!Multiplayer.IsActive || Multiplayer.Session.IsServer || NebulaeWorld.Planet.PlanetManager.LoadingStage == 0) return true;
        Multiplayer.Session.Planets.ModelLoader.Step(planet,
            new NebulaeModel.DataStructures.WorkBudget(Multiplayer.Session.JoinSync.CanInteract && Multiplayer.Session.IsGameLoaded ? 4 : 12));
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlanetModelingManager.LoadingPlanetFactoryMain))]
    public static void LoadingFactory_Postfix(PlanetData planet, long __state)
    {
        if (!Multiplayer.IsActive) return;
        Multiplayer.Session.Planets.Timings.Record("factory-step", NebulaeModel.DataStructures.LoadingTimings.Elapsed(__state));
        if (planet.factoryLoaded) Multiplayer.Session.Planets.CompleteFactory(planet);
    }

    [HarmonyFinalizer]
    [HarmonyPatch(nameof(PlanetModelingManager.LoadingPlanetFactoryMain))]
    public static Exception LoadingFactory_Finalizer(PlanetData planet, Exception __exception)
    {
        if (Multiplayer.IsActive && __exception != null)
            Multiplayer.Session.Planets.FailFactory(planet, NebulaeWorld.Planet.PlanetManager.LoadingStage, __exception);
        // Keep vanilla's exception cleanup, which unloads the failed display and clears the loader.
        return __exception;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetModelingManager.RequestLoadPlanet))]
    public static bool RequestLoadPlanet_Prefix(PlanetData planet)
    {
        // NOTE: This does not appear to ever be called in the game code, but just in case, let's override it
        // RequestLoadStar takes care of these instead currently

        // Run the original method if this is the master client or in single player games
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return true;
        }

        InternalLoadPlanetsRequestGenerator(new[] { planet });

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetModelingManager.RequestLoadStar))]
    public static bool RequestLoadStar_Prefix(StarData star)
    {
        // Run the original method if this is the master client or in single player games
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return true;
        }

        InternalLoadPlanetsRequestGenerator(star.planets);

        Multiplayer.Session.DysonSpheres.UnloadRemoteDysonSpheres();
        // Request initial dysonSphere data
        if (GameMain.data.dysonSpheres[star.index] == null)
        {
            Multiplayer.Session.DysonSpheres.RequestDysonSphere(star.index, false);
        }

        return false;
    }

    private static void InternalLoadPlanetsRequestGenerator(IEnumerable<PlanetData> planetsToLoad)
    {
        var localPlanetId = Multiplayer.Session.LocalPlayer?.Data?.LocalPlanetId ?? -1;
        if (localPlanetId == -1) localPlanetId = GameMain.galaxy.birthPlanetId;
        // A space rejoin has no factory dependency. Ground clients can download their
        // factory before OnActivePlanetLoaded, overlapping the two expensive paths.
        var seatPlanet = Multiplayer.Session.LocalPlayer?.Data?.LocalPlanetId ?? -1;
        if (Multiplayer.Session.JoinSync.LocalPending && seatPlanet > 0)
            Multiplayer.Session.Planets.PrefetchFactory(GameMain.galaxy.PlanetById(seatPlanet));
        lock (PlanetModelingManager.genPlanetReqList)
        {
            var planetsToRequest = new List<int>();

            foreach (var planet in planetsToLoad.OrderBy(p => p.id == localPlanetId ? 0 : 1))
            {
                planet.wanted = true;
                if (planet.loaded || planet.loading)
                {
                    continue;
                }

                planet.loading = true;
                if (!Multiplayer.Session.Planets.IsPristine(planet))
                {
                    Multiplayer.Session.Planets.BeginTerrainLoad(planet.id);
                    planetsToRequest.Add(planet.id);
                }
                // Generate the deterministic base immediately, in parallel with runtime synchronization.
                PlanetModelingManager.genPlanetReqList.Enqueue(planet);
            }

            if (planetsToRequest.Count == 0)
            {
                return;
            }
            // Make local planet load first
            if (planetsToRequest.Remove(localPlanetId))
            {
                planetsToRequest.Insert(0, localPlanetId);
            }
            Multiplayer.Session.Network.SendPacket(new PlanetDataRequest(planetsToRequest.ToArray()));
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetModelingManager.RequestScanPlanet))]
    public static bool RequestScanPlanet_Prefix(PlanetData planet)
    {
        // Run the original method if this is the master client or in single player games
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return true;
        }

        if (Multiplayer.Session.Planets.IsPristine(planet)) return true;
        RequestScanPlanet(planet);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlanetModelingManager.RequestScanStar))]
    public static bool RequestScanStar_Prefix(StarData star)
    {
        // Run the original method if this is the master client or in single player games
        if (!Multiplayer.IsActive || Multiplayer.Session.LocalPlayer.IsHost)
        {
            return true;
        }

        foreach (var planet in star.planets)
        {
            PlanetModelingManager.RequestScanPlanet(planet);
        }
        return false;
    }

    private static void RequestScanPlanet(PlanetData planet)
    {
        if (planet.scanned || planet.scanning || planet.data != null)
        {
            return;
        }
        if (planet.loaded || planet.loading)
        {
            return;
        }
        planet.scanning = true;
        Multiplayer.Session.Network.SendPacket(new PlanetDetailRequest(planet.id));
    }

    [HarmonyPrefix, HarmonyPatch(nameof(PlanetModelingManager.ModelingPlanetMain))]
    public static bool ModelingPlanetMain_Prefix(PlanetData planet) => !Multiplayer.IsActive ||
        Multiplayer.Session.IsServer || Multiplayer.Session.Planets.PrepareModeling(planet);
}
