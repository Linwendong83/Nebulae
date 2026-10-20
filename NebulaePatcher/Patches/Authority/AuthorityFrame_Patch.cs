#region

using System;
using System.Threading;
using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Logger;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch(typeof(ThreadManager))]
internal class AuthorityFrame_Patch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(ThreadManager.ProcessFrame))]
    public static void ProcessFrame_Prefix(ThreadManager __instance)
    {
        var session = Multiplayer.Session;
        if (session is null || !Multiplayer.IsActive) return;
        session.FactoryProducts.CollectRefunds ??= FactoryRefunds.Collect;
        session.FactoryProducts.CollectLabRefunds ??= FactoryRefunds.CollectLab;

        var runtime = session.AuthorityRuntime;
        if (runtime is null || !runtime.Identity.IsActive) return;
        if (!__instance.initialized || __instance.frameBarrier == null) return;

        // Entry is before SignalAndWait, not after it. Signalling here would release workers to
        // mutate pools concurrently with replica installation. Wait for their arrivals instead.
        var spins = 0;
        while (__instance.frameBarrier.remainingParticipantCount > 1)
        {
            Thread.SpinWait(32);
            if (++spins % 32 == 0) Thread.Yield();
        }

        var hostTick = GameMain.gameTick;
        try
        {
            NebulaeWorld.Combat.NativeEnemySimulation.BeginFrame();
            // Every worker has arrived and cannot leave until vanilla signals the main participant.
            if (session.IsServer) session.Ships.FlushLifecycle();
            runtime.OnFrameComplete(hostTick, __instance.frameCounter > 0 ? __instance.frameCounter : Math.Max(0, hostTick - 1));

            // Now admit the messages that arrived while the previous frame ran. Both queues were
            // filled by the socket thread, which never touches the world itself.
            runtime.OnFrameBoundary(hostTick);

            // the standing subscription set follows where the client actually is. The context
            // is the client's own local view — the same observation the standing-set policy is
            // defined over — and a change of view is what issues the subscribe/unsubscribe delta.
            // The host re-checks every request against its registry, so this only plans delivery.
            if (!session.IsServer && GameMain.data != null && session.JoinSync?.CanSubscribe != false)
            {
                session.CombatAuthority.SendSettings(hostTick);
                runtime.UpdateStandingSubscriptions(
                    GameMain.localPlanet != null ? GameMain.localPlanet.id : 0,
                    isInSector: GameMain.localPlanet == null, supportsPool: HasReplicaAdapter,
                    currentStarId: GameMain.localStar?.id ?? 0);
                var factory = GameMain.localPlanet?.factory;
                if (session.IsGameLoaded && !GameMain.isPaused)
                {
                    NebulaeWorld.Combat.NativeEnemySimulation.Prepare(factory);
                    NebulaeWorld.Combat.NativeEnemySimulation.Prepare(GameMain.spaceSector);
                    NebulaeWorld.Combat.NativeCraftSimulation.Prepare(factory);
                    NebulaeWorld.Combat.NativeCraftSimulation.Prepare(GameMain.spaceSector);
                    runtime.AuditRuntime(hostTick);
                }
            }
        }
        catch (Exception e)
        {
            Log.Error("[authority] frame boundary failed; the frame continues unchanged", e);
        }
    }

    // These are the sources registered by MultiplayerSession. Construction/resource pools still
    // use their native protocols; subscribing them without an adapter strands the client forever.
    private static bool HasReplicaAdapter(PoolKind kind) => kind == PoolKind.Entity || kind == PoolKind.Turret ||
        kind == PoolKind.GroundEnemy || kind == PoolKind.GroundCraft ||
        kind == PoolKind.SpaceEnemy || kind == PoolKind.SpaceCraft ||
        kind == PoolKind.Base || kind == PoolKind.HiveSummary;
}
