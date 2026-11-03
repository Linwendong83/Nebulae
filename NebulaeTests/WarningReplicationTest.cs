using HarmonyLib;
using NebulaeWorld.Warning;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class WarningReplicationTest
{
    [ClassInitialize]
    public static void Initialize(TestContext _)
    {
        // Harmony's copy permits the same private game field access as the BepInEx runtime.
        Harmony.ReversePatch(AccessTools.Method(typeof(WarningManager), nameof(WarningManager.ImportBinaryData)),
            new HarmonyMethod(typeof(WarningReplicationTest), nameof(ImportSnapshot)));
    }

    private static void ImportSnapshot(WarningManager _, BinaryReader __, int ___) =>
        throw new NotSupportedException("Replaced by the real snapshot importer.");

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(5)]
    public void ReplacingSnapshotRebuildsNativeLinesWithoutRemovedOrReusedSlots(int count)
    {
        var previousData = GameMain.data;
        var data = Uninitialized<GameData>();
        var ws = data.warningSystem = new WarningSystem { warningPool = new WarningData[8], warningCursor = 7 };
        SetField(ws, "warningCapacity", 8);
        SetField(ws, "warningLineOrderPrev", Enumerable.Repeat(-1, 8).ToArray());
        SetField(ws, "warningLineOrderNext", Enumerable.Repeat(-1, 8).ToArray());
        SetField(ws, "warningLineOrderLinked", new bool[8]);
        data.trashSystem = Uninitialized<TrashSystem>();
        data.trashSystem.container = Uninitialized<TrashContainer>();

        // Old native line order contains holes and metadata absent from network snapshots.
        foreach (var slot in new[] { 6, 1, 4 })
        {
            ws.warningPool[slot] = new WarningData
            {
                id = slot,
                state = 1,
                signalId = 405,
                detailId2 = 99,
                factoryId = WarningData.LOCATION_TRACKING,
                objectId = 123
            };
            ws.AppendWarningLineOrder(slot);
        }

        using var warnings = new WarningManager();
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        for (var i = 1; i <= count; i++)
        {
            writer.Write(405);
            writer.Write(2000 + i);
            writer.Write(101);
            writer.Write((float)i);
            writer.Write(2f);
            writer.Write(3f);
            writer.Write(-1);
        }
        bytes.Position = 0;
        using var reader = new BinaryReader(bytes);
        try
        {
            GameMain.data = data;
            ImportSnapshot(warnings, reader, count);
            ws.SyncVisibleWarningLineOrder(405, 0);

            var next = (int[])AccessTools.Field(typeof(WarningSystem), "warningLineOrderNext").GetValue(ws);
            var head = (int)AccessTools.Field(typeof(WarningSystem), "warningLineOrderHead").GetValue(ws);
            var visibleSlots = new List<int>();
            for (var slot = head; slot > 0; slot = next[slot])
            {
                Check.IsLessThan(8, visibleSlots.Count, "The native line list must not contain a cycle.");
                Check.IsTrue(ws.ShouldRenderWarningLine(slot, 405, 0), $"Stale line for slot {slot}.");
                visibleSlots.Add(slot);
            }
            CollectionAssert.AreEquivalent(Enumerable.Range(1, count).ToArray(), visibleSlots.ToArray());
            for (var slot = count + 1; slot < 7; slot++)
                Check.AreEqual(default(WarningData), ws.warningPool[slot], $"Removed slot {slot} must be empty.");
            for (var slot = 1; slot <= count; slot++)
            {
                Check.AreEqual(2000 + slot, ws.warningPool[slot].detailId1);
                Check.AreEqual(0, ws.warningPool[slot].detailId2);
                Check.AreEqual(0, ws.warningPool[slot].factoryId);
                Check.AreEqual(0, ws.warningPool[slot].objectId);
            }

            ws.SyncVisibleWarningLineOrder(0, 0);
            Check.AreEqual(-1, AccessTools.Field(typeof(WarningSystem), "warningLineOrderHead").GetValue(ws),
                "Clearing focus must remove every line from the replaced snapshot.");
        }
        finally { GameMain.data = previousData; }
    }
}
