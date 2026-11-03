using NebulaeWorld.Statistics;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class ResearchStationStatisticsTest
{
    [TestMethod]
    [DataRow(-1, false, DisplayName = "NoRecentResearchPowerReturnsFalse")]
    [DataRow(599, true, DisplayName = "RecentResearchPowerReturnsTrueAcrossHistoryWraparound")]
    [DataRow(500, false, DisplayName = "OldResearchPowerOutsideRecentWindowReturnsFalse")]
    public void ResearchPowerUsesTheRecentHistoryWindow(int activeIndex, bool expected)
    {
        var energy = new long[600];
        if (activeIndex >= 0) energy[activeIndex] = 1;
        var factory = CreateFactoryStat(energy, 0);

        TestAssert.AreEqual(expected, ResearchStationStatistics.HasRecentPowerUsage([factory], 1));
    }

    [TestMethod]
    public void AnyFactoryWithRecentResearchPowerReturnsTrue()
    {
        var inactive = CreateFactoryStat(new long[600], 0);
        var energy = new long[600];
        energy[10] = 1;
        var active = CreateFactoryStat(energy, 11);

        TestAssert.IsTrue(ResearchStationStatistics.HasRecentPowerUsage([inactive, active], 2));
    }

    private static FactoryProductionStat CreateFactoryStat(long[] energy, int cursor)
    {
        var powerStat = Uninitialized<PowerStat>();
        powerStat.energy = energy;
        powerStat.cursor = [cursor];

        var factoryStat = Uninitialized<FactoryProductionStat>();
        factoryStat.powerPool = new PowerStat[5];
        factoryStat.powerPool[4] = powerStat;
        return factoryStat;
    }
}
