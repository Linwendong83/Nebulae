using NebulaeModel.Authority;

namespace NebulaeModel.Packets.Authority;

/// <summary>Settled manufacture and travel facts; no client decision or resource simulation.</summary>
internal readonly struct SpaceEnemyActivity
{
    public readonly int RelayCount, TinderCount, RelaySp, RelaySpMax, TinderSp, TinderSpMax;
    public readonly int Stage, Direction, TargetAstroId, BaseId, BaseState;

    public SpaceEnemyActivity(int relayCount = 0, int tinderCount = 0, int relaySp = 0, int relaySpMax = 0,
        int tinderSp = 0, int tinderSpMax = 0, int stage = 0, int direction = 0, int targetAstroId = 0,
        int baseId = 0, int baseState = 0)
    {
        RelayCount = relayCount; TinderCount = tinderCount; RelaySp = relaySp; RelaySpMax = relaySpMax;
        TinderSp = tinderSp; TinderSpMax = tinderSpMax; Stage = stage; Direction = direction;
        TargetAstroId = targetAstroId; BaseId = baseId; BaseState = baseState;
    }

    public bool Same(in SpaceEnemyActivity other) => RelayCount == other.RelayCount && TinderCount == other.TinderCount &&
        RelaySp == other.RelaySp && RelaySpMax == other.RelaySpMax && TinderSp == other.TinderSp && TinderSpMax == other.TinderSpMax &&
        Stage == other.Stage && Direction == other.Direction && TargetAstroId == other.TargetAstroId && BaseId == other.BaseId && BaseState == other.BaseState;

    public static int SizeFor(SpaceEnemyKind kind) => kind switch
    { SpaceEnemyKind.SpaceCore => 24, SpaceEnemyKind.SpaceRelay => 11, SpaceEnemyKind.SpaceTinder => 6, _ => 0 };

    public bool Valid(SpaceEnemyKind kind)
    {
        if (kind == SpaceEnemyKind.SpaceCore)
            return RelayCount >= 0 && TinderCount >= 0 && RelaySp >= 0 && RelaySp <= RelaySpMax && TinderSp >= 0 && TinderSp <= TinderSpMax &&
                Stage == 0 && Direction == 0 && TargetAstroId == 0 && BaseId == 0 && BaseState == 0;
        if (kind is not (SpaceEnemyKind.SpaceRelay or SpaceEnemyKind.SpaceTinder)) return Same(default);
        return RelayCount == 0 && TinderCount == 0 && RelaySp == 0 && RelaySpMax == 0 && TinderSp == 0 && TinderSpMax == 0 &&
            Stage >= -2 && Stage <= 2 && Direction >= -1 && Direction <= 1 &&
            (TargetAstroId == 0 || (kind == SpaceEnemyKind.SpaceRelay
                ? TargetAstroId > 0 && TargetAstroId <= AuthorityScope.MaxPlanetId
                : AuthorityScope.IsSectorAstro(TargetAstroId) && TargetAstroId <= AuthorityScope.MaxPlanetId + 65536)) &&
            BaseId >= 0 && BaseId <= 65536 && BaseState >= 0 && BaseState <= 2 &&
            (kind == SpaceEnemyKind.SpaceRelay || (BaseId == 0 && BaseState == 0));
    }

    public void Write(AuthorityPayloadWriter writer, SpaceEnemyKind kind)
    {
        if (kind == SpaceEnemyKind.SpaceCore)
        {
            writer.WriteInt(RelayCount); writer.WriteInt(TinderCount); writer.WriteInt(RelaySp); writer.WriteInt(RelaySpMax);
            writer.WriteInt(TinderSp); writer.WriteInt(TinderSpMax);
        }
        else if (kind is SpaceEnemyKind.SpaceRelay or SpaceEnemyKind.SpaceTinder)
        {
            writer.WriteByte((byte)(Stage + 2)); writer.WriteByte((byte)(Direction + 1)); writer.WriteInt(TargetAstroId);
            if (kind == SpaceEnemyKind.SpaceRelay) { writer.WriteInt(BaseId); writer.WriteByte((byte)BaseState); }
        }
    }

    public static bool TryRead(ref AuthorityPayloadReader reader, SpaceEnemyKind kind, out SpaceEnemyActivity state)
    {
        state = default;
        if (kind == SpaceEnemyKind.SpaceCore)
        {
            if (!reader.TryReadInt(out var relays) || !reader.TryReadInt(out var tinders) ||
                !reader.TryReadInt(out var relaySp) || !reader.TryReadInt(out var relayMax) ||
                !reader.TryReadInt(out var tinderSp) || !reader.TryReadInt(out var tinderMax)) return false;
            state = new SpaceEnemyActivity(relays, tinders, relaySp, relayMax, tinderSp, tinderMax);
        }
        else if (kind is SpaceEnemyKind.SpaceRelay or SpaceEnemyKind.SpaceTinder)
        {
            if (!reader.TryReadByte(out var stage) || !reader.TryReadByte(out var direction) || !reader.TryReadInt(out var target)) return false;
            int baseId = 0; byte baseState = 0;
            if (kind == SpaceEnemyKind.SpaceRelay && (!reader.TryReadInt(out baseId) || !reader.TryReadByte(out baseState))) return false;
            state = new SpaceEnemyActivity(stage: stage - 2, direction: direction - 1, targetAstroId: target, baseId: baseId, baseState: baseState);
        }
        return state.Valid(kind);
    }
}
