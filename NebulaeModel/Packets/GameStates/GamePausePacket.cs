#region

#endregion

namespace NebulaeModel.Packets.GameStates;

[NebulaeModel.Networking.PacketId(2059223239710366120UL)]
internal class GamePausePacket
{
    public GamePausePacket() { }

    public GamePausePacket(bool paused, long stepTick = 0)
    {
        Paused = paused;
        StepTick = stepTick;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public bool Paused { get; set; }

    [NebulaeModel.Networking.WireOrder(1)]
    public long StepTick { get; set; }
}

// Requests are operations; only the host publishes an absolute stepping limit.
[NebulaeModel.Networking.PacketId(0x4e45425553544550UL)]
internal class GamePauseStepRequest { }
