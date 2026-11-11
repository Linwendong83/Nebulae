using HarmonyLib;
using NebulaeModel.Contracts;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking.Serialization;
using NebulaeWorld;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class PlayerSaveCompatibilityTest
{
    private GameData previousGame = null!;
    private bool previousLoaded;
    private int[] previousStacks = null!;
    private Harmony harmony = null!;

    [TestInitialize]
    public void Setup()
    {
        previousGame = GameMain.data;
        previousLoaded = StorageComponent.staticLoaded; previousStacks = StorageComponent.itemStackCount;
        StorageComponent.staticLoaded = true; StorageComponent.itemStackCount = Enumerable.Repeat(100, 12000).ToArray();
        GameMain.data = Uninitialized<GameData>();
        harmony = new Harmony("nebulae.tests.legacy-player-save");
        harmony.Patch(AccessTools.Method(typeof(CombatModuleComponent), "Setup", [typeof(int), typeof(GameData)]),
            prefix: new HarmonyMethod(typeof(PlayerSaveCompatibilityTest), nameof(SkipAssets)));
    }

    [TestCleanup]
    public void Cleanup()
    {
        harmony.UnpatchSelf(); GameMain.data = previousGame;
        StorageComponent.staticLoaded = previousLoaded; StorageComponent.itemStackCount = previousStacks;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Revision8PlayersKeepTheirInventoriesAndDoNotConsumeTheNextIdentity(bool payload)
    {
        var writer = new NetDataWriter();
        var first = LegacyPlayer(payload, 2); var second = LegacyPlayer(payload, 3);
        writer.Put(new string('a', 64)); WriteRevision8(writer, first);
        var boundary = writer.Length;
        writer.Put(new string('b', 64)); WriteRevision8(writer, second);
        var reader = new NetDataReader(writer.CopyData());
        Check.AreEqual(new string('a', 64), reader.GetString());
        var restored = new PlayerData { DashboardData = [42] }; restored.Import(reader, 8);
        Check.AreEqual(boundary, reader.Position);
        Check.IsNull(restored.DashboardData);
        Check.AreEqual("legacy-2", restored.Username); Check.AreEqual(1234567890123L, restored.Mecha.SandCount);
        Check.AreEqual(1, restored.DIYItemId.Length); Check.AreEqual(1101, restored.DIYItemId[0]);
        if (payload)
        {
            Check.AreEqual(17, restored.Mecha.Inventory.GetItemCount(1101));
            Check.AreEqual(34, restored.Mecha.Inventory.grids[0].inc);
            Check.AreEqual(77, restored.Mecha.FightData.Hp);
        }
        Check.AreEqual(new string('b', 64), reader.GetString());
        var next = new PlayerData(); next.Import(reader, 8);
        Check.AreEqual("legacy-3", next.Username); Check.AreEqual(0, reader.AvailableBytes);
        // Migration writes the current layout without dropping the historical player data.
        var migrated = new NetDataWriter(); restored.Serialize(migrated);
        var current = new PlayerData(); current.Import(new NetDataReader(migrated.CopyData()), 13);
        Check.AreEqual(restored.Mecha.SandCount, current.Mecha.SandCount);
        if (payload) Check.AreEqual(17, current.Mecha.Inventory.GetItemCount(1101));
    }

    [TestMethod]
    [DataRow(9)]
    [DataRow(10)]
    [DataRow(11)]
    [DataRow(12)]
    [DataRow(13)]
    public void NewerRevisionsStillReadDashboardAndOnlyTheirOwnTails(int revision)
    {
        var source = LegacyPlayer(false, 2); source.DashboardData = [7, 8, 9];
        var writer = new NetDataWriter(); source.Serialize(writer);
        var omitted = revision < 12 ? sizeof(int) : 0;
        if (revision < 11) omitted += 38;
        if (revision < 10) omitted += sizeof(int);
        var bytes = writer.CopyData().Take(writer.Length - omitted).ToArray();
        var stream = new NetDataWriter(); stream.Put(bytes); stream.Put("next-player");
        var reader = new NetDataReader(stream.CopyData());
        var restored = new PlayerData(); restored.Import(reader, revision);
        CollectionAssert.AreEqual(source.DashboardData, restored.DashboardData);
        Check.AreEqual("next-player", reader.GetString()); Check.AreEqual(0, reader.AvailableBytes);
    }

    [TestMethod]
    public void Revision10CannotSilentlyAcceptAMissingOrTruncatedVegetationSection()
    {
        var writer = new NetDataWriter(); LegacyPlayer(false, 2).Serialize(writer);
        var bytes = writer.CopyData();
        // Remove flight/star, then truncate the mandatory vegetation length itself.
        foreach (var missing in new[] { 1, 2, 3, 4 })
            Check.Throws<ParseException>(() => new PlayerData().Import(
                new NetDataReader(bytes.Take(bytes.Length - 42 - missing).ToArray()), 10));
        var truncated = bytes.Take(bytes.Length - 42).ToArray();
        Array.Copy(BitConverter.GetBytes(10), 0, truncated, truncated.Length - 4, 4);
        Check.Throws<ParseException>(() => new PlayerData().Import(new NetDataReader(truncated), 10));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ServerFileLoadEnablesSavingOnlyForACompleteLegacyFileAndNeverRewritesItsSource(bool truncated)
    {
        var folder = Path.Combine(Path.GetTempPath(), "nebulae-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "legacy.server");
        var writer = new NetDataWriter(); writer.Put("REV"); writer.Put((ushort)8); writer.Put(2);
        writer.Put(new string('a', 64)); WriteRevision8(writer, LegacyPlayer(true, 2));
        writer.Put(new string('b', 64)); WriteRevision8(writer, LegacyPlayer(true, 3));
        var source = writer.CopyData(); if (truncated) Array.Resize(ref source, source.Length - 1);
        File.WriteAllBytes(path, source);
        var previousPath = AccessTools.Field(typeof(GameConfig), "gameSavePath").GetValue(null);
        var saves = (Dictionary<string, IPlayerData>)AccessTools.Field(typeof(SaveManager), "playerSaves").GetValue(null);
        var previousSaves = saves.ToArray();
        var fields = new[] { "serverDataLoaded", "serverDataLoadFailed", "legacyAuthorityArchive", "loadedServerIdentity", "<WorldId>k__BackingField" }
            .Select(name => AccessTools.Field(typeof(SaveManager), name)).ToArray();
        var values = fields.Select(field => field.GetValue(null)).ToArray();
        try
        {
            SetStatic(typeof(GameConfig), "gameSavePath", folder + Path.DirectorySeparatorChar);
            AccessTools.Method(typeof(SaveManager), "LoadServerDataNow").Invoke(null, [true, "legacy"]);
            Check.AreEqual(!truncated, SaveManager.CanSave);
            Check.AreEqual(truncated ? 0 : 2, SaveManager.PlayerSaves.Count);
            if (!truncated)
            {
                Check.AreEqual(17, SaveManager.PlayerSaves[new string('b', 64)].Mecha.Inventory.GetItemCount(1101));
                Check.AreEqual(new string('a', 64), ((PlayerData)SaveManager.PlayerSaves[new string('a', 64)]).PersistentId);
            }
            CollectionAssert.AreEqual(source, File.ReadAllBytes(path));
        }
        finally
        {
            SetStatic(typeof(GameConfig), "gameSavePath", previousPath);
            saves.Clear(); foreach (var entry in previousSaves) saves.Add(entry.Key, entry.Value);
            for (var i = 0; i < fields.Length; i++) fields[i].SetValue(null, values[i]);
            File.Delete(path); Directory.Delete(folder);
        }
    }

    private static PlayerData LegacyPlayer(bool payload, ushort id)
    {
        var mecha = new MechaData { SandCount = 1234567890123L };
        if (payload)
        {
            mecha.Inventory = new StorageComponent(2); mecha.Inventory.AddItemStacked(1101, 17, 34, out _);
            mecha.DeliveryPackage = new DeliveryPackage(); mecha.DeliveryPackage.Init();
            mecha.ReactorStorage = new StorageComponent(4); mecha.WarpStorage = new StorageComponent(1);
            mecha.Forge = new MechaForge { tasks = [], extraItems = new ItemBundle() };
            mecha.ConstructionModule = new ConstructionModuleComponent();
            mecha.FightData = new MechaFightData
            {
                Hp = 77,
                GroundCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] },
                SpaceCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] }
            };
        }
        return new PlayerData(id, 101, "legacy-" + id) { Mecha = mecha, DIYItemId = [1101], DIYItemValue = [5] };
    }

    // Historical revision 8 writer: notably no dashboard flag or life/vegetation/flight tails.
    private static void WriteRevision8(NetDataWriter writer, PlayerData player)
    {
        writer.Put(player.Username); writer.Put(player.PlayerId); writer.Put(player.LocalPlanetId);
        player.LocalPlanetPosition.Serialize(writer); player.UPosition.Serialize(writer);
        player.Rotation.Serialize(writer); player.BodyRotation.Serialize(writer);
        var mecha = (MechaData)player.Mecha;
        mecha.TechBonuses.Serialize(writer); writer.Put(mecha.SandCount);
        writer.Put(mecha.CoreEnergy); writer.Put(mecha.ReactorEnergy); writer.Put(mecha.ReactorStorage != null);
        if (mecha.ReactorStorage != null)
        {
            mecha.FightData.Serialize(writer);
            using var stream = new MemoryStream(); using var binary = new BinaryWriter(stream);
            mecha.Inventory.Export(binary); mecha.DeliveryPackage.Export(binary);
            mecha.ReactorStorage.Export(binary); mecha.WarpStorage.Export(binary);
            mecha.Forge.Export(binary); mecha.ConstructionModule.Export(binary);
            var bytes = stream.ToArray(); writer.Put(bytes.Length); writer.Put(bytes);
        }
        writer.Put(false); writer.Put(false);
        writer.Put(player.DIYItemId.Length);
        for (var i = 0; i < player.DIYItemId.Length; i++) { writer.Put(player.DIYItemId[i]); writer.Put(player.DIYItemValue[i]); }
    }

    private static bool SkipAssets() => false;
}
