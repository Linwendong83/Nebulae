using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

/// <summary>
/// A client releases every factory when it leaves a star system, but the vein panel still names the
/// planet it was showing. SetInspectPlanet then unsubscribes through a null factory before it can
/// switch to the next planet, and the exception aborts the rest of every UIGame update.
/// </summary>
[TestClass]
public class VeinDetailPatchTest
{
    [TestMethod]
    public void TheGuardBindsToTheVanillaEntryPoint()
    {
        var targets = DeclaredPatches(typeof(UIVeinDetail_Patch))
            .Select(patch => patch.Target?.Name).ToArray();

        CollectionAssert.AreEquivalent(new[] { nameof(UIVeinDetail.SetInspectPlanet) }, targets);
    }

    [TestMethod]
    public void OnlyAPlanetWithoutAFactoryCountsAsStale()
    {
        TestAssert.IsFalse(UIVeinDetail_Patch.IsStale(null), "no planet is nothing to forget");

        var planet = Uninitialized<PlanetData>();
        TestAssert.IsTrue(UIVeinDetail_Patch.IsStale(planet), "a released factory leaves the planet stale");

        SetField(planet, "factory", Uninitialized<PlanetFactory>());
        TestAssert.IsFalse(UIVeinDetail_Patch.IsStale(planet), "a live factory must stay subscribed");
    }
}
