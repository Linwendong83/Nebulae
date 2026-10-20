#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe.Editor;

internal class DysonSphereColorChangeBatchProcessor : PacketProcessor<DysonSphereColorChangeBatchPacket>
{
    protected override void ProcessPacket(DysonSphereColorChangeBatchPacket packet, NebulaeConnection conn)
    {
        if (!packet.IsConsistent())
        {
            return;
        }
        var sphere = GameMain.data.dysonSpheres[packet.StarIndex];
        if (sphere == null)
        {
            return;
        }
        using (Multiplayer.Session.DysonSpheres.IsIncomingRequest.On())
        {
            var layer = sphere.GetLayer(packet.LayerId);
            if (layer == null)
            {
                Multiplayer.Session.DysonSpheres.HandleDesync(packet.StarIndex, conn);
                return;
            }
            for (var i = 0; i < packet.Index.Length; i++)
            {
                var color = new Color32(packet.Rgba[i * 4], packet.Rgba[i * 4 + 1], packet.Rgba[i * 4 + 2],
                    packet.Rgba[i * 4 + 3]);
                switch ((DysonSphereComponentType)packet.Type[i])
                {
                    case DysonSphereComponentType.Node:
                        var node = packet.Index[i] < layer.nodeCursor ? layer.nodePool[packet.Index[i]] : null;
                        if (node != null)
                        {
                            node.color = color;
                            sphere.UpdateColor(node);
                        }
                        break;

                    case DysonSphereComponentType.Frame:
                        var frame = packet.Index[i] < layer.frameCursor ? layer.framePool[packet.Index[i]] : null;
                        if (frame != null)
                        {
                            frame.color = color;
                            sphere.UpdateColor(frame);
                        }
                        break;

                    case DysonSphereComponentType.Shell:
                        var shell = packet.Index[i] < layer.shellCursor ? layer.shellPool[packet.Index[i]] : null;
                        if (shell != null)
                        {
                            shell.color = color;
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(packet),
                            "Unknown DysonSphereColorChangeBatchPacket type: " + packet.Type[i]);
                }
            }
        }
        if (IsHost)
        {
            Multiplayer.Session.DysonSpheres.SendPacketToDysonSphereExcept(packet, packet.StarIndex, conn);
        }
    }
}
