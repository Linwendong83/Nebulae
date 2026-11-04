using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Factory.RayReceiver;
using NebulaeNetwork.PacketProcessors.Factory.RayReceiver;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class RayReceiverSynchronizationTest
{
    private sealed class Receiver : RayReceiverChangeLensProcessor
    {
        public void Apply(RayReceiverChangeLensPacket packet) => ProcessPacket(packet, null!);
    }

    [TestMethod]
    public void NativeCatalystStateRoundTripsAndAppliesWithoutChangingPowerOutput()
    {
        var source = new PowerGeneratorComponent
        {
            id = 1,
            gamma = true,
            catalystId = 1209,
            curCatalystId = 1210,
            catalystCount = 12,
            catalystInc = 36,
            catalystMask = 3,
            catalystIncLevel = 4,
            catalystPoint = 2700
        };
        var sent = new RayReceiverChangeLensPacket(1, source, 101);
        var processor = new NebulaeNetPacketProcessor();
        RayReceiverChangeLensPacket? received = null;
        processor.SubscribeReusable<RayReceiverChangeLensPacket>(packet => received = packet);
        var bytes = processor.Write(sent);
        // Packet id, five Int32 fields, three Int16 fields and one byte.
        TestAssert.HasCount(35, bytes);
        processor.ReadPacket(new NetDataReader(bytes));
        TestAssert.IsNotNull(received);

        var previousGame = GameMain.data;
        var game = Uninitialized<GameData>();
        var factory = Uninitialized<PlanetFactory>();
        var power = Uninitialized<PowerSystem>();
        power.genPool = [default, new PowerGeneratorComponent { id = 1, gamma = true, productCount = 17 }];
        factory.powerSystem = power;
        game.galaxy = new GalaxyData { stars = [new StarData { planets = [new PlanetData { id = 101, factory = factory }] }] };
        try
        {
            GameMain.data = game;
            new Receiver().Apply(received);
            var applied = power.genPool[1];
            TestAssert.AreEqual(source.catalystId, applied.catalystId);
            TestAssert.AreEqual(source.curCatalystId, applied.curCatalystId);
            TestAssert.AreEqual(source.catalystCount, applied.catalystCount);
            TestAssert.AreEqual(source.catalystInc, applied.catalystInc);
            TestAssert.AreEqual(source.catalystMask, applied.catalystMask);
            TestAssert.AreEqual(source.catalystIncLevel, applied.catalystIncLevel);
            TestAssert.AreEqual(source.catalystPoint, applied.catalystPoint);
            TestAssert.AreEqual(17f, applied.productCount);
            received.CatalystCount = -1;
            received.LensCount = 0;
            new Receiver().Apply(received);
            TestAssert.AreEqual(source.catalystPoint, power.genPool[1].catalystPoint);
        }
        finally { GameMain.data = previousGame; }
    }
}
