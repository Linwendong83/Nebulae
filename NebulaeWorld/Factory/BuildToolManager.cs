using NebulaeModel.Networking;
#region

using System;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Factory;
using NebulaeWorld.Warning;
using UnityEngine;

#endregion

namespace NebulaeWorld.Factory;

internal class BuildToolManager : IDisposable
{
    private const long WAIT_TIME = 10000;
    private long LastCheckTime;
    private Vector3 LastPosition;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public void CreatePrebuildsRequest(CreatePrebuildsRequest packet)
    {
        var planet = GameMain.galaxy.PlanetById(packet.PlanetId);
        if (planet.factory == null)
        {
            if (Multiplayer.Session.Factories.IsIncomingRequest.Value)
            {
                // We only execute the code if the client has loaded the factory at least once.
                // Else it will get it once it goes to the planet for the first time. 
                return;
            }
            Log.Warn("planet.factory was null create new one");
            planet.factory = GameMain.data.GetOrCreateFactory(planet);
        }

        var pab = GameMain.mainPlayer.controller != null ? GameMain.mainPlayer.controller.actionBuild : null;
        if (pab == null)
        {
            return;
        }
        var buildTools = pab.tools;
        var buildTool = buildTools.FirstOrDefault(t => t.GetType().ToString() == packet.BuildToolType);

        if (buildTool == null)
        {
            return;
        }
        using var context = new FactoryOperationScope(pab, buildTool);
        Multiplayer.Session.Factories.TargetPlanet = packet.PlanetId;
        Multiplayer.Session.Factories.PacketAuthor = packet.AuthorId;

        PlanetFactory tmpFactory = null;
        NearColliderLogic tmpNearcdLogic = null;
        PlanetPhysics tmpPlanetPhysics = null;
        var loadExternalPlanetData = GameMain.localPlanet?.id != planet.id;

        if (loadExternalPlanetData)
        {
            //Make backup of values that are overwritten
            tmpFactory = buildTool.factory;
            tmpNearcdLogic = buildTool.actionBuild.nearcdLogic;
            tmpPlanetPhysics = buildTool.actionBuild.planetPhysics;
            Multiplayer.Session.Factories.AddPlanetTimer(packet.PlanetId);
        }

        var incomingBlueprintEvent = packet.BuildToolType == typeof(BuildTool_BlueprintPaste).ToString();
        var pos = Vector3.zero;

        //Create Prebuilds from incoming packet and prepare new position
        var tmpList = new List<BuildPreview>();
        if (!incomingBlueprintEvent)
        {
            tmpList.AddRange(buildTool.buildPreviews);
            buildTool.buildPreviews.Clear();
            if (packet.TryGetBuildPreviews(out var incomingPreviews))
            {
                buildTool.buildPreviews.AddRange(incomingPreviews);
                pos = buildTool.buildPreviews[0].lpos;
            }
        }

        Multiplayer.Session.Factories.EventFactory = planet.factory;

        //Set temporary Local Planet / Factory data that are needed for original methods CheckBuildConditions() and CreatePrebuilds()
        buildTool.factory = planet.factory;
        buildTool.planet = planet;
        pab.factory = planet.factory;
        pab.planet = planet; pab.planetAux = planet.aux;
        pab.noneTool.factory = planet.factory;
        pab.noneTool.planet = planet;
        if (Multiplayer.Session.LocalPlayer.IsHost)
        {
            // Only the server needs to set these
            pab.planetPhysics = planet.physics;
            pab.nearcdLogic = planet.physics.nearColliderLogic;
        }

        //Check if prebuilds can be build (collision check, height check, etc)
        var canBuild = false;
        if (Multiplayer.Session.LocalPlayer.IsHost)
        {
            GameMain.mainPlayer.mecha.buildArea = float.MaxValue;
            canBuild = CheckBuildingConnections(buildTool.buildPreviews, planet.factory.entityPool,
                planet.factory.prebuildPool);
            if (canBuild)
            {
                var previews = incomingBlueprintEvent && packet.TryGetBuildPreviews(out var blueprintPreviews)
                    ? blueprintPreviews : buildTool.buildPreviews;
                canBuild = GeothermalPlacement.Validate(planet.factory, previews);
            }
            if (!canBuild)
            {
                Log.Warn($"CreatePrebuildsRequest: request do not pass connections test on planet {planet.id}");
            }
        }

        if (canBuild || Multiplayer.Session.LocalPlayer.IsClient)
        {
            if (Multiplayer.Session.Factories.IsIncomingRequest.Value)
            {
                CheckAndFixConnections(buildTool, planet);
            }
            if (Multiplayer.Session.LocalPlayer.IsClient)
            {
                if (packet.PrebuildId != Multiplayer.Session.Factories.GetNextPrebuildId(packet.PlanetId))
                {
                    var warningText =
                        string.Format("(Desync) PrebuildId mismatch {0} != {1} on planet {2}. Please reconnect!".Translate(), packet.PrebuildId, Multiplayer.Session.Factories.GetNextPrebuildId(planet.factory), planet.displayName);
                    Log.Warn(warningText);
                    WarningManager.DisplayTemporaryWarning(warningText, 15000);
                    return;
                }
            }
            else
            {
                // Should there be check that the buildPreviews sent by client don't contain outdated objId?
            }

            if (packet.BuildToolType == typeof(BuildTool_Click).ToString())
            {
                ((BuildTool_Click)buildTool).CreatePrebuilds();
            }
            else if (packet.BuildToolType == typeof(BuildTool_Path).ToString())
            {
                ((BuildTool_Path)buildTool).CreatePrebuilds();
            }
            else if (packet.BuildToolType == typeof(BuildTool_Addon).ToString())
            {
                ((BuildTool_Addon)buildTool).CreatePrebuilds();
            }
            else if (packet.BuildToolType == typeof(BuildTool_Inserter).ToString())
            {
                ((BuildTool_Inserter)buildTool).CreatePrebuilds();
            }
            else if (incomingBlueprintEvent)
            {
                // Cache the current data before performing the requested CreatePrebuilds();
                if (buildTool is BuildTool_BlueprintPaste bpTool)
                {
                    var previousCursor = bpTool.bpCursor;
                    var previousPool = bpTool.bpPool;

                    // Perform the requested CreatePrebuilds();
                    if (packet.TryGetBuildPreviews(out var incomingPreviews))
                    {
                        bpTool.bpCursor = incomingPreviews.Count;
                        bpTool.bpPool = incomingPreviews.ToArray();
                        bpTool.CreatePrebuilds();
                        pos = incomingPreviews[0].lpos;
                    }

                    // Revert to previous data
                    bpTool.bpCursor = previousCursor;
                    bpTool.bpPool = previousPool;
                }
            }
        }

        //Revert changes back to the original planet
        if (loadExternalPlanetData)
        {
            buildTool.factory = tmpFactory;
            pab.factory = tmpFactory;
            pab.noneTool.factory = tmpFactory;
            pab.planetPhysics = tmpPlanetPhysics;
            pab.nearcdLogic = tmpNearcdLogic;
        }

        GameMain.mainPlayer.mecha.buildArea = Configs.freeMode.mechaBuildArea;
        Multiplayer.Session.Factories.EventFactory = null;

        if (!incomingBlueprintEvent)
        {
            buildTool.buildPreviews.Clear();
            buildTool.buildPreviews.AddRange(tmpList);
        }

        Multiplayer.Session.Factories.TargetPlanet = SessionSentinels.PLANET_NONE;
        Multiplayer.Session.Factories.PacketAuthor = SessionSentinels.AUTHOR_NONE;

        if (pos == LastPosition)
        {
            //Reset check timer on client
            LastCheckTime = 0;
        }
    }

    private static void CheckAndFixConnections(BuildTool buildTool, PlanetData planet)
    {
        foreach (var preview in buildTool.buildPreviews)
        {
            //Check only, if buildPreview has some connection to another prebuild
            if (preview.coverObjId >= 0)
            {
                continue;
            }
            var tmpVector = preview.lpos;
            if (planet.factory.prebuildPool[-preview.coverObjId].id != 0)
            {
                //Prebuild exists, check if it is same prebuild that client wants by comparing prebuild positions
                if (tmpVector == planet.factory.prebuildPool[-preview.coverObjId].pos)
                {
                    //Position of prebuilds are same, everything is OK.
                    continue;
                }
            }
            // Prebuild does not exists, check what is the new ID of the finished building that was constructed from prebuild
            // or
            // Positions of prebuilds are different, which means this is different prebuild and we need to find ID of contructed building
            foreach (var entity in planet.factory.entityPool)
            {
                // `entity.pos == tmpVector` does not work in every cases (rounding errors?).
                if (!((entity.pos - tmpVector).sqrMagnitude < 0.1f))
                {
                    continue;
                }
                preview.coverObjId = entity.id;
                break;
            }
        }
    }

    private static bool CheckBuildingConnections(List<BuildPreview> buildPreviews, IList<EntityData> entityPool,
        IList<PrebuildData> prebuildPool)
    {
        //Check if some entity that is suppose to be connected to this building is missing
        foreach (var buildPreview in buildPreviews)
        {
            var inputObjId = buildPreview.inputObjId;
            switch (inputObjId)
            {
                case > 0 when inputObjId >= entityPool.Count || entityPool[inputObjId].id == 0:
                    return false;
                case < 0:
                    {
                        inputObjId = -inputObjId;
                        if (inputObjId >= prebuildPool.Count || prebuildPool[inputObjId].id == 0)
                        {
                            return false;
                        }
                        break;
                    }
            }

            var outputObjId = buildPreview.outputObjId;
            switch (outputObjId)
            {
                case > 0 when outputObjId >= entityPool.Count || entityPool[outputObjId].id == 0:
                    return false;
                case < 0:
                    {
                        outputObjId = -outputObjId;
                        if (outputObjId >= prebuildPool.Count || prebuildPool[outputObjId].id == 0)
                        {
                            return false;
                        }
                        break;
                    }
            }
        }
        return true;
    }

    public bool InitialCheck(Vector3 pos)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        if (now - LastCheckTime < WAIT_TIME && LastPosition == pos)
        {
            //Stop client from sending prebuilds at the same position
            UIRealtimeTip.Popup("Please wait for server response".Translate());
            return false;
        }
        LastCheckTime = now;
        LastPosition = pos;
        return true;
    }
}
