using BepInEx.Configuration;
using NebulaeModel.Utils;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
internal class LegacyDataMigrationTest
{
    private string root = null!;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "nebulae-migration-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(root, true);

    [TestMethod]
    public void MainConfigMigratesSectionValuesAndUnknownKeysWithoutChangingSource()
    {
        const string contents = "# 用户配置\r\n[Nebula - Settings]\r\nHostPort = 9876\r\nUnknownOption = preserve me\r\n";
        var legacy = Path.Combine(root, "nebula.cfg");
        File.WriteAllText(legacy, contents);
        var path = LegacyDataMigration.ConfigFile(root, "nebulae.cfg", "nebula.cfg", true);
        TestAssert.Contains("# 用户配置", File.ReadAllText(path));
        var config = new ConfigFile(path, false);
        TestAssert.AreEqual(9876, config.Bind("Nebulae - Settings", "HostPort", 9869).Value);
        TestAssert.AreEqual("preserve me", config.Bind("Nebulae - Settings", "UnknownOption", "").Value);
        TestAssert.AreEqual(contents, File.ReadAllText(legacy));
    }

    [TestMethod]
    public void ExistingConfigWinsOnEveryLaunch()
    {
        var target = Path.Combine(root, "nebulae.cfg");
        File.WriteAllText(target, "new settings");
        File.WriteAllText(Path.Combine(root, "nebula.cfg"), "old settings");
        for (var i = 0; i < 2; i++) LegacyDataMigration.ConfigFile(root, "nebulae.cfg", "nebula.cfg", true);
        TestAssert.AreEqual("new settings", File.ReadAllText(target));
    }

    [TestMethod]
    public void WorldGenerationConfigCopiesBytesWithoutChangingSections()
    {
        var source = Path.Combine(root, "nebulaGameDescSettings.cfg");
        File.WriteAllText(source, "[Basic]\r\ngalaxySeed = 42\r\n[Combat]\r\naggressiveness = 0.5\r\n");
        var target = LegacyDataMigration.ConfigFile(root, "nebulaeGameDescSettings.cfg", Path.GetFileName(source));
        CollectionAssert.AreEqual(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }

    [TestMethod]
    public void CleanInstallDoesNotPublishAnEmptyMigration()
    {
        var config = LegacyDataMigration.ConfigFile(root, "nebulae.cfg", "nebula.cfg", true);
        var data = LegacyDataMigration.DataDirectory(root);
        TestAssert.IsFalse(File.Exists(config));
        TestAssert.IsFalse(Directory.Exists(data));
        TestAssert.IsEmpty(Directory.GetFileSystemEntries(root));
    }

    [TestMethod]
    public void ConfigReadFailureLeavesSourceIntactAndCanBeRetried()
    {
        var source = Path.Combine(root, "nebula.cfg");
        File.WriteAllText(source, "[Nebula - Settings]\nHostPort = 9876\n");
        using (var held = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            TestAssert.Throws<IOException>(() => LegacyDataMigration.ConfigFile(root, "nebulae.cfg", "nebula.cfg", true));
        TestAssert.IsFalse(File.Exists(Path.Combine(root, "nebulae.cfg")));
        TestAssert.HasCount(1, Directory.GetFiles(root));
        TestAssert.IsTrue(File.Exists(LegacyDataMigration.ConfigFile(root, "nebulae.cfg", "nebula.cfg", true)));
    }

    [TestMethod]
    public void DirectoryMigrationCopiesNestedDataAndBackupsAndLeavesOriginals()
    {
        var source = Path.Combine(root, "Nebula", "world-id");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "transactions.bin"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(source, "transactions.bin.bak"), new byte[] { 4, 5 });
        var target = LegacyDataMigration.DataDirectory(root);
        foreach (var file in Directory.GetFiles(source))
            CollectionAssert.AreEqual(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(target, "world-id", Path.GetFileName(file))));
        TestAssert.IsTrue(Directory.Exists(source));
        TestAssert.IsEmpty(Directory.GetDirectories(root, ".nebulae-migration-*"));
    }

    [TestMethod]
    public void ExistingDataDirectoryIsNeverMergedOrOverwritten()
    {
        var old = Path.Combine(root, "Nebula");
        var target = Path.Combine(root, "Nebulae");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(old, "metadata.bin"), "old");
        File.WriteAllText(Path.Combine(old, "extra.bin"), "old extra");
        File.WriteAllText(Path.Combine(target, "metadata.bin"), "new");
        for (var i = 0; i < 2; i++) LegacyDataMigration.DataDirectory(root);
        TestAssert.AreEqual("new", File.ReadAllText(Path.Combine(target, "metadata.bin")));
        TestAssert.IsFalse(File.Exists(Path.Combine(target, "extra.bin")));
    }

    [TestMethod]
    public void DirectoryCopyFailureDoesNotPublishPartialDataAndCanBeRetried()
    {
        var source = Path.Combine(root, "Nebula", "world-id");
        Directory.CreateDirectory(source);
        var blocked = Path.Combine(source, "transactions.bin");
        File.WriteAllText(Path.Combine(source, "metadata.bin"), "ledger");
        File.WriteAllText(blocked, "transactions");
        using (var held = new FileStream(blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            TestAssert.Throws<IOException>(() => LegacyDataMigration.DataDirectory(root));
        TestAssert.IsFalse(Directory.Exists(Path.Combine(root, "Nebulae")));
        TestAssert.IsEmpty(Directory.GetDirectories(root, ".nebulae-migration-*"));
        var target = LegacyDataMigration.DataDirectory(root);
        TestAssert.AreEqual("transactions", File.ReadAllText(Path.Combine(target, "world-id", "transactions.bin")));
        TestAssert.AreEqual("transactions", File.ReadAllText(blocked));
    }
}
