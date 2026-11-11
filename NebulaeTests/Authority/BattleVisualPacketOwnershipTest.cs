using System.Collections;
using HarmonyLib;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Combat;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class BattleVisualPacketOwnershipTest
{
    [TestMethod]
    public void ReusedTransportHeadersCannotDiscardQueuedBeamEnds()
    {
        var previous = Multiplayer.Session;
        try
        {
            var session = Uninitialized<MultiplayerSession>();
            session.Authority = new AuthoritySessionState();
            Multiplayer.Session = session;
            var manager = new BattleVisualManager();
            var codec = new NebulaeNetPacketProcessor();
            codec.SubscribeReusable<BattleVisualPacket>(packet =>
                manager.Receive(packet, BattleVisualFrame.Import(packet.Data)));

            // Several messages are decoded before the safe simulation boundary. The
            // decoder reuses its envelope, but each queued start/end must keep its sequence.
            for (var generation = 1; generation <= 1000; generation++)
            {
                foreach (var end in new[] { false, true })
                {
                    var frame = new BattleVisualFrame();
                    frame.Effects.Add(new BattleEffectData
                    {
                        Kind = BattleEffectKind.TurretLaser,
                        Id = 1,
                        Generation = generation,
                        Tick = generation * 2L,
                        End = end,
                        Life = 0,
                        Payload = end ? [] : [1]
                    });
                    var packet = new BattleVisualPacket
                    {
                        StarId = 1,
                        Sequence = generation * 2L + (end ? 1 : 0),
                        Tick = generation * 2L,
                        Data = frame.Export()
                    };
                    codec.ReadPacket(new NetDataReader(codec.Write(packet)), null);
                }
            }

            manager.TickClient(); // Unloaded clients cache the same ordered lifecycle.
            var frames = (IDictionary)AccessTools.Field(typeof(BattleVisualManager), "frames").GetValue(manager);
            var cached = frames[1]!;
            var effects = (IDictionary)AccessTools.Field(cached.GetType(), "Effects").GetValue(cached);
            TestAssert.IsEmpty(effects, "Every queued continuous beam received its End; none may remain alive.");
            TestAssert.AreEqual(2001L, AccessTools.Field(cached.GetType(), "Sequence").GetValue(cached));
        }
        finally { Multiplayer.Session = previous; }
    }
}
