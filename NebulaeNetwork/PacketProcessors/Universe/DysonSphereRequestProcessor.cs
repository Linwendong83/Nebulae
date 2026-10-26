#region

using System;
using System.Collections.Generic;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameStates;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe;
internal class DysonSphereRequestProcessor : PacketProcessor<DysonSphereLoadRequest>
{
    protected override void ProcessPacket(DysonSphereLoadRequest packet, NebulaeConnection conn)
    {
        if (IsClient)
        {
            return;
        }
        var spheres = GameMain.data?.dysonSpheres;
        if (spheres == null || (packet.Event != DysonSphereRequestEvent.List &&
            (packet.StarIndex < 0 || packet.StarIndex >= spheres.Length))) return;
        switch (packet.Event)
        {
            case DysonSphereRequestEvent.List:
                using (var writer = new BinaryUtils.Writer())
                {
                    var list = new List<int>();
                    for (var i = 0; i < GameMain.data.dysonSpheres.Length; ++i)
                    {
                        if (GameMain.data.dysonSpheres[i] != null)
                        {
                            list.Add(i);
                        }
                    }
                    writer.BinaryWriter.Write(list.Count);
                    foreach (var starIndex in list)
                    {
                        writer.BinaryWriter.Write(starIndex);
                    }
                    conn.SendPacket(new DysonSphereData(packet.StarIndex, writer.CloseAndGetBytes(),
                        DysonSphereRespondEvent.List));
                }
                break;
            case DysonSphereRequestEvent.Load:
                var dysonSphere = GameMain.data.CreateDysonSphere(packet.StarIndex);
                using (var writer = new BinaryUtils.Writer())
                {
                    dysonSphere.Export(writer.BinaryWriter);
                    var data = writer.CloseAndGetBytes();
                    conn.SendPacket(new FragmentInfo(data.Length));
                    conn.SendPacket(new DysonSphereData(packet.StarIndex, data, DysonSphereRespondEvent.Load,
                        packet.RequestId, GameMain.gameTick));
                    Multiplayer.Session.DysonSpheres.RegisterPlayer(conn, packet.StarIndex);
                }
                break;
            case DysonSphereRequestEvent.Unload:
                Multiplayer.Session.DysonSpheres.UnRegisterPlayer(conn, packet.StarIndex);
                break;

            case DysonSphereRequestEvent.Query:
                // Ignore query if dyson sphere doesn't exist on host
                if (packet.StarIndex < 0 || packet.StarIndex >= GameMain.data.galaxy.starCount)
                {
                    return;
                }
                dysonSphere = GameMain.data.dysonSpheres[packet.StarIndex];
                if (dysonSphere == null)
                {
                    conn.SendPacket(new DysonSphereData(packet.StarIndex, Array.Empty<byte>(),
                        DysonSphereRespondEvent.Missing, packet.RequestId));
                    return;
                }
                using (var writer = new BinaryUtils.Writer())
                {
                    dysonSphere.Export(writer.BinaryWriter);
                    var data = writer.CloseAndGetBytes();
                    conn.SendPacket(new FragmentInfo(data.Length));
                    conn.SendPacket(new DysonSphereData(packet.StarIndex, data, DysonSphereRespondEvent.Load,
                        packet.RequestId, GameMain.gameTick));
                    Multiplayer.Session.DysonSpheres.RegisterPlayer(conn, packet.StarIndex);
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(packet), "Unknown DysonSphereRequestEvent: " + packet.Event);
        }
    }
}
