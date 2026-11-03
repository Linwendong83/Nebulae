using HarmonyLib;
using global::NebulaeModel.Packets.Authority;
using global::NebulaeModel.Utils;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class GalaxyEngineLimitsTest
{
    private readonly Harmony harmony = new("nebulae.tests.galaxy-engine-limits");
    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public void Cleanup()
    {
        try { harmony.UnpatchSelf(); }
        catch (Exception failure) { TestAssert.Fail("Unpatch: " + failure); }
    }

    private void Patch(Type type)
    {
        TestContext.WriteLine("Installing " + type.Name);
        try { harmony.CreateClassProcessor(type).Patch(); }
        catch (Exception failure) { TestAssert.Fail(type.Name + ": " + failure); }
    }

    [TestMethod]
    public void ManagedBoundaryPatchesInstallOnRealGameBodies()
    {
        Patch(typeof(GalaxyAstroClassification_Patch));
        // CalcGlobalSunLight embeds Mono-only camera ECalls. Its prefix is tested
        // below, and installation/execution is checked in the real game process.
        Patch(typeof(GalaxyActiveColliderCapacity_Patch));
        Patch(typeof(GalaxyActiveAudioCapacity_Patch));
        Patch(typeof(GalaxySpaceFormation_Patch));
        Patch(typeof(GalaxyTemporarySpaceEnemy_Patch));
        foreach (var target in GalaxyTemporarySpaceEnemy_Patch.TargetMethods()
                     .Concat(GalaxyAstroClassification_Patch.TargetMethods()))
            TestAssert.AreEqual(1, Harmony.GetPatchInfo(target).Transpilers.Count(patch => patch.owner == harmony.Id));
    }

    [TestMethod]
    [DataRow(1024)]
    [DataRow(1025)]
    [DataRow(2047)]
    [DataRow(2048)]
    [DataRow(2049)]
    [DataRow(4095)]
    [DataRow(4096)]
    [DataRow(4097)]
    [DataRow(8191)]
    public void GalaxyAstrosKeepTheirClassificationAndHashLayout(int stars)
    {
        Patch(typeof(GalaxyAstroClassification_Patch));
        var star = stars * 100;
        foreach (var astro in new[] { star, star + 1, star + 99 })
        {
            TestAssert.IsTrue(Invoke<bool>(typeof(SpaceSector), nameof(SpaceSector.IsGalaxyAstro), astro));
            TestAssert.AreEqual(astro % 100 == 0, Invoke<bool>(typeof(SpaceSector), nameof(SpaceSector.IsStarAstro), astro));
            TestAssert.AreEqual(astro % 100 != 0, Invoke<bool>(typeof(SpaceSector), nameof(SpaceSector.IsPlanetAstro), astro));
            TestAssert.IsFalse(Invoke<bool>(typeof(SpaceSector), nameof(SpaceSector.IsSectorAstro), astro));
            TestAssert.AreEqual("G" + astro, Invoke<string>(typeof(AstroData), nameof(AstroData.IdString), astro));
            TestAssert.AreEqual(0x4000 | ((stars & 0x7F) << 7) | ((astro % 100) & 0xF),
                Invoke<int>(typeof(SectorPhysics), nameof(SectorPhysics.HashPhysBlock), astro));
            TestAssert.AreEqual(0x4000 | ((stars & 0x7F) << 7),
                Invoke<int>(typeof(SectorAudio), nameof(SectorAudio.HashAuBlock), astro));
        }
        foreach (var astro in new[] { 99, 819200, 900000, 1000000, 1000001 })
            TestAssert.IsFalse(Invoke<bool>(typeof(SpaceSector), nameof(SpaceSector.IsGalaxyAstro), astro));
        TestAssert.AreEqual("S65528", Invoke<string>(typeof(AstroData), nameof(AstroData.IdString), 1065528));
    }

    private static T Invoke<T>(Type type, string name, int value) =>
        (T)AccessTools.Method(type, name, new[] { typeof(int) }).Invoke(null, new object[] { value });

    [TestMethod]
    public void SharedPhysicsBucketsKeepFullAstroIdsAndPositiveColliderIds()
    {
        Patch(typeof(GalaxyAstroClassification_Patch));
        var physics = new SectorPhysics(null!) { colChunks = new ColliderContainerLF[32768] };
        var ids = new[] { 101, 204901, 409701, 819101, 819199, 1000001, 1032769, 1065528 };
        var handles = ids.Select((astro, index) => physics.AddColliderData(new ColliderDataLF
        { astroId = astro, idType = index + 1 })).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            TestAssert.IsGreaterThan(0, handles[i]);
            TestAssert.AreEqual(ids[i], physics.GetColliderData(handles[i]).astroId);
        }
        physics.RemoveColliderData(handles[1]);
        TestAssert.AreEqual(ids[0], physics.GetColliderData(handles[0]).astroId);
        TestAssert.AreEqual(ids[2], physics.GetColliderData(handles[2]).astroId);
    }

    [TestMethod]
    public void ActiveHashListsGrowPastTheirNative768Entries()
    {
        Patch(typeof(GalaxyActiveColliderCapacity_Patch));
        Patch(typeof(GalaxyActiveAudioCapacity_Patch));
        var collider = Uninitialized<SpaceColliderLogic>();
        var audio = Uninitialized<SpaceAudioLogic>();
        SetField(collider, "activeColHashes", new int[768]);
        SetField(audio, "activeAuHashes", new int[768]);
        for (var i = 0; i < 1000; i++)
        {
            var astro = 1000001 + i;
            AccessTools.Method(typeof(SpaceColliderLogic), "MarkActivePos").Invoke(collider, new object[] { astro });
            AccessTools.Method(typeof(SpaceAudioLogic), "MarkActivePos").Invoke(audio, new object[] { astro });
        }
        TestAssert.AreEqual(1000, AccessTools.Field(typeof(SpaceColliderLogic), "activeColHashCount").GetValue(collider));
        TestAssert.AreEqual(1000, AccessTools.Field(typeof(SpaceAudioLogic), "activeAuHashCount").GetValue(audio));
        TestAssert.AreEqual(999, ((int[])AccessTools.Field(typeof(SpaceColliderLogic), "activeColHashes").GetValue(collider))[998]);
    }

    [TestMethod]
    public void SunLightCacheGrowsForTheLoadedGalaxyAndRetainsExistingEntries()
    {
        var model = Uninitialized<SectorModel>();
        model.gameData = Uninitialized<GameData>();
        model.gameData.galaxy = new GalaxyData { stars = new StarData[8191] };
        var distances = new double[2048]; distances[2047] = 42;
        GalaxySunLightCapacity_Patch.Prefix(model, ref distances);
        TestAssert.HasCount(8191, distances);
        TestAssert.AreEqual(42d, distances[2047]);
        distances[8190] = 123;
        GalaxySunLightCapacity_Patch.Prefix(model, ref distances);
        TestAssert.AreEqual(123d, distances[8190]);
    }

    [TestMethod]
    public void HeadlessTerrainChangesSkipOnlyTheGpuHeightmapPass()
    {
        var previous = NebulaeWorld.Multiplayer.IsDedicated;
        try
        {
            NebulaeWorld.Multiplayer.IsDedicated = true;
            TestAssert.IsFalse(HeadlessHeightmap_Patch.Prefix());
            NebulaeWorld.Multiplayer.IsDedicated = false;
            TestAssert.IsTrue(HeadlessHeightmap_Patch.Prefix());
        }
        finally { NebulaeWorld.Multiplayer.IsDedicated = previous; }
    }

    private static (VectorLF3 Position, Quaternion Rotation, Vector3 Velocity) Formation(EnemyData enemy, int ticks)
    {
        VectorLF3 position = default; Quaternion rotation = default; Vector3 velocity = default;
        enemy.Formation(ticks, 16000f, ref position, ref rotation, ref velocity);
        return (position, rotation, velocity);
    }

    [TestMethod]
    [DataRow(32767)]
    [DataRow(32768)]
    [DataRow(65527)]
    [DataRow(65528)]
    public void SpaceFormationUsesFullHiveIndexAcrossSignedShortBoundary(int hiveIndex)
    {
        // Native formation phase is ticks + owner * 19351. Move the full phase
        // into ticks with owner zero to obtain an independent unpatched reference.
        var enemy = new EnemyData { protoId = 8112, port = 17, isSpace = true };
        var expected = Formation(enemy, checked(1234 + hiveIndex * 19351));
        GalaxyLimits.SetSpaceFormationOwner(ref enemy, hiveIndex);
        Patch(typeof(GalaxySpaceFormation_Patch));
        TestAssert.AreEqual(expected, Formation(enemy, 1234));
        enemy.astroId = 0; // Detached units still belong to their original hive.
        TestAssert.AreEqual(hiveIndex, GalaxyLimits.SpaceFormationOwner(ref enemy));
    }

    [TestMethod]
    public void ExistingSpaceFormationsAreBitForBitUnchanged()
    {
        var samples = new List<(EnemyData Enemy, (VectorLF3, Quaternion, Vector3) Pose)>();
        foreach (var owner in new[] { 1, 512, 8192, 32767 })
            // Relay formation (8111) calls Unity's native Quaternion.LookRotation.
            // Exercise it in the game process; the managed test host covers both unit forms.
            foreach (var proto in new short[] { 8112, 8113 })
            {
                var enemy = new EnemyData
                {
                    protoId = proto,
                    port = 17,
                    owner = (short)owner,
                    originAstroId = 1000000 + owner,
                    isSpace = true
                };
                samples.Add((enemy, Formation(enemy, 1234)));
            }
        Patch(typeof(GalaxySpaceFormation_Patch));
        foreach (var sample in samples) TestAssert.AreEqual(sample.Pose, Formation(sample.Enemy, 1234));
    }

    [TestMethod]
    [DataRow(32767)]
    [DataRow(32768)]
    [DataRow(65527)]
    [DataRow(65528)]
    public void NativeSaveAndReplicaStatePreserveFullHiveIdentity(int hiveIndex)
    {
        var enemy = new EnemyData
        {
            id = 1,
            protoId = 8112,
            modelIndex = 284,
            isSpace = true,
            dynamic = true,
            port = 1,
            rot = Quaternion.identity
        };
        GalaxyLimits.SetSpaceFormationOwner(ref enemy, hiveIndex);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        enemy.Export(writer); writer.Flush(); stream.Position = 0;
        var loaded = new EnemyData(); loaded.Import(new BinaryReader(stream));
        TestAssert.AreEqual(hiveIndex, GalaxyLimits.SpaceFormationOwner(ref loaded));
        TestAssert.AreEqual(enemy.owner, loaded.owner);
        var state = new SpaceEnemyState(SpaceEnemyKind.SpaceUnit, true, true,
            enemy.protoId, enemy.modelIndex, enemy.owner, enemy.port, 0, 0, enemy.originAstroId, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 100, 100, 0, 0);
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(state, out var bytes));
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryDecode(bytes, 0, bytes.Length, out var received, out var reject), reject.ToString());
        loaded.originAstroId = received.OriginAstroId; loaded.owner = received.Owner;
        TestAssert.AreEqual(hiveIndex, GalaxyLimits.SpaceFormationOwner(ref loaded));
    }

}
