using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

/// <summary>
/// The planetary shield panel refreshed itself every frame from references captured when it opened.
/// A client that imports the planet under it can leave those references null, and vanilla has no
/// guard, so one null shield field becomes a NullReferenceException per frame.
/// </summary>
[TestClass]
public class PlanetShieldDetailPatchTest
{
    [TestMethod]
    public void TheGuardsBindToBothVanillaEntryPoints()
    {
        var targets = DeclaredPatches(typeof(UIPlanetShieldDetail_Patch))
            .Select(patch => patch.Target?.Name).ToArray();

        CollectionAssert.AreEquivalent(
            new[] { nameof(UIPlanetShieldDetail.Refresh), nameof(UIPlanetShieldDetail.DetermineVisible) }, targets);
    }

    [TestMethod]
    public void EveryReferenceRefreshDereferencesDecidesWhetherThePanelMayShow()
    {
        var planet = Uninitialized<PlanetData>();
        var factory = Uninitialized<PlanetFactory>();
        var field = Uninitialized<PlanetATField>();

        TestAssert.IsFalse(UIPlanetShieldDetail_Patch.CanRefresh(null, factory, field, null), "no planet");
        TestAssert.IsFalse(UIPlanetShieldDetail_Patch.CanRefresh(planet, factory, null, null), "no shield field");

        SetField(factory, "powerSystem", Uninitialized<PowerSystem>());
        TestAssert.IsFalse(UIPlanetShieldDetail_Patch.CanRefresh(planet, factory, field, null), "no physics arguments");

        SetField(field, "physicsArgs", new uint[2]);
        TestAssert.IsFalse(UIPlanetShieldDetail_Patch.CanRefresh(planet, factory, field, null), "no physics verts");

        SetField(field, "physicsMeshVertsOriginal", new UnityEngine.Vector3[1]);
        // A remote planet draws no model; a loaded one does, and Refresh reads it for the checkbox.
        TestAssert.IsTrue(UIPlanetShieldDetail_Patch.CanRefresh(planet, factory, field, null), "an unloaded planet needs no factory model");

        SetField(planet, "factoryLoaded", true);
        TestAssert.IsFalse(UIPlanetShieldDetail_Patch.CanRefresh(planet, factory, field, null), "a loaded planet draws its model");

        SetField(factory, "powerSystem", null);
        TestAssert.IsFalse(UIPlanetShieldDetail_Patch.CanRefresh(planet, factory, field, null), "no power system");
    }

    [TestMethod]
    public void TheGuardsAreInertOutsideMultiplayer()
    {
        TestAssert.IsTrue(UIPlanetShieldDetail_Patch.Refresh_Prefix(null!));
        TestAssert.IsTrue(UIPlanetShieldDetail_Patch.DetermineVisible_Prefix(null!));
    }
}
