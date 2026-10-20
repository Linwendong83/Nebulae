namespace NebulaeModel.Packets.GameStates;

[NebulaeModel.Networking.PacketId(4688151536277797104UL)]
internal class FragmentInfo
{
    public FragmentInfo() { }

    public FragmentInfo(int size)
    {
        Size = size;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int Size { get; set; }
}
