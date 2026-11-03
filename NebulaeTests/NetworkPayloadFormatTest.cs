using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Session;
using NebulaeNetwork.PacketProcessors.Session;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class NetworkPayloadFormatTest
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SessionResponsesRejectUnknownHeadersAndExtraBytesBeforeChangingTheWorld(bool lobby)
    {
        var processor = new NebulaeNetPacketProcessor();
        PacketUtils.RegisterNestedTypes(processor);
        if (lobby) new LobbyResponseProcessor().Register(processor, false);
        else new HandshakeResponseProcessor().Register(processor, false);
        var settings = new CombatSettings(); settings.SetDefault();
        var current = NativeBytes(w => settings.Export(w));
        foreach (var header in new[] { -1, 1, int.MaxValue })
        {
            var invalid = current.ToArray(); BitConverter.GetBytes(header).CopyTo(invalid, 0);
            Reject(Compress(invalid));
        }
        Reject(Compress(current.Concat(new byte[] { 0 }).ToArray()));
        for (var length = 0; length < current.Length; length++)
            TestAssert.Throws<EndOfStreamException>(() => Dispatch(Compress(current.Take(length).ToArray())));

        void Reject(byte[] bytes) => TestAssert.Throws<ParseException>(() => Dispatch(bytes));
        void Dispatch(byte[] bytes)
        {
            var packet = lobby ? processor.Write(new LobbyResponse { CombatSettingsData = bytes, SavedThemeIds = [] }) :
                processor.Write(new HandshakeResponse
                { CombatSettingsData = bytes, SavedThemeIds = [], LocalPlayerData = new PlayerData(1, 101) });
            // No session or scene is needed: validation must finish before either is touched.
            processor.DispatchInline(packet, null);
        }
    }

    [TestMethod]
    public void CurrentCombatSettingsRoundTripWithoutChangingTheirLayout()
    {
        var settings = new CombatSettings(); settings.SetDefault();
        settings.initialLevel = 3; settings.battleExpFactor = 2;
        using var reader = new BinaryUtils.Reader(Compress(NativeBytes(w => settings.Export(w))), 0);
        var restored = new CombatSettings(); restored.Import(reader.BinaryReader); reader.RequireEnd();
        TestAssert.AreEqual(settings, restored);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SandboxPayloadsRequireTheirCurrentByteHeaderAndExactEnd(bool vein)
    {
        var current = NativeBytes(w =>
        {
            if (vein) new VeinData { id = 17, amount = 100 }.Export(w);
            else new VegeData { id = 17, protoId = 1 }.Export(w);
        });
        Read(current);
        for (var header = 0; header <= byte.MaxValue; header++)
        {
            if (header == current[0]) continue;
            var invalid = current.ToArray(); invalid[0] = (byte)header;
            TestAssert.Throws<ParseException>(() => Read(invalid));
        }
        TestAssert.Throws<ParseException>(() => Read(current.Concat(new byte[] { 0 }).ToArray()));
        for (var length = 0; length < current.Length; length++)
            TestAssert.Throws<EndOfStreamException>(() => Read(current.Take(length).ToArray()));

        void Read(byte[] bytes)
        {
            using var reader = new BinaryUtils.Reader(Compress(bytes), vein ? 1 : 2, byteHeader: true);
            if (vein) { var value = new VeinData(); value.Import(reader.BinaryReader); TestAssert.AreEqual(17, value.id); }
            else { var value = new VegeData(); value.Import(reader.BinaryReader); TestAssert.AreEqual(17, value.id); }
            reader.RequireEnd();
        }
    }

    private static byte[] NativeBytes(Action<BinaryWriter> export)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        export(writer); return stream.ToArray();
    }
}
