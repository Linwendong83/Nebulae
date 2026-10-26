using System.Collections.Generic;
using System.IO;

namespace NebulaeModel.DataStructures;

internal enum BattleEffectKind : byte
{
    GroundLaser, GroundPlasma, GroundShieldPlasma, SpaceLaser, SpacePlasmaF, SpacePlasmaA,
    TurretMissile, TurretPlasma, LancerSweep, BomberProjectile, Impact,
    MechaGroundLaser, MechaGroundGauss, MechaSpaceLaser, MechaSpaceGauss, MechaPlasma, MechaMissile, MechaLocalCannon, MechaSpaceCannon, MechaShieldBurst, ExplosiveBomb, LiquidBomb, EMBomb,
    TurretGauss, TurretCannon, TurretLocalPlasma, TurretLaser, TurretDisturb,
    RaiderLaser, RangerPlasma, GuardianPlasma, GroundTowerLaser, GroundTowerPlasma,
    SpaceTowerLaser, SpaceTowerPlasma, LancerLaser, LancerPlasma
}

internal sealed class BattleEffectData
{
    public BattleEffectKind Kind;
    public int Id;
    public long Generation;
    public long Tick;
    public bool End;
    public int Subtype;
    public int Life;
    public int SourceAstroId;
    public BattleEffectKind OriginKind = BattleEffectKind.Impact;
    public int SourceObjectId;
    public long SourceGeneration;
    public long TargetGeneration;
    public long LaunchTick = -1;
    public int SourcePort = -1, MuzzleIndex = -1;
    public byte[] Payload;
    public static bool IsTurret(BattleEffectKind kind) => kind is BattleEffectKind.TurretGauss or
        BattleEffectKind.TurretCannon or BattleEffectKind.TurretLocalPlasma or BattleEffectKind.TurretLaser or
        BattleEffectKind.TurretDisturb or BattleEffectKind.TurretMissile or BattleEffectKind.TurretPlasma;
    public static bool IsEnemyUnit(BattleEffectKind kind) => kind is BattleEffectKind.RaiderLaser or
        BattleEffectKind.RangerPlasma or BattleEffectKind.GuardianPlasma or BattleEffectKind.LancerLaser or
        BattleEffectKind.LancerPlasma or BattleEffectKind.LancerSweep or BattleEffectKind.BomberProjectile;
    public static bool IsCraft(BattleEffectKind kind) => kind is BattleEffectKind.GroundLaser or BattleEffectKind.GroundPlasma or
        BattleEffectKind.GroundShieldPlasma or BattleEffectKind.SpaceLaser or BattleEffectKind.SpacePlasmaF or BattleEffectKind.SpacePlasmaA;
    public static bool IsDarkFogTurret(BattleEffectKind kind) => kind is BattleEffectKind.GroundTowerLaser or BattleEffectKind.GroundTowerPlasma or
        BattleEffectKind.SpaceTowerLaser or BattleEffectKind.SpaceTowerPlasma;

    public bool NeedsReplication(int localPlanet, bool simulatesTurrets) =>
        !simulatesTurrets || SourceAstroId <= 0 || SourceAstroId != localPlanet || !IsTurret(Kind);
    public static bool IsOneShotLaser(BattleEffectKind kind) => kind is BattleEffectKind.GroundLaser or
        BattleEffectKind.SpaceLaser or BattleEffectKind.MechaGroundLaser or BattleEffectKind.MechaSpaceLaser or
        BattleEffectKind.RaiderLaser or BattleEffectKind.GroundTowerLaser or BattleEffectKind.SpaceTowerLaser or BattleEffectKind.LancerLaser;

    /// <summary>These trajectories need only their start state and an authoritative end event.</summary>
    public static bool SimulatesLocally(BattleEffectKind kind) => IsOneShotLaser(kind) || kind is
        BattleEffectKind.GroundPlasma or BattleEffectKind.GroundShieldPlasma or BattleEffectKind.MechaGroundGauss or
        BattleEffectKind.TurretGauss or BattleEffectKind.RangerPlasma or BattleEffectKind.GuardianPlasma or
        BattleEffectKind.GroundTowerPlasma or BattleEffectKind.TurretCannon or BattleEffectKind.MechaLocalCannon or
        BattleEffectKind.MechaSpaceCannon or BattleEffectKind.TurretDisturb or BattleEffectKind.Impact;
}

internal sealed class BattleVisualFrame
{
    public const int MaxEffects = 16384;
    public List<BattleEffectData> Effects { get; } = new();

    public byte[] Export()
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(Effects.Count);
        foreach (var effect in Effects)
        {
            w.Write((byte)effect.Kind); w.Write(effect.Id); w.Write(effect.Generation); w.Write(effect.Tick); w.Write(effect.End); w.Write(effect.Subtype);
            w.Write(effect.Life); w.Write(effect.SourceAstroId); w.Write((byte)effect.OriginKind); w.Write(effect.SourceObjectId); w.Write(effect.SourceGeneration);
            if (BattleEffectData.IsCraft(effect.Kind))
            { w.Write(effect.TargetGeneration); w.Write(effect.LaunchTick); w.Write(effect.SourcePort); w.Write(effect.MuzzleIndex); }
            w.Write(effect.Payload.Length); w.Write(effect.Payload);
        }
        return stream.ToArray();
    }

    public static BattleVisualFrame Import(byte[] bytes)
    {
        if (bytes == null || bytes.Length < sizeof(int) || bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Invalid visual frame size");
        using var stream = new MemoryStream(bytes, false); using var r = new BinaryReader(stream);
        var frame = new BattleVisualFrame();
        var count = r.ReadInt32();
        if (count < 0 || count > MaxEffects) throw new InvalidDataException("Invalid effect count");
        for (var i = 0; i < count; i++)
        {
            var effect = new BattleEffectData
            {
                Kind = (BattleEffectKind)r.ReadByte(),
                Id = r.ReadInt32(),
                Generation = r.ReadInt64(),
                Tick = r.ReadInt64(),
                End = r.ReadBoolean(),
                Subtype = r.ReadInt32(),
                Life = r.ReadInt32(),
                SourceAstroId = r.ReadInt32(),
                OriginKind = (BattleEffectKind)r.ReadByte(),
                SourceObjectId = r.ReadInt32(),
                SourceGeneration = r.ReadInt64()
            };
            if (BattleEffectData.IsCraft(effect.Kind))
            {
                effect.TargetGeneration = r.ReadInt64(); effect.LaunchTick = r.ReadInt64();
                effect.SourcePort = r.ReadInt32(); effect.MuzzleIndex = r.ReadInt32();
                if (effect.TargetGeneration < 0 || effect.LaunchTick < -1 || effect.LaunchTick > effect.Tick ||
                    effect.SourcePort < -1 || effect.SourcePort > 255 || effect.MuzzleIndex < -1 || effect.MuzzleIndex > 255)
                    throw new InvalidDataException("Invalid craft shot identity");
            }
            var length = r.ReadInt32();
            if (effect.Kind > BattleEffectKind.LancerPlasma || effect.OriginKind > BattleEffectKind.LancerPlasma || effect.SourceObjectId < 0 || effect.SourceGeneration < 0 || effect.Id <= 0 || effect.Generation <= 0 || effect.Tick < 0 || effect.Life < 0 || effect.Life > 360000 || effect.SourceAstroId < 0 ||
                effect.End && (length != 0 || effect.Life != 0) || !effect.End && length == 0 ||
                length < 0 || length > 4096 || length > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid combat effect");
            effect.Payload = r.ReadBytes(length);
            frame.Effects.Add(effect);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing battle visual bytes");
        return frame;
    }

}
