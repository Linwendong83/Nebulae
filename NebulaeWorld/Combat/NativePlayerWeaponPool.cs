using System.Collections.Generic;
using System.IO;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;

namespace NebulaeWorld.Combat;

/// <summary>Adapters bind native skills to local pool slots; no copied trajectory implementation.</summary>
internal abstract class NativePlayerWeaponPool
{
    public BattleEffectKind Kind;
    public abstract int Cursor { get; }
    public abstract int Recycled { get; }
    public abstract int RecycledId(int slot);
    public abstract bool Exists(int id);
    public abstract IPoolElement Read(int id);
    protected abstract void Write(IPoolElement value);
    public abstract int Install(byte[] payload, int id = 0, ObjectKey? expectedTarget = null);
    public abstract void Remove(int id);
    public void Advance(int id, SkillSystem s, long from, int ticks)
    {
        var origin = GameMain.data.relativePos; var rotation = GameMain.data.relativeRot;
        for (var i = 0; i < ticks && Exists(id); i++)
        {
            var value = Read(id);
            if (Life(value) == 0) break;
            switch (value)
            {
                case LocalGeneralProjectile p: p.TickSkillLogic(s); value = p; break;
                case LocalLaserOneShot p: p.TickSkillLogic(s, s.astroFactories); value = p; break;
                case SpaceLaserOneShot p: p.TickSkillLogic(s, ref origin, ref rotation); value = p; break;
                case GeneralProjectile p: p.TickSkillLogic(s, GameMain.data, ref origin, ref rotation); value = p; break;
                case GeneralMissile p:
                    s.mechaMissileTrails.SetTrailCapacity(s.mechaMissiles.capacity);
                    p.TickSkillLogic(s, s.mechaMissileTrails, s.sector, ref origin, ref rotation, from + i); value = p; break;
                case LocalCannonade p: p.TickSkillLogic(s, s.astroFactories); value = p; break;
                case GeneralCannonade p: p.TickSkillLogic(s, ref origin, ref rotation); value = p; break;
                case Bomb_Explosive p: p.TickSkillLogic(s, from + i); value = p; break;
                case Bomb_Liquid p: p.TickSkillLogic(s, from + i); value = p; break;
                case Bomb_EMCapsule p: p.TickSkillLogic(s, from + i); value = p; break;
                case GeneralShieldBurst p: p.TickSkillLogic(s); value = p; break;
            }
            Write(value);
        }
        if (Exists(id) && Life(Read(id)) == 0) Remove(id);
    }
    public static int Life(IPoolElement value) => value switch
    {
        LocalGeneralProjectile p => p.life,
        LocalLaserOneShot p => p.life,
        SpaceLaserOneShot p => p.life,
        GeneralProjectile p => p.life,
        GeneralMissile p => p.life,
        LocalCannonade p => p.life,
        GeneralCannonade p => p.life,
        Bomb_Explosive p => p.life,
        Bomb_Liquid p => p.life,
        Bomb_EMCapsule p => p.life,
        GeneralShieldBurst p => p.life,
        _ => 0
    };
    public byte[] Export(int id)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        Read(id).Export(w); return stream.ToArray();
    }
    public IEnumerable<int> Added(int cursor, int recycled)
    {
        for (var id = cursor; id < Cursor; id++) if (Exists(id)) yield return id;
        for (var slot = Recycled; slot < recycled; slot++)
        { var id = RecycledId(slot); if (Exists(id)) yield return id; }
    }
    private sealed class Pool<T> : NativePlayerWeaponPool where T : struct, IPoolElement
    {
        private readonly DataPool<T> pool;
        private readonly DataPoolRenderer<T> renderer;
        private T[] Buffer => pool != null ? pool.buffer : renderer.buffer;
        public Pool(BattleEffectKind kind, DataPool<T> value) { Kind = kind; pool = value; }
        public Pool(BattleEffectKind kind, DataPoolRenderer<T> value) { Kind = kind; renderer = value; }
        public override int Cursor => pool != null ? pool.cursor : renderer.cursor;
        public override int Recycled => pool != null ? pool.recycleCursor : renderer.recycleCursor;
        public override int RecycledId(int slot) => (pool != null ? pool.recycleIds : renderer.recycleIds)[slot];
        public override bool Exists(int id) => id > 0 && id < Cursor && Buffer[id].ID == id;
        public override IPoolElement Read(int id) => Buffer[id];
        protected override void Write(IPoolElement value) => Buffer[value.ID] = (T)value;
        public override void Remove(int id) { if (Exists(id)) { if (pool != null) pool.Remove(id); else renderer.Remove(id); } }
        public override int Install(byte[] payload, int id = 0, ObjectKey? expectedTarget = null)
        {
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            var value = default(T); value.Import(r);
            if (stream.Position != stream.Length || value.ID <= 0) throw new InvalidDataException("Invalid native weapon");
            object box = value;
            // These are process-local references, never host references on a client. Retain damage
            // and masks: vanilla needs both for collision and particle sizing. Write guards own HP.
            switch (box)
            {
                case LocalGeneralProjectile p: p.targetCombatStatId = 0; p.damageIncoming = 0; box = p; break;
                case GeneralProjectile p: p.targetCombatStatId = 0; p.damageIncoming = 0; box = p; break;
                case GeneralMissile p:
                    p.targetCombatStatId = 0; p.damageIncoming = 0;
                    // Apply the host's trajectory even when its target has died or its slot
                    // now holds a different enemy. Native flight can then continue/reacquire.
                    if (expectedTarget.HasValue && p.target.type == ETargetType.Enemy && p.target.id > 0 &&
                        (!EnemyTargetIdentity.TryResolve(p.target, out var key) || key != expectedTarget.Value)) p.target.id = 0;
                    box = p; break;
            }
            value = (T)box;
            if (!Exists(id)) id = pool != null ? pool.Add().ID : renderer.Add().ID;
            // A correction may deliberately retarget the same missile to a new generation.
            if (expectedTarget.HasValue && pool is DataPool<GeneralMissile> missiles) EnemyTargetIdentity.ForgetMissile(missiles, id);
            value.ID = id; Buffer[id] = value;
            return id;
        }
    }

    public static NativePlayerWeaponPool[] Create(SkillSystem s) =>
    [
        new Pool<LocalLaserOneShot>(BattleEffectKind.MechaGroundLaser, s.mechaLocalLaserOneShots),
        new Pool<LocalGeneralProjectile>(BattleEffectKind.MechaGroundGauss, s.mechaLocalGaussProjectiles),
        new Pool<SpaceLaserOneShot>(BattleEffectKind.MechaSpaceLaser, s.mechaSpaceLaserOneShots),
        new Pool<GeneralProjectile>(BattleEffectKind.MechaSpaceGauss, s.mechaSpaceGaussProjectiles),
        new Pool<GeneralProjectile>(BattleEffectKind.MechaPlasma, s.mechaPlasmas),
        new Pool<GeneralMissile>(BattleEffectKind.MechaMissile, s.mechaMissiles),
        new Pool<LocalCannonade>(BattleEffectKind.MechaLocalCannon, s.mechaLocalCannonades),
        new Pool<GeneralCannonade>(BattleEffectKind.MechaSpaceCannon, s.mechaSpaceCannonades),
        new Pool<GeneralShieldBurst>(BattleEffectKind.MechaShieldBurst, s.mechaShieldBursts),
        new Pool<Bomb_Explosive>(BattleEffectKind.ExplosiveBomb, s.explosiveUnitBombs),
        new Pool<Bomb_Liquid>(BattleEffectKind.LiquidBomb, s.liquidBombs),
        new Pool<Bomb_EMCapsule>(BattleEffectKind.EMBomb, s.emCapsuleBombs)
    ];

    public static int Astro(IPoolElement skill) => skill switch
    {
        LocalLaserOneShot x => x.astroId,
        LocalGeneralProjectile x => x.astroId,
        LocalCannonade x => x.astroId,
        Bomb_Explosive x => x.nearPlanetAstroId,
        Bomb_Liquid x => x.nearPlanetAstroId,
        Bomb_EMCapsule x => x.nearPlanetAstroId,
        GeneralShieldBurst x => SpaceSector.IsPlanetAstro(x.astroId) ? x.astroId : 0,
        _ => 0
    };
    public static NativePlayerWeaponPool[] WorldEffects(SkillSystem s) =>
    [
        new Pool<LocalLaserOneShot>(BattleEffectKind.RaiderLaser, s.raiderLasers),
        new Pool<LocalLaserOneShot>(BattleEffectKind.GroundTowerLaser, s.dfgTowerLasers),
        new Pool<LocalGeneralProjectile>(BattleEffectKind.RangerPlasma, s.rangerPlasmas),
        new Pool<LocalGeneralProjectile>(BattleEffectKind.GuardianPlasma, s.guardianPlasmas),
        new Pool<LocalGeneralProjectile>(BattleEffectKind.GroundTowerPlasma, s.dfgTowerPlasmas),
        new Pool<SpaceLaserOneShot>(BattleEffectKind.SpaceTowerLaser, s.dfsTowerLasers),
        new Pool<SpaceLaserOneShot>(BattleEffectKind.LancerLaser, s.lancerLaserOneShots),
        new Pool<GeneralProjectile>(BattleEffectKind.SpaceTowerPlasma, s.dfsTowerPlasmas),
        new Pool<GeneralProjectile>(BattleEffectKind.LancerPlasma, s.lancerSpacePlasma),
        new Pool<LocalLaserOneShot>(BattleEffectKind.GroundLaser, s.fighterLasers),
        new Pool<LocalGeneralProjectile>(BattleEffectKind.GroundPlasma, s.fighterPlasmas),
        new Pool<LocalGeneralProjectile>(BattleEffectKind.GroundShieldPlasma, s.fighterShieldPlasmas),
        new Pool<SpaceLaserOneShot>(BattleEffectKind.SpaceLaser, s.warshipTypeFLasers),
        new Pool<GeneralProjectile>(BattleEffectKind.SpacePlasmaF, s.warshipTypeFPlasmas),
        new Pool<GeneralProjectile>(BattleEffectKind.SpacePlasmaA, s.warshipTypeAPlasmas),
        new Pool<SpaceLaserSweep>(BattleEffectKind.LancerSweep, s.lancerLaserSweeps),
        new Pool<GeneralExpImpProjectile>(BattleEffectKind.BomberProjectile, s.humpbackProjectiles)
    ];
    public static int RequiredPlanet(IPoolElement skill)
    {
        VectorLF3 position, velocity; int star;
        switch (skill)
        {
            case Bomb_Explosive bomb: position = bomb.uPos; velocity = bomb.uVel; star = bomb.nearStarId; break;
            case Bomb_Liquid bomb: position = bomb.uPos; velocity = bomb.uVel; star = bomb.nearStarId; break;
            case Bomb_EMCapsule bomb: position = bomb.uPos; velocity = bomb.uVel; star = bomb.nearStarId; break;
            default: return Astro(skill);
        }
        var planets = GameMain.galaxy.StarById(star)?.planets;
        if (planets == null) return Astro(skill);
        foreach (var planet in planets)
            if (planet.type != EPlanetType.Gas && (position + velocity / 60.0 - planet.uPosition).sqrMagnitude < 1000000)
                return planet.id;
        return 0;
    }
    public static bool IsLocal(BattleEffectKind kind) => kind is BattleEffectKind.MechaGroundLaser or BattleEffectKind.MechaGroundGauss or BattleEffectKind.MechaLocalCannon;
}
