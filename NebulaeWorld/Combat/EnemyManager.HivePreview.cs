using System;
using System.Collections.Generic;
using HarmonyLib;
using NebulaeModel.Packets.Universe;

namespace NebulaeWorld.Combat;

internal partial class EnemyManager
{
    private readonly Dictionary<ushort, int> pendingHivePreviews = new();
    private readonly Dictionary<ushort, int> hivePreviewStars = new();
    private readonly HashSet<int> localHivePreviews = new();
    private readonly HashSet<int> remoteHivePreviews = new();
    private readonly HashSet<int> desiredHivePreviews = new();
    private readonly List<int> closedHivePreviews = new();
    private int clientHivePreviewStar;
    private static readonly Action<EnemyDFHiveSystem> instantiateHivePreview =
        AccessTools.MethodDelegate<Action<EnemyDFHiveSystem>>(AccessTools.Method(typeof(EnemyDFHiveSystem), "InstantiateEnemies"));
    private static readonly Action<EnemyDFHiveSystem> uninstantiateHivePreview =
        AccessTools.MethodDelegate<Action<EnemyDFHiveSystem>>(AccessTools.Method(typeof(EnemyDFHiveSystem), "UninstantiateEnemies"));

    public void TickClientHivePreview()
    {
        var star = UIGame.viewMode == EViewMode.Starmap ? GameMain.data?.starmapViewStar?.id ?? 0 : 0;
        if (star == clientHivePreviewStar) return;
        clientHivePreviewStar = star;
        Multiplayer.Session.Network.SendPacket(new HivePreviewRequest { StarId = star });
    }

    public void QueueHivePreview(ushort playerId, int starId)
    {
        lock (pendingHivePreviews) pendingHivePreviews[playerId] = starId;
    }

    public void TickHivePreviews(SpaceSector sector)
    {
        if (sector?.dfHives == null) return;
        lock (pendingHivePreviews)
        {
            foreach (var request in pendingHivePreviews)
                if (request.Value == 0) hivePreviewStars.Remove(request.Key);
                else if (sector.galaxy.StarById(request.Value) != null) hivePreviewStars[request.Key] = request.Value;
            pendingHivePreviews.Clear();
        }
        desiredHivePreviews.Clear();
        foreach (var starId in hivePreviewStars.Values)
        {
            var star = sector.galaxy.StarById(starId);
            if (star == null) continue;
            for (var hive = sector.dfHives[star.index]; hive != null; hive = hive.nextSibling)
            {
                if (!desiredHivePreviews.Add(hive.hiveAstroId)) continue;
                if (!hive.realized)
                {
                    if (!hive.isPreview && !hive.isEmpty) instantiateHivePreview(hive);
                    hive.isPreview = true;
                }
                remoteHivePreviews.Add(hive.hiveAstroId);
            }
        }
        closedHivePreviews.Clear();
        foreach (var astro in remoteHivePreviews)
        {
            if (desiredHivePreviews.Contains(astro)) continue;
            closedHivePreviews.Add(astro);
            var hive = sector.GetHiveByAstroId(astro);
            if (hive == null || localHivePreviews.Contains(astro)) continue;
            if (!hive.realized) uninstantiateHivePreview(hive);
            hive.isPreview = false;
        }
        foreach (var astro in closedHivePreviews) remoteHivePreviews.Remove(astro);
    }

    public void OpenLocalHivePreview(EnemyDFHiveSystem hive)
    {
        localHivePreviews.Add(hive.hiveAstroId);
        if (!hive.isPreview && !hive.realized && !hive.isEmpty) instantiateHivePreview(hive);
        hive.isPreview = true;
    }

    public void CloseLocalHivePreview(EnemyDFHiveSystem hive)
    {
        localHivePreviews.Remove(hive.hiveAstroId);
        if (remoteHivePreviews.Contains(hive.hiveAstroId)) return;
        if (!hive.realized) uninstantiateHivePreview(hive);
        hive.isPreview = false;
    }
}
