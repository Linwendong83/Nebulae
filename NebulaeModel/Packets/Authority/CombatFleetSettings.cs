namespace NebulaeModel.Packets.Authority;

internal static class CombatFleetSettings
{
    private const int Relay = 1 << 18;
    private const int SpaceBuildings = 1 << 19;
    private const int GroundBuildings = 1 << 20;

    public static int Encode(int flags, CombatModuleComponent ground, CombatModuleComponent space) =>
        (flags & ~(Relay | SpaceBuildings | GroundBuildings)) | (space.attackRelay ? Relay : 0) |
        (space.attackBuilding ? SpaceBuildings : 0) | (ground.attackBuilding ? GroundBuildings : 0);

    public static void Apply(int flags, CombatModuleComponent ground, CombatModuleComponent space)
    {
        space.attackRelay = (flags & Relay) != 0;
        space.attackBuilding = (flags & SpaceBuildings) != 0;
        ground.attackBuilding = (flags & GroundBuildings) != 0;
    }
}
