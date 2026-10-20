using System;
using NebulaeModel.DataStructures;

namespace NebulaeWorld.Combat;

internal sealed class RemoteRespawnVisualState
{
    public int Tick { get; private set; }

    public bool Observe(PlayerLifeData previous, PlayerLifeData current)
    {
        var enteringRespawn = !current.IsAlive && current.RespawnMode == 2 &&
                              (previous.IsAlive || previous.RespawnMode != 2 || previous.DeathCount != current.DeathCount);

        if (current.IsAlive || current.RespawnMode != 2) Tick = 0;
        else Tick = enteringRespawn ? current.RespawnTick : Math.Max(Tick, current.RespawnTick);

        return enteringRespawn;
    }

    public int Advance()
    {
        var tick = Tick;
        if (Tick < int.MaxValue) Tick++;
        return tick;
    }
}
