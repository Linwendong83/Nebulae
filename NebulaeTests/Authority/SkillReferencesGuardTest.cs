using HarmonyLib;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// Native skill teardown must never dereference a planet factory this peer does not have: the
/// failure happens before the skill leaves its pool, so one stranded projectile aborts the whole
/// SpaceSector task on every later frame.
/// </summary>
[TestClass]
public class SkillReferencesGuardTest
{
    private static bool AllowsCleanup(SkillSystem skills, int astroId)
    {
        var type = typeof(NebulaePatcher.NebulaePlugin).Assembly
            .GetType("NebulaePatcher.Patches.Authority.SkillReferencesGuard_Patch", true)!;
        return (bool)AccessTools.Method(type, "Prefix")!.Invoke(null, [skills, astroId])!;
    }

    [TestMethod]
    public void APlanetWithoutAFactoryNeedsNoReferenceCleanup()
    {
        var skills = Uninitialized<SkillSystem>();
        skills.astroFactories = new PlanetFactory[200];
        // A client empties astrosFactory when it leaves the star system.
        TestAssert.IsFalse(AllowsCleanup(skills, 101));

        // A live planet resolves the base references the native pass walks.
        skills.astroFactories[101] = new PlanetFactory { enemySystem = Uninitialized<EnemyDFGroundSystem>() };
        TestAssert.IsTrue(AllowsCleanup(skills, 101));

        // An out-of-range astro is never a real planet.
        TestAssert.IsFalse(AllowsCleanup(skills, 199));

        // A view without the factory array cannot be walked either.
        skills.astroFactories = null!;
        TestAssert.IsFalse(AllowsCleanup(skills, 101));

        // Non-planet astros keep their vanilla path untouched.
        TestAssert.IsTrue(AllowsCleanup(skills, 0));
    }
}
