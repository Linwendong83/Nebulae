namespace NebulaeWorld.Factory;

/// <summary>Player drones in remote pools share the same death boundary as native local drones.</summary>
internal static class ConstructionDroneLife
{
    public static bool IsDead(ushort playerId)
    {
        var session = Multiplayer.Session;
        if (session == null) return false;
        if (playerId == session.LocalPlayer?.Id) return GameMain.mainPlayer?.isAlive == false;
        if (session.World == null) return false;
        using (session.World.GetRemotePlayersModels(out var models))
            return models.TryGetValue(playerId, out var model) && model.PlayerInstance?.isAlive == false;
    }

    public static void PlayerDied(Mecha mecha)
    {
        var session = Multiplayer.Session;
        ushort playerId = 0;
        if (mecha.player == GameMain.mainPlayer) playerId = session.LocalPlayer.Id;
        else if (session.World != null)
        {
            using (session.World.GetRemotePlayersModels(out var models))
                foreach (var model in models.Values)
                    if (model.PlayerInstance == mecha.player) { playerId = model.PlayerId; break; }
        }
        if (playerId == 0) return;
        // Death can be presented again by the life channel after the combat channel.
        // Every cleanup is idempotent and only touches this pilot's own flights.
        session.Repairs?.PlayerDied(playerId);
        session.BuildDispatch?.PlayerDied(playerId);
        session.Drones?.RecyclePlayerDrones(playerId);
    }
}
