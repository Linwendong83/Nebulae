namespace NebulaeModel.Packets.Players;

[NebulaeModel.Networking.PacketId(11688262469771951876UL)]
internal class PlayerMechaDIYArmor
{
    public PlayerMechaDIYArmor() { }

    public PlayerMechaDIYArmor(byte[] diyArmorData, int[] diyItemId, int[] diyItemValue)
    {
        DIYAppearanceData = diyArmorData;
        DIYItemId = diyItemId;
        DIYItemValue = diyItemValue;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public byte[] DIYAppearanceData { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int[] DIYItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int[] DIYItemValue { get; set; }
}
