using System;
using System.Collections.Generic;
using HarmonyLib;
using NebulaeModel.Packets.Universe;
using NebulaeModel.Authority;

namespace NebulaeWorld.Combat;

internal partial class EnemyManager
{
    private readonly Dictionary<ushort, (int Star, int Target)> pendingHivePreviews = new();
    private readonly Dictionary<ushort, int> navigationTargets = new();
    private readonly HashSet<int> globalEnemyTargets = new();
    private readonly HashSet<ScopeKey> clientHiveScopes = new(), desiredHiveScopes = new();
    private readonly List<ScopeKey> retiredHiveScopes = new();
    private int clientNavigationTarget;
    private AuthorityEpoch clientViewEpoch;
    public bool HiveDetailsReady
    {
        get
        {
            var replica = Multiplayer.Session?.AuthorityRuntime?.WorldReplica;
            foreach (var scope in clientHiveScopes)
                if (replica == null || !replica.Versions.TryGetScope(scope, out var state) || state.Phase != SubscriptionPhase.Live) return false;
            return true;
        }
    }
    public int ViewedStarFor(ushort player) => hivePreviewStars.TryGetValue(player, out var star) ? star : 0;
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
        var runtime = Multiplayer.Session.AuthorityRuntime;
        if (runtime?.WorldReplica == null) return;
        if (clientViewEpoch != runtime.Identity.Epoch)
        {
            clientViewEpoch = runtime.Identity.Epoch;
            clientHiveScopes.Clear();
            clientHivePreviewStar = clientNavigationTarget = -1;
        }
        var star = UIGame.viewMode == EViewMode.Starmap ? GameMain.data?.starmapViewStar?.id ?? 0 : 0;
        var target = GameMain.mainPlayer?.navigation?.indicatorEnemyId ?? 0;
        if (star != clientHivePreviewStar || target != clientNavigationTarget)
        {
            clientHivePreviewStar = star; clientNavigationTarget = target;
            Multiplayer.Session.Network.SendPacket(new HivePreviewRequest { StarId = star, TargetEnemyId = target });
        }
        if (Multiplayer.Session.JoinSync?.CanSubscribe == false) return;
        desiredHiveScopes.Clear();
        AddHiveScopes(GameMain.localStar?.id ?? 0);
        AddHiveScopes(star);
        retiredHiveScopes.Clear();
        foreach (var scope in clientHiveScopes) if (!desiredHiveScopes.Contains(scope)) retiredHiveScopes.Add(scope);
        foreach (var scope in retiredHiveScopes) { runtime.TryUnsubscribeScope(scope); clientHiveScopes.Remove(scope); }
        foreach (var scope in desiredHiveScopes)
            if (clientHiveScopes.Add(scope)) runtime.TrySubscribeScope(scope);
        // A directory can arrive while an earlier details request was unreadable. Use the normal
        // recovery lease for existing scopes instead of issuing new baselines each render frame.
    }

    private void AddHiveScopes(int starId)
    {
        var star = GameMain.galaxy?.StarById(starId);
        if (star == null || GameMain.spaceSector?.dfHives == null) return;
        for (var hive = GameMain.spaceSector.dfHives[star.index]; hive != null; hive = hive.nextSibling)
            desiredHiveScopes.Add(new ScopeKey(PoolKind.Hive, hive.hiveAstroId));
    }

    public void QueueHivePreview(ushort playerId, int starId, int targetEnemyId = 0)
    {
        lock (pendingHivePreviews) pendingHivePreviews[playerId] = (starId, targetEnemyId);
    }

    public void TickHivePreviews(SpaceSector sector)
    {
        if (sector?.dfHives == null) return;
        lock (pendingHivePreviews)
        {
            foreach (var request in pendingHivePreviews)
            {
                if (request.Value.Star == 0) hivePreviewStars.Remove(request.Key);
                else if (sector.galaxy.StarById(request.Value.Star) != null) hivePreviewStars[request.Key] = request.Value.Star;
                var target = request.Value.Target;
                if (target > 0 && target < sector.enemyCursor && sector.enemyPool[target].id == target)
                    navigationTargets[request.Key] = target;
                else navigationTargets.Remove(request.Key);
            }
            pendingHivePreviews.Clear();
        }
        UpdateGlobalEnemyTargets();
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

    public void UpdateGlobalEnemyTargets()
    {
        globalEnemyTargets.Clear();
        foreach (var id in navigationTargets.Values) globalEnemyTargets.Add(id);
        // Retain only the handful of objects named by a live native warning; distant relay pools
        // are otherwise absent. This preserves native warning lines and expiry unchanged.
        if (GameMain.data?.warningSystem?.broadcasts is { } warnings)
            foreach (var warning in warnings)
                if (warning.vocal is EBroadcastVocal.LandingRelay or EBroadcastVocal.ApproachingSeed)
                    globalEnemyTargets.Add(warning.context);
        Multiplayer.Session?.AuthorityRuntime?.SpaceEnemyHost?.SetGlobalTargets(globalEnemyTargets);
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
