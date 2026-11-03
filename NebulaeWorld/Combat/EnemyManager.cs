#region

using System;
using NebulaeModel.DataStructures;
#pragma warning disable IDE1006 // Naming Styles
#pragma warning disable CA1822 // Mark members as static

#endregion

namespace NebulaeWorld.Combat;

internal partial class EnemyManager : IDisposable
{
    public readonly ToggleSwitch IsIncomingRequest = new();

    public readonly ToggleSwitch IsIncomingRelayRequest = new();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public void OnFactoryLoadFinished(PlanetFactory factory)
    {
        var unitCursor = factory.enemySystem.units.cursor;
        var unitBuffer = factory.enemySystem.units.buffer;
        for (var i = 1; i < unitCursor; i++)
        {
            // clear the blocking skill to prevent error due to skills are not all present in client
            unitBuffer[i].ClearBlockSkill();
        }
    }

    public void OnLeavePlanet()
    {
        if (Multiplayer.Session.IsServer) return;

        // Reset threat on each base on the loaded factory so it doesn't show on the monitor        
        for (var factoryIdx = 0; factoryIdx < GameMain.data.factoryCount; factoryIdx++)
        {
            var factory = GameMain.data.factories[factoryIdx];
            if (factory == null) continue;

            var bases = factory.enemySystem.bases;
            for (var baseId = 1; baseId < bases.cursor; baseId++)
            {
                if (bases[baseId] != null && bases[baseId].id == baseId && !bases[baseId].hasAssaultingUnit)
                {
                    bases[baseId].evolve.threat = 0;
                }
            }
        }
    }
}
