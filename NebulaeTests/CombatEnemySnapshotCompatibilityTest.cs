using HarmonyLib;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class CombatEnemySnapshotCompatibilityTest
{
    [TestMethod]
    public void NativeEnemySnapshotContractsAreAvailable()
    {
        var zeroHp = AccessTools.Method(typeof(CombatStat), nameof(CombatStat.HandleZeroHp));
        TestAssert.IsNotNull(zeroHp);
        TestAssert.IsGreaterThan(5,
            zeroHp.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0, "Snapshot compatibility requires the installed game's real method bodies.");

        foreach (var type in new[]
        {
            typeof(EnemyData), typeof(EnemyBuilderComponent), typeof(EnemyUnitComponent),
            typeof(DFGBaseComponent), typeof(DFGConnectorComponent), typeof(DFGReplicatorComponent),
            typeof(DFGTurretComponent), typeof(DFGShieldComponent), typeof(DFSCoreComponent),
            typeof(DFSNodeComponent), typeof(DFSConnectorComponent), typeof(DFSReplicatorComponent),
            typeof(DFSGammaComponent), typeof(DFSTurretComponent), typeof(DFTinderComponent),
            typeof(DFRelayComponent)
        })
        {
            TestAssert.IsNotNull(AccessTools.Method(type, "Export", [typeof(BinaryWriter)]), type.Name);
            TestAssert.IsNotNull(AccessTools.Method(type, "Import", [typeof(BinaryReader)]), type.Name);
        }

        foreach (var (system, names) in new[]
        {
            (typeof(EnemyDFGroundSystem), new[] { "builders", "bases", "connectors", "replicators", "turrets", "shields", "units" }),
            (typeof(EnemyDFHiveSystem), new[] { "builders", "cores", "nodes", "connectors", "replicators", "gammas", "turrets", "tinders", "relays", "units" })
        })
        {
            foreach (var name in names)
            {
                var pool = AccessTools.Field(system, name);
                TestAssert.IsNotNull(pool, system.Name + "." + name);
                TestAssert.IsNotNull(AccessTools.Field(pool.FieldType, "buffer"), pool.FieldType.Name);
            }
            // The client's unit shells are made hoverable — and released again with the shell — through
            // these vanilla entry points; a game update that renames or removes them must fail here
            // rather than silently dropping every dark-fog unit's nameplate and selection.
            TestAssert.IsNotNull(AccessTools.Method(system, "NewEnemyUnitComponent", [typeof(int)]), system.Name);
            TestAssert.IsNotNull(AccessTools.Method(system, "RemoveEnemyUnitComponent", [typeof(int)]), system.Name);
        }

        var enemy = new EnemyData { id = 17, protoId = 8128, originAstroId = 101 };
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) enemy.Export(writer);
        stream.Position = 0;
        var restored = new EnemyData();
        using (var reader = new BinaryReader(stream)) restored.Import(reader);
        TestAssert.AreEqual(enemy.id, restored.id);
        TestAssert.AreEqual(enemy.protoId, restored.protoId);
        TestAssert.AreEqual(enemy.originAstroId, restored.originAstroId);
    }
}
