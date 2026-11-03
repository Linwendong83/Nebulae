using NebulaeWorld.Factory;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class GeothermalPlacementTest
{
    [TestMethod]
    public void MissingPitAndMalformedReferencesCannotReachNativeConstruction()
    {
        var factory = (PlanetFactory)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PlanetFactory));
        factory.ruinPool = new RuinData[4]; factory.ruinCursor = 4;
        var preview = new BuildPreview { desc = new PrefabDesc { geothermal = true }, paramCount = 1, parameters = new[] { 1 } };
        TestAssert.IsFalse(GeothermalPlacement.Validate(factory, new[] { preview }));
        factory.ruinPool[1] = new RuinData { id = 1, modelIndex = 405 };
        TestAssert.IsFalse(GeothermalPlacement.Validate(factory, new[] { preview }));
        preview.parameters[0] = -1;
        TestAssert.IsFalse(GeothermalPlacement.Validate(factory, new[] { preview }));
        preview.parameters = Array.Empty<int>();
        TestAssert.IsFalse(GeothermalPlacement.Validate(factory, new[] { preview }));
        preview.parameters = new[] { 0 };
        TestAssert.IsTrue(GeothermalPlacement.Validate(factory, new[] { preview }), "Preserve native lava-planet construction without a pit.");
    }
}
