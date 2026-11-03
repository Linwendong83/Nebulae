using NebulaeModel.Authority;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Utils;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass]
public class ServerSaveAuthorityArchiveTest
{
    private const string World = "0123456789abcdef0123456789abcdef";

    // Historical schema-1 bytes: migration must read an existing archive independently of an encoder.
    private static byte[] Archive() =>
        Convert.FromBase64String(
            "TkJBUwEAAAAgADAxMjM0NTY3ODlhYmNkZWYwMTIzNDU2Nzg5YWJjZGVmAQAAAAAAAAACAAAAAAAAADIAAAAAAAAAAQAAAAYA" +
            "cGxheWVyAmYAAAABAAAAAAAABAAAAAIAAAAGAHBsYXllcgEAAAAAAAAAAAAAAAAAAAAABgsHAAAAKgAAAAAAAAAGAHBsYXll" +
            "cgEAAAAAAAAAAAAAAAAAAAAAAQAAAAABAAAAAADgXkABAAAAAAACZgAAAAcAAAACAAAAAAAAAAYAAAABAAAABgBwbGF5ZXID" +
            "AAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAWYAAAAJAAAAAgAAAAAAAAACBAQAAAAAAAAAMgAAAAAAAAAA");

    [TestMethod]
    public void HistoricalFactsRoundTripInsideTheUnchangedServerPlayerLayout()
    {
        var writer = new NetDataWriter();
        writer.Put("REV");
        writer.Put((ushort)10);
        writer.Put(World);
        writer.Put(0);
        var original = writer.CopyData();
        ServerSaveAuthorityArchive.Append(writer, null, World);
        CollectionAssert.AreEqual(original, writer.CopyData());
        var archive = Archive();
        ServerSaveAuthorityArchive.Append(writer, archive, World);
        var reader = new NetDataReader(writer.CopyData());
        TestAssert.AreEqual("REV", reader.GetString());
        TestAssert.AreEqual((ushort)10, reader.GetUShort());
        TestAssert.AreEqual(World, reader.GetString());
        TestAssert.AreEqual(0, reader.GetInt());
        var restored = ServerSaveAuthorityArchive.Read(reader, World);
        CollectionAssert.AreEqual(archive, restored);
        TestAssert.AreEqual(0, reader.AvailableBytes);
        TestAssert.IsTrue(AuthoritySaveCodec.TryDecode(restored, out var state, out var reason), reason);
        TestAssert.AreEqual("player", state.Players[0].PersistentId);
        TestAssert.AreEqual(42L, state.Accounts[0].LongBalance);
        TestAssert.AreEqual(123.5, state.Accounts[1].DoubleBalance);
        TestAssert.AreEqual(6, state.Budgets[0].Total);
        TestAssert.AreEqual(ConstructionTaskStage.Working, state.Tasks[0].Stage);
        reader = new NetDataReader(original, original.Length, original.Length);
        TestAssert.IsNull(ServerSaveAuthorityArchive.Read(reader, World));
    }

    [TestMethod]
    [DataRow("magic")]
    [DataRow("schema")]
    [DataRow("truncated")]
    [DataRow("trailing")]
    [DataRow("role")]
    public void MalformedArchivesAreRejected(string corruption)
    {
        var archive = Archive();
        switch (corruption)
        {
            case "magic": archive[0] = (byte)'X'; break;
            case "schema": BitConverter.GetBytes(AuthoritySidecarSchema.Current + 1).CopyTo(archive, 4); break;
            case "truncated": Array.Resize(ref archive, archive.Length - 1); break;
            case "trailing": Array.Resize(ref archive, archive.Length + 1); break;
            // NBAS header, world string, epoch/tick, player count and the first player's identity precede its role.
            case "role":
                {
                    var roleIndex = 8 + 2 + 32 + 16 + 8 + 4 + 2 + 6;
                    TestAssert.AreNotEqual((byte)200, archive[roleIndex],
                        "The role offset drifted; the layout comment above is stale.");
                    archive[roleIndex] = 200;
                    break;
                }
        }
        TestAssert.ThrowsExactly<InvalidDataException>(() =>
            ServerSaveAuthorityArchive.Read(new NetDataReader(archive), World));
    }

    [TestMethod]
    [DataRow(0, AuthoritySidecarLimits.PlayersMax)]
    [DataRow(1, AuthoritySidecarLimits.AccountsMax)]
    [DataRow(2, AuthoritySidecarLimits.BudgetsMax)]
    [DataRow(3, AuthoritySidecarLimits.TasksMax)]
    public void OversizedRecordCountsAreRejectedBeforeReadingRecords(int block, int limit)
    {
        var writer = new NetDataWriter();
        writer.Put(System.Text.Encoding.ASCII.GetBytes(AuthoritySaveCodec.Magic));
        writer.Put(AuthoritySidecarSchema.Current);
        writer.Put((ushort)World.Length);
        writer.Put(System.Text.Encoding.UTF8.GetBytes(World));
        writer.Put(1UL); writer.Put(2UL);
        writer.Put(50L);
        for (var index = 0; index < 4; index++) writer.Put(index == block ? limit + 1 : 0);
        TestAssert.IsFalse(AuthoritySaveCodec.TryDecode(writer.CopyData(), out _, out var reason));
        StringAssert.Contains(reason, "ceiling");
    }

    [TestMethod]
    public void AForeignArchiveCannotOverwriteThePreviousServerFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "nebulae-server-" + Guid.NewGuid().ToString("N"));
        var original = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(path, original);
        try
        {
            var writer = new NetDataWriter();
            writer.Put(original);
            TestAssert.ThrowsExactly<InvalidDataException>(() =>
            {
                ServerSaveAuthorityArchive.Append(writer, Archive(), Guid.NewGuid().ToString("N"));
                AtomicFile.Write(path, writer.CopyData());
            });
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }
}
