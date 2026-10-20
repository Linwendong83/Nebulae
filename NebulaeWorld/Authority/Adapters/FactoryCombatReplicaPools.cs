#region

using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;

#endregion

namespace NebulaeWorld.Authority.Adapters;

/// <summary>
/// The client-local pool mechanics behind <see cref="FactoryCombatBinding"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every method here touches vanilla pools; none of them decides <em>whether</em> a write should
/// happen — that is the binding core's call. The seam keeps the core testable without a game
/// process and keeps the vanilla access in one reviewable place.
/// </para>
/// <para>
/// Two vanilla behaviors matter to correctness here. <c>DataPool.Add</c> resets the element and
/// assigns the id, so a freshly allocated stat is clean before the canonical values land. And the
/// client's stat pool indexes have no relation to the host's, which is why nothing in this class
/// accepts a host pool index: stats are allocated locally and rebound by <see cref="ObjectKey"/>.
/// </para>
/// <para>
/// Display-only fields the host does not ship (the HP bar position and width) are initialized from
/// the local entity at allocation.
/// </para>
/// </remarks>
internal sealed class FactoryCombatReplicaPools : IFactoryCombatPools
{
    private readonly int planetId;

    public FactoryCombatReplicaPools(int planetId)
    {
        this.planetId = planetId;
    }

    public bool EntityExists(int entityId)
    {
        var factory = Factory;
        return factory != null && entityId < factory.entityCursor && factory.entityPool[entityId].id == entityId;
    }

    public int ReadEntityCombatStat(int entityId) => Factory.entityPool[entityId].combatStatId;

    public void WriteEntityCombatStat(int entityId, int statId)
    {
        Factory.entityPool[entityId].combatStatId = statId;
    }

    public int AllocateCombatStat()
    {
        // DataPool.Add resets the element and assigns the id; the canonical values and the local
        // display anchor are written by WriteCombatStat, which knows the entity.
        return GameMain.data.spaceSector.skillSystem.combatStats.Add().id;
    }

    public void ReleaseCombatStat(int statId)
    {
        GameMain.data.spaceSector.skillSystem.combatStats.Remove(statId);
    }

    public void WriteCombatStat(int statId, in FactoryCombatState state, int planetId, int entityId)
    {
        var stats = GameMain.data.spaceSector.skillSystem.combatStats;
        ref var stat = ref stats.buffer[statId];
        stat.hp = state.Hp;
        stat.hpMax = state.HpMax;
        stat.hpRecover = state.HpRecover;
        stat.hpIncoming = state.HpIncoming;
        stat.astroId = stat.originAstroId = planetId;
        stat.objectType = (int)EObjectType.Entity;
        stat.objectId = entityId;
        // Local display anchor for the HP bar; the host does not ship it.
        stat.localPos = Factory.entityPool[entityId].pos;
        stat.size = 1f;
        // lastCaster/lastImpact stay local: they are rule inputs on the host, never on the client.
    }

    public int ReadEntityConstructStat(int entityId) => Factory.entityPool[entityId].constructStatId;

    public void WriteEntityConstructStat(int entityId, int statId)
    {
        Factory.entityPool[entityId].constructStatId = statId;
    }

    public int AllocateConstructStat()
    {
        ref var construct = ref Factory.constructionSystem.constructStats.Add();
        return construct.id;
    }

    public void ReleaseConstructStat(int statId)
    {
        // The pool's own Remove, not ConstructionSystem.RemoveConstructStat: that one also clears
        // module repair queues and warnings, which are rule-side effects the client must not run.
        Factory.constructionSystem.constructStats.Remove(statId);
    }

    public void WriteConstructStat(int statId, in FactoryCombatState state, int entityId)
    {
        ref var construct = ref Factory.constructionSystem.constructStats.buffer[statId];
        construct.entityId = entityId;
        construct.damageRate = state.DamageRate;
        construct.repairerCount = state.RepairerCount;
        construct.repairerModuleId = state.RepairerModuleId;
        construct.repairerValue = state.RepairerValue;
        // damageRegister/damageRateLastFrame are the host's tick-local decay state; the replicated
        // rate is the published result, so the client does not re-decay anything.
    }

    private PlanetFactory Factory => GameMain.galaxy?.PlanetById(planetId)?.factory;
}
