using System;
using System.Linq;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Session;
using NebulaeWorld;

namespace NebulaeModel.Packets;

internal interface IRuntimePacketProcessor
{
    void Register(NetPacketProcessor processor, bool isHost);
}

internal abstract class PacketProcessor<T> : IRuntimePacketProcessor where T : class, new()
{
    protected bool IsHost;
    protected bool IsClient => !IsHost;
    protected ConcurrentPlayerCollection Players => Multiplayer.Session.Server.Players;
    protected IServer Server => Multiplayer.Session.Server;

    public void Register(NetPacketProcessor processor, bool isHost)
    {
        IsHost = isHost;
        processor.SubscribeReusable<T, NebulaeConnection>((packet, conn) =>
        {
            // Admission belongs to each connection, including peers joining an already live host.
            if (isHost)
            {
                if (conn == null || (!conn.HandshakeCompleted && typeof(T) != typeof(LobbyRequest) &&
                                     typeof(T) != typeof(ServerStatusRequest)))
                    throw new ParseException("Packet requires a completed handshake");
                if (conn.HandshakeCompleted && typeof(T) == typeof(LobbyRequest))
                    throw new ParseException("Handshake already completed");
            }
            ProcessPacket(packet, conn);
        });
    }

    protected abstract void ProcessPacket(T packet, NebulaeConnection conn);
}

internal static class RuntimePacketRegistry
{
    internal static Type[] ProcessorTypes => typeof(RuntimePacketRegistry).Assembly.GetTypes()
        .Where(t => !t.IsAbstract && !t.ContainsGenericParameters && typeof(IRuntimePacketProcessor).IsAssignableFrom(t)).ToArray();

    internal static void Register(NetPacketProcessor processor, bool isHost)
    {
        PacketUtils.RegisterNestedTypes(processor);
        foreach (var type in ProcessorTypes)
            ((IRuntimePacketProcessor)Activator.CreateInstance(type, true)).Register(processor, isHost);
    }
}
