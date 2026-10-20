namespace NebulaeModel.Packets.Authority;

/// <summary>Shield settings inside the Settings command.</summary>
internal static class CombatShieldSettings
{
    public const int Enabled = 1 << 17;
    public static int Encode(int flags, bool enabled) => (flags & ~Enabled) | (enabled ? Enabled : 0);
    public static bool TryDecode(CombatIntent intent, out bool enabled, out float speed)
    {
        enabled = (intent.Argument & Enabled) != 0;
        speed = (float)intent.Z;
        return !double.IsNaN(intent.Z) && !double.IsInfinity(intent.Z) && intent.Z >= 0 && intent.Z <= 3.5;
    }
}
