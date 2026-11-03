using NebulaeModel.Contracts;
using NebulaeModel.Networking;

namespace NebulaeTests.Fixtures;

internal sealed class RecordingNetwork : INetworkProvider
{
    public List<object> Packets { get; } = new();
    public INetPacketProcessor PacketProcessor => throw new NotSupportedException();
    public byte[] WriteOnce<T>(T packet) where T : class, new() => throw new NotSupportedException();
    public void SendPacket<T>(T packet) where T : class, new() => Packets.Add(packet);
    public void SendToMatching<T>(T packet, Predicate<INebulaePlayer> condition) where T : class, new() => throw new NotSupportedException();
    public void SendPacketToLocalStar<T>(T packet) where T : class, new() => throw new NotSupportedException();
    public void SendPacketToLocalPlanet<T>(T packet) where T : class, new() => throw new NotSupportedException();
    public void SendPacketToPlanet<T>(T packet, int planetId) where T : class, new() => throw new NotSupportedException();
    public void SendPacketToStar<T>(T packet, int starId) where T : class, new() => throw new NotSupportedException();
    public void SendPacketExclude<T>(T packet, INebulaeConnection exclude) where T : class, new() => throw new NotSupportedException();
    public void SendPacketToStarExclude<T>(T packet, int starId, INebulaeConnection exclude) where T : class, new() => throw new NotSupportedException();
    public void Dispose() { }
}
