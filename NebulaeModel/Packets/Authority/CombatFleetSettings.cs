namespace NebulaeModel.Packets.Authority;

internal static class CombatFleetSettings
{
    private const int Relay = 1 << 18;
    private const int SpaceBuildings = 1 << 19;
    private const int GroundBuildings = 1 << 20;
    private const int GroundReplenish = 1 << 26;
    private const int SpaceReplenish = 1 << 27;

    public static int Encode(int flags, CombatModuleComponent ground, CombatModuleComponent space) =>
        (flags & ~(Relay | SpaceBuildings | GroundBuildings | GroundReplenish | SpaceReplenish)) | (space.attackRelay ? Relay : 0) |
        (space.attackBuilding ? SpaceBuildings : 0) | (ground.attackBuilding ? GroundBuildings : 0) |
        (ground.autoReplenishFleet ? GroundReplenish : 0) | (space.autoReplenishFleet ? SpaceReplenish : 0);

    public static void Apply(int flags, CombatModuleComponent ground, CombatModuleComponent space)
    {
        space.attackRelay = (flags & Relay) != 0;
        space.attackBuilding = (flags & SpaceBuildings) != 0;
        ground.attackBuilding = (flags & GroundBuildings) != 0;
        ground.autoReplenishFleet = (flags & GroundReplenish) != 0;
        space.autoReplenishFleet = (flags & SpaceReplenish) != 0;
    }
}
