using System;

namespace NebulaeWorld.Statistics;

internal static class ResearchStationStatistics
{
    private const int ResearchPowerPoolIndex = 4;
    private const int RecentSampleCount = 60;

    public static bool HasRecentPowerUsage(FactoryProductionStat[] factoryStats, int factoryCount)
    {
        if (factoryStats == null || factoryCount <= 0)
        {
            return false;
        }

        var count = Math.Min(factoryCount, factoryStats.Length);
        for (var factoryIndex = 0; factoryIndex < count; factoryIndex++)
        {
            var powerPool = factoryStats[factoryIndex]?.powerPool;
            if (powerPool == null || powerPool.Length <= ResearchPowerPoolIndex)
            {
                continue;
            }

            var researchPower = powerPool[ResearchPowerPoolIndex];
            if (researchPower?.energy == null || researchPower.energy.Length == 0 ||
                researchPower.cursor == null || researchPower.cursor.Length == 0)
            {
                continue;
            }

            var historyLength = researchPower.energy.Length;
            var samples = Math.Min(RecentSampleCount, historyLength);
            // Match UIMechaLab.OnSupplyButtonClick: inspect the 60 most recent samples from power pool 4.
            for (var sample = 0; sample < samples; sample++)
            {
                var historyIndex = (researchPower.cursor[0] - 1 - sample) % historyLength;
                if (historyIndex < 0)
                {
                    historyIndex += historyLength;
                }
                if (researchPower.energy[historyIndex] > 0)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
