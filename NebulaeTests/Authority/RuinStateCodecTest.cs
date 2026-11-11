using NebulaeModel.Packets.Authority;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// The ruin codec is the gate every replicated wreck passes through, so its rejection rules decide
/// whether a whole Ruin scope can be installed at all.
/// </summary>
[TestClass]
public class RuinStateCodecTest
{
    private static RuinData Wreck(short modelIndex, int lifeTime = -1) => new()
    {
        id = 6301,
        modelIndex = modelIndex,
        lifeTime = lifeTime,
        pos = new Vector3(1.5f, 2.5f, 3.5f),
        rot = new Quaternion(0f, 0.7071068f, 0f, 0.7071068f)
    };

    /// <summary>
    /// A belt the dark fog destroys keeps a loot prefab with ruin id 0, which PlanetFactory turns
    /// into modelArray[0] — a null model and a lifetime of -1. Refusing that made a 16-member Ruin
    /// baseline uninstallable on that planet, once per frame, for as long as the client stayed.
    /// </summary>
    [TestMethod]
    public void ADestroyedBeltRuinWithModelIndexZeroRoundTrips()
    {
        var decoded = RuinStateCodec.Decode(RuinStateCodec.Encode(Wreck(modelIndex: 0)));

        TestAssert.AreEqual(6301, decoded.id);
        TestAssert.AreEqual(0, decoded.modelIndex);
        TestAssert.AreEqual(-1, decoded.lifeTime);
        TestAssert.AreEqual(new Vector3(1.5f, 2.5f, 3.5f), decoded.pos);
        TestAssert.AreEqual(new Quaternion(0f, 0.7071068f, 0f, 0.7071068f), decoded.rot);
    }

    [TestMethod]
    public void ARuinIndexOutsideTheModelTableIsStillRefused()
    {
        TestAssert.ThrowsExactly<InvalidDataException>(() => RuinStateCodec.Decode(RuinStateCodec.Encode(Wreck(2048))));
        TestAssert.ThrowsExactly<InvalidDataException>(() => RuinStateCodec.Decode(RuinStateCodec.Encode(Wreck(-1))));
    }

    [TestMethod]
    public void ANonFinitePositionIsRefused()
    {
        var wreck = Wreck(7);
        wreck.pos = new Vector3(float.NaN, 2f, 3f);
        var error = TestAssert.ThrowsExactly<InvalidDataException>(() => RuinStateCodec.Decode(RuinStateCodec.Encode(wreck)));
        // The message names the facts that failed so a log line is enough to diagnose the next one.
        TestAssert.Contains("id=6301", error.Message);
    }
}
