#region

#endregion

namespace NebulaeModel.Packets.Players;

/// <summary>
///     Sand a client just collected, reported to the host.
/// </summary>
/// <remarks>
///     The host shares the sand a battle base yields between the connected players, so it has to know how
///     much each of them has picked up. Without this the host only learns a player's total from their
///     five-second checkpoint, which means the share it hands out is computed against a stale figure.
/// </remarks>
[NebulaeModel.Networking.PacketId(9908273645102938475UL)]
internal class PlayerSandGained
{
    public PlayerSandGained() { }

    public PlayerSandGained(long gained)
    {
        Gained = gained;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public long Gained { get; set; }
}
