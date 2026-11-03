using System;
using NebulaeModel.Logger;

namespace NebulaeWorld.GameStates;

// The same ordered pause protocol as a user pause. Nested AutoSave/SaveCurrentGame
// scopes hold one boundary through native save, companion save and slot rotation.
internal sealed class HostSaveScope : IDisposable
{
    [ThreadStatic] private static int depth;
    private readonly MultiplayerSession session;
    private readonly bool worldPaused, fullscreenPaused;
    private readonly long pauseLimit;
    private readonly double started;
    private bool disposed;

    private HostSaveScope(MultiplayerSession current)
    {
        session = current;
        started = Diagnostics.Now;
        worldPaused = current.WorldPaused;
        pauseLimit = current.WorldPauseStepTick;
        fullscreenPaused = GameMain.isFullscreenPaused;
    }

    internal static HostSaveScope Begin()
    {
        var current = Multiplayer.Session;
        if (current?.IsServer != true || !current.IsGameLoaded || GameMain.data == null || !SaveManager.CanSave) return null;
        if (depth == 0) JoinSyncManager.WaitForSimulation();
        var scope = new HostSaveScope(current);
        if (depth++ != 0) return scope;
        try
        {
            SavePerformance.Begin();
            current.State.SuspendHostRate();
            if (!scope.worldPaused) current.World.HostSetWorldPause(true);
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (--depth != 0) return;
        try
        {
            if (ReferenceEquals(Multiplayer.Session, session))
            {
                // Exclude the synchronous save from the next measured host tick rate.
                session.State.SuspendHostRate();
                if (!worldPaused) session.World.HostSetWorldPause(false);
                else session.WorldPauseStepTick = pauseLimit;
                GameMain.isFullscreenPaused = fullscreenPaused;
            }
        }
        finally { SavePerformance.End(Diagnostics.Now - started); }
    }
}
