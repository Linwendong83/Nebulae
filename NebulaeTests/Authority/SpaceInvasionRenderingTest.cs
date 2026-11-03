using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using global::NebulaeModel.DataStructures;
using NebulaeWorld;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

[TestClass, DoNotParallelize]
public class SpaceInvasionRenderingTest
{
    private static readonly HashSet<object> drawn = new();

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeAndDetachedAttacksRemainVisibleAcrossReplicationReadiness(bool ready)
    {
        var oldGame = GameMain.data;
        var oldSession = Multiplayer.Session;
        var harmony = new Harmony("nebulae.tests.space-invasion-rendering");
        try
        {
            var game = Uninitialized<GameData>();
            var sector = Uninitialized<SpaceSector>();
            AccessTools.Field(typeof(GameData), "spaceSector").SetValue(game, sector);
            var native = sector.skillSystem = Skills();
            var detached = Skills();
            GameMain.data = game;
            var session = Uninitialized<MultiplayerSession>();
            session.Client = Uninitialized<global::NebulaeNetwork.Client>();
            Multiplayer.Session = session;

            var targets = new HashSet<MethodBase>
            {
                AccessTools.Method(typeof(SkillSystem), nameof(SkillSystem.UpdateTurretMissileRenderingData)),
                AccessTools.Method(typeof(SkillSystem), nameof(SkillSystem.UpdateLancerLaserSweepRenderingData)),
                AccessTools.Method(typeof(DataPoolRenderer<GeneralProjectile>), nameof(DataPoolRenderer<GeneralProjectile>.Render)),
                AccessTools.Method(typeof(DataPoolRenderer<GeneralExpImpProjectile>), nameof(DataPoolRenderer<GeneralExpImpProjectile>.Render))
            };
            // Replace only GPU-facing bodies. All production prefixes and postfixes still run,
            // including any that could hide the populated native or detached renderers.
            foreach (var target in targets)
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(SpaceInvasionRenderingTest), nameof(RenderBody)));
            foreach (var patch in typeof(NebulaePatcher.NebulaePlugin).Assembly.GetTypes().Where(type =>
                         type.Namespace?.StartsWith("NebulaePatcher.Patches.") == true &&
                         type.GetCustomAttributesData().Any(attribute => attribute.AttributeType == typeof(HarmonyPatch))))
                if (DeclaredPatches(patch).Any(member => member.Target != null && targets.Contains(member.Target)))
                    harmony.CreateClassProcessor(patch).Patch();

            foreach (var kind in new[] { BattleEffectKind.LancerSweep, BattleEffectKind.BomberProjectile })
                TestAssert.AreEqual(ready, BattleVisualManager.NativeEnemyEffect(kind, 1000001, 101, false, ready));
            foreach (var kind in new[] { BattleEffectKind.TurretMissile, BattleEffectKind.TurretPlasma })
                TestAssert.AreEqual(!ready, new BattleEffectData { Kind = kind, SourceAstroId = 101 }.NeedsReplication(101, ready));

            drawn.Clear();
            foreach (var skills in new[] { native, detached })
            {
                skills.UpdateTurretMissileRenderingData();
                TestAssert.AreEqual(1, skills.turretMissileRenderer.instCursor, "A populated missile renderer must reach drawing.");
                skills.UpdateLancerLaserSweepRenderingData();
                TestAssert.AreEqual(1, skills.lancerLaserSweepRenderer.instCursor, "A populated sweep renderer must reach drawing.");
                skills.turretPlasmas.Render();
                TestAssert.Contains(skills.turretPlasmas, drawn, "Plasma drawing must reach the renderer body.");
                skills.humpbackProjectiles.Render();
                TestAssert.Contains(skills.humpbackProjectiles, drawn, "Bomber drawing must reach the renderer body.");
            }
        }
        finally
        {
            harmony.UnpatchSelf();
            drawn.Clear();
            GameMain.data = oldGame;
            Multiplayer.Session = oldSession;
        }
    }

    private static SkillSystem Skills()
    {
        var skills = Uninitialized<SkillSystem>();
        skills.turretMissileRenderer = Uninitialized<GenericRenderer<GeneralMissileRenderingData>>();
        skills.lancerLaserSweepRenderer = Uninitialized<GenericRenderer<SpaceLaserSweepRenderingData>>();
        skills.turretPlasmas = new DataPoolRenderer<GeneralProjectile>();
        skills.humpbackProjectiles = new DataPoolRenderer<GeneralExpImpProjectile>();
        return skills;
    }

    private static IEnumerable<CodeInstruction> RenderBody() => new[]
    {
        new CodeInstruction(OpCodes.Ldarg_0),
        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SpaceInvasionRenderingTest), nameof(Draw))),
        new CodeInstruction(OpCodes.Ret)
    };

    private static void Draw(object value)
    {
        if (value is SkillSystem skills)
        {
            skills.turretMissileRenderer.instCursor = 1;
            skills.lancerLaserSweepRenderer.instCursor = 1;
        }
        else drawn.Add(value);
    }
}
