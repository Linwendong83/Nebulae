using NebulaeModel.Contracts;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;

namespace NebulaeTests.Fixtures;

internal class RecordingBroadcastNetwork : INetworkProvider
{
    public List<object> Packets { get; } = new();
    public INetPacketProcessor PacketProcessor { get; } = new NebulaeNetPacketProcessor();
    public byte[] WriteOnce<T>(T packet) where T : class, new() => PacketProcessor.Write(packet);
    public void SendPacket<T>(T packet) where T : class, new() => Packets.Add(packet);
    public void SendToMatching<T>(T packet, Predicate<INebulaePlayer> condition) where T : class, new() => SendPacket(packet);
    public void SendPacketToLocalStar<T>(T packet) where T : class, new() => SendPacket(packet);
    public void SendPacketToLocalPlanet<T>(T packet) where T : class, new() => SendPacket(packet);
    public void SendPacketToPlanet<T>(T packet, int planetId) where T : class, new() => SendPacket(packet);
    public void SendPacketToStar<T>(T packet, int starId) where T : class, new() => SendPacket(packet);
    public void SendPacketExclude<T>(T packet, INebulaeConnection exclude) where T : class, new() => SendPacket(packet);
    public void SendPacketToStarExclude<T>(T packet, int starId, INebulaeConnection exclude) where T : class, new() => SendPacket(packet);
    public void Dispose() { }
}
