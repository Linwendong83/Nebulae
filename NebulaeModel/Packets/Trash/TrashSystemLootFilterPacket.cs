namespace NebulaeModel.Packets.Trash;

[NebulaeModel.Networking.PacketId(2929721336225327657UL)]
internal class TrashSystemLootFilterPacket
{
    public TrashSystemLootFilterPacket() { }

    public TrashSystemLootFilterPacket(byte[] enemyDropBansData)
    {
        EnemyDropBansData = enemyDropBansData;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public byte[] EnemyDropBansData { get; set; }
}
