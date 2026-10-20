using System;
using NebulaeModel.Authority;

namespace NebulaeWorld.Authority;

/// <summary>Native client simulation may advance local state; shared outcomes are committed by the host.</summary>
internal static class WorldSimulation
{
    [ThreadStatic] private static CombatStat predictedHit;

    public static bool IsClient
    {
        get
        {
            var session = AuthorityRuleGuard.CurrentSession();
            return session.IsActive && !session.IsHost;
        }
    }

    // Native projectile callers use the returned reference for impact bookkeeping even when
    // damage is refused. Give them a thread-local empty result instead of a null by-ref return.
    public static ref CombatStat PredictedHit()
    {
        predictedHit = default;
        return ref predictedHit;
    }
}
