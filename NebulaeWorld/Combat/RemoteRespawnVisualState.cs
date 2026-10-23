using System;
using NebulaeModel.DataStructures;

namespace NebulaeWorld.Combat;

internal sealed class RemoteRespawnVisualState
{
    public int Tick { get; private set; }
    public bool IsAlive { get; private set; } = true;
    public int DeathCount { get; private set; }
    private int respawnMode;

    public bool Observe(PlayerLifeData current)
    {
        var enteringRespawn = !current.IsAlive && current.RespawnMode == 2 &&
                              (IsAlive || respawnMode != 2 || DeathCount != current.DeathCount);

        if (current.IsAlive || current.RespawnMode != 2) Tick = 0;
        else Tick = enteringRespawn ? current.RespawnTick : Math.Max(Tick, current.RespawnTick);

        IsAlive = current.IsAlive;
        DeathCount = current.DeathCount;
        respawnMode = current.RespawnMode;
        return enteringRespawn;
    }

    public int Advance()
    {
        var tick = Tick;
        if (Tick < int.MaxValue) Tick++;
        return tick;
    }
}
