using NebulaeModel.Packets.Authority;

namespace NebulaeModel.Authority;

/// <summary>Immutable delivery routing for one object generation. Zero means every subscriber.</summary>
internal readonly struct ReplicationInterest
{
    private ReplicationInterest(int starId) => StarId = starId;

    public static ReplicationInterest InStar(int starId) => new(starId > 0 ? starId : -1);
    public int StarId { get; }
    public bool Includes(int localStarId, int viewedStarId) => Includes(localStarId) || viewedStarId > 0 && Includes(viewedStarId);
    public bool Includes(int subscriberStarId) => StarId == 0 || StarId > 0 && StarId == subscriberStarId;

    public static ReplicationInterest SpaceEnemy(SpaceEnemyKind kind, int hiveStarId) =>
        kind == SpaceEnemyKind.SpaceTinder
            ? default : new ReplicationInterest(hiveStarId > 0 ? hiveStarId : -1);
}
