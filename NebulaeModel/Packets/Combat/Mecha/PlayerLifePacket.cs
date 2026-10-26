using NebulaeModel.DataStructures;

namespace NebulaeModel.Packets.Combat.Mecha;

internal enum CheckpointReceiptStatus : byte { Accepted = 1, Duplicate, Retry, Rejected }
internal enum CheckpointRejectReason : byte { None, Busy, CombatBaseline, InvalidSnapshot, InvalidLedger, CombatLedger, InventoryLedger }

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

    [NebulaeModel.Networking.WireOrder(10)]
    public byte ReceiptStatus { get; set; }
    [NebulaeModel.Networking.WireOrder(11)]
    public long AcceptedRevision { get; set; }
    [NebulaeModel.Networking.WireOrder(12)]
    public byte RejectReason { get; set; }
    [NebulaeModel.Networking.WireOrder(13)]
    public ulong EpochHigh { get; set; }
    [NebulaeModel.Networking.WireOrder(14)]
    public ulong EpochLow { get; set; }
    [NebulaeModel.Networking.WireOrder(15)]
    public ulong ConnectionEpoch { get; set; }

    /// <summary>A terminal receipt for a snapshot, without adopting its life or inventory facts.</summary>
    public static PlayerLifePacket CreateReceipt(ushort playerId, long receivedRevision,
        CheckpointReceiptStatus status = CheckpointReceiptStatus.Accepted, long acceptedRevision = 0,
        CheckpointRejectReason reason = CheckpointRejectReason.None) => new()
        {
            PlayerId = playerId,
            Life = new PlayerLifeData { Revision = receivedRevision },
            Acknowledgement = true,
            ReceiptStatus = (byte)status,
            AcceptedRevision = acceptedRevision,
            RejectReason = (byte)reason
        };
}
