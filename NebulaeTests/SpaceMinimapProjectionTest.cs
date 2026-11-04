using NebulaeWorld.MonoBehaviours.Local;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class SpaceMinimapProjectionTest
{
    // 1 AU is 40000 universal units, and the minimap lays the star system out at 0.1 scene units
    // per AU, so a planet one AU from the player must land 0.1 units from the local player's dot.
    [TestMethod]
    public void APlayerOneAstronomicalUnitAwayLandsAtTheMinimapSystemScale()
    {
        var offset = SpaceMinimapProjection.SceneOffset(new VectorLF3(40000, 0, 0), new VectorLF3(0, 0, 0));
        TestAssert.AreEqual(0.1f, offset.x, 1e-5f);
        TestAssert.AreEqual(0f, offset.y, 1e-5f);
    }

    // In interstellar space there is no local star; the offset is measured from the player, which
    // is the same frame the minimap keeps its own dot in.
    [TestMethod]
    public void PositionsAreRelativeToThePlayerSoInterstellarSpaceNeedsNoStar()
    {
        var offset = SpaceMinimapProjection.SceneOffset(new VectorLF3(80000, 0, 0), new VectorLF3(40000, 0, 0));
        TestAssert.AreEqual(0.1f, offset.x, 1e-5f);
    }
}
