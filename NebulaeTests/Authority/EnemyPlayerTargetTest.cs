using NebulaeWorld.Combat;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>Verifies that <see cref="EnemyPlayerTarget"/> resolves multiplayer Player hatred targets by player
/// id.</summary>
[TestClass, DoNotParallelize]
public class EnemyPlayerTargetTest
{
    private const int PlayerType = (int)ETargetType.Player << 26;
    private const int CraftType = (int)ETargetType.Craft << 26;

    private static CombatManager.PlayerPosition[] Table() => new[]
    {
        new CombatManager.PlayerPosition
        {
            id = 2, isAlive = true, planetId = 101, starId = 1,
            position = new Vector3(30, 200, 0), uPosition = new VectorLF3(1000, 0, 0),
            mecha = new Mecha { energyShieldRadius = 12f }
        },
        new CombatManager.PlayerPosition
        {
            id = 7, isAlive = true, planetId = 101, starId = 1,
            position = new Vector3(2, 200, 0), uPosition = new VectorLF3(2000, 0, 0),
            mecha = new Mecha { energyShieldRadius = 5f }
        },
        new CombatManager.PlayerPosition
        {
            id = 9, isAlive = false, planetId = 101, starId = 1,
            position = new Vector3(1, 200, 0), uPosition = new VectorLF3(3000, 0, 0)
        }
    };

    [TestMethod]
    public void IsPlayerSeparatesTheTypeBitsFromTheId()
    {
        TestAssert.IsTrue(EnemyPlayerTarget.IsPlayer(PlayerType | 7));
        TestAssert.IsFalse(EnemyPlayerTarget.IsPlayer(CraftType | 7));
        TestAssert.IsFalse(EnemyPlayerTarget.IsPlayer(0));
        TestAssert.AreEqual(7, EnemyPlayerTarget.IdOf(PlayerType | 7));
    }

    [TestMethod]
    public void GroundResolvesTheHostSelectedPlayerOnTheSamePlanet()
    {
        var resolved = EnemyPlayerTarget.TryResolveGround(Table(), PlayerType | 7, 101, out var target, out var territory);
        TestAssert.IsTrue(resolved);
        TestAssert.AreEqual(new Vector3(2, 200, 0), target);
        TestAssert.AreEqual(5f, territory);
    }

    [TestMethod]
    public void GroundRefusesStaleDeadUnknownOrUnavailablePlayers()
    {
        // The patched sensor never hates a cross-planet player, so such an id is stale data.
        var table = Table();
        table[1].planetId = 202;
        TestAssert.IsFalse(EnemyPlayerTarget.TryResolveGround(table, PlayerType | 7, 101, out _, out _));
        TestAssert.IsFalse(EnemyPlayerTarget.TryResolveGround(Table(), PlayerType | 9, 101, out _, out _));
        TestAssert.IsFalse(EnemyPlayerTarget.TryResolveGround(Table(), PlayerType | 55, 101, out _, out _));
        TestAssert.IsFalse(EnemyPlayerTarget.TryResolveGround(Table(), PlayerType, 101, out _, out _));
        TestAssert.IsFalse(EnemyPlayerTarget.TryResolveGround(null, PlayerType | 7, 101, out _, out _));
        TestAssert.IsFalse(EnemyPlayerTarget.TryResolveGround(Array.Empty<CombatManager.PlayerPosition>(),
            PlayerType | 7, 101, out _, out _));
    }

    [TestMethod]
    public void SpaceResolvesAnyAlivePlayerByUniversalPosition()
    {
        // Vanilla's space branch only checks isAlive, so a cross-planet player still resolves.
        var table = Table();
        table[1].planetId = 202;
        var resolved = EnemyPlayerTarget.TryResolveSpace(table, PlayerType | 7, out var target, out var territory);
        TestAssert.IsTrue(resolved);
        TestAssert.AreEqual(new VectorLF3(2000, 0, 0), target);
        TestAssert.AreEqual(5f, territory);
    }

    [TestMethod]
    public void SpaceRefuesADeadPlayerAndReportsNoTerritoryWithoutAMecha()
    {
        TestAssert.IsFalse(EnemyPlayerTarget.TryResolveSpace(Table(), PlayerType | 9, out _, out _));
        var table = Table();
        table[1].mecha = null;
        TestAssert.IsTrue(EnemyPlayerTarget.TryResolveSpace(table, PlayerType | 7, out _, out var territory));
        TestAssert.AreEqual(0f, territory);
    }
}
