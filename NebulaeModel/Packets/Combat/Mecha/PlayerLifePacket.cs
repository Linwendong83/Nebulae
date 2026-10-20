using NebulaeModel.DataStructures;

namespace NebulaeModel.Packets.Combat.Mecha;

[NebulaeModel.Networking.PacketId(7385105842791126233UL)]
internal class PlayerLifePacket
{
    [NebulaeModel.Networking.WireOrder(0)]
    public long CombatRevision { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public double CoreEnergyDebitAcknowledged { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public long LastCombatCommand { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public long InventoryTransferSequence { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int[] DebitItemsAcknowledged { get; set; } = System.Array.Empty<int>();
    [NebulaeModel.Networking.WireOrder(5)]
    public int[] DebitTotalsAcknowledged { get; set; } = System.Array.Empty<int>();
    [NebulaeModel.Networking.WireOrder(6)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public PlayerLifeData Life { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public byte[] PlayerSnapshot { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public bool Acknowledgement { get; set; }

    /// <summary>A terminal receipt for a snapshot, without adopting its life or inventory facts.</summary>
    public static PlayerLifePacket CreateReceipt(ushort playerId, long receivedRevision) => new()
    {
        PlayerId = playerId,
        Life = new PlayerLifeData { Revision = receivedRevision },
        Acknowledgement = true
    };
}
