namespace NebulaeModel.Packets.Authority;

internal static class CombatWeaponSettings
{
    public const int Mask = 31 << 21;

    public static int Encode(int flags, int ammoMode, int laserMode, bool bomb) =>
        (flags & ~Mask) | ((ammoMode | (laserMode << 2) | (bomb ? 16 : 0)) << 21);

    public static int Encode(int flags, Mecha mecha) => Encode(flags, mecha.ammoSelectSlotState, mecha.laserActiveState, mecha.bombActive);

    public static bool TryDecode(int flags, out int ammoMode, out int laserMode, out bool bomb)
    {
        ammoMode = (flags >> 21) & 3;
        laserMode = (flags >> 23) & 3;
        bomb = (flags & (1 << 25)) != 0;
        return ammoMode <= 2 && laserMode <= 2;
    }

    public static bool Allows(Mecha mecha, bool laser, bool manual)
    {
        var mode = laser ? mecha.laserActiveState : mecha.ammoSelectSlotState;
        return (laser ? mecha.laserActive : mecha.ammoSelectSlot > 0) && mode is >= 0 and <= 2 &&
            (manual ? mode != 1 : mode != 2);
    }
}
