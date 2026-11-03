using NebulaeModel.DataStructures;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class BattleImpactHistoryTest
{
    private static BattleImpactHistory.Entry Hit(int caster, long tick, int kind, bool authority = false, long generation = 2) =>
        new() { Origin = BattleEffectKind.RangerPlasma, Astro = 101, Caster = caster, Tick = tick, Kind = kind, Authority = authority, Generation = generation };

    [TestMethod]
    public void MatchingIsolatesSourceAndGenerationThenPrefersSubtypeAndNearestTick()
    {
        var history = new BattleImpactHistory();
        for (var i = 1; i < 3000; i++) history.Add(Hit(i, 100, 8));
        var nearest = Hit(4, 101, 3); var sameKind = Hit(4, 105, 8);
        history.Add(nearest); history.Add(sameKind);
        TestAssert.AreSame(nearest, history.Find(BattleEffectKind.RangerPlasma, 101, 4, 2, 101, false, 3));
        TestAssert.AreSame(sameKind, history.Find(BattleEffectKind.RangerPlasma, 101, 4, 2, 106, false, 8));
        TestAssert.IsNull(history.Find(BattleEffectKind.RangerPlasma, 102, 4, 2, 100, false));
        TestAssert.IsNull(history.Find(BattleEffectKind.RangerPlasma, 101, 4, 3, 100, false));
        sameKind.Matched = true;
        TestAssert.AreNotSame(sameKind, history.Find(BattleEffectKind.RangerPlasma, 101, 4, 2, 106, false, 8));
    }

    [TestMethod]
    public void HostFirstHitsPreserveArrivalOrderAndKindWhileExpiredHitsAreReleased()
    {
        var history = new BattleImpactHistory();
        var first = Hit(7, 100, 8, true); var second = Hit(7, 110, 3, true);
        history.Add(first); history.Add(second);
        TestAssert.AreSame(first, history.Find(BattleEffectKind.RangerPlasma, 101, 7, 2, 110, true));
        TestAssert.AreSame(second, history.Find(BattleEffectKind.RangerPlasma, 101, 7, 2, 110, true, 3, true));
        TestAssert.IsNull(history.Find(BattleEffectKind.RangerPlasma, 101, 7, 2, 231, true));
        history.Prune(281);
        TestAssert.IsNull(history.Find(BattleEffectKind.RangerPlasma, 101, 7, 2, 100, true, 8, true));
        TestAssert.AreSame(second, history.Find(BattleEffectKind.RangerPlasma, 101, 7, 2, 110, true));
        history.Clear();
        TestAssert.IsNull(history.Find(BattleEffectKind.RangerPlasma, 101, 7, 2, 110, true));
    }
}
