#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeWorld;
using NebulaeWorld.Universe;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe.Editor;
internal class DysonSwarmRemoveOrbitProcessor : PacketProcessor<DysonSwarmRemoveOrbitPacket>
{
    protected override void ProcessPacket(DysonSwarmRemoveOrbitPacket packet, NebulaeConnection conn)
    {
        var spheres = GameMain.data?.dysonSpheres;
        if (spheres == null || packet.StarIndex < 0 || packet.StarIndex >= spheres.Length) return;
        if (packet.Event == SwarmRemoveOrbitEvent.MakeSailsEternal && !GameMain.data.gameDesc.isSandboxMode) return;
        var sphere = spheres[packet.StarIndex];
        if (sphere == null)
        {
            return;
        }
        if (packet.Event == SwarmRemoveOrbitEvent.RemoveSails)
        {
            if (packet.OrbitId == 0) return; // Compaction is local maintenance, never a remote command.
            if (packet.OrbitId != -1 && !sphere.swarm.OrbitExist(packet.OrbitId)) return;
        }
        else if (packet.Event != SwarmRemoveOrbitEvent.MakeSailsEternal &&
                 (packet.OrbitId < 1 || packet.OrbitId > 20 || !sphere.swarm.OrbitExist(packet.OrbitId))) return;
        using (Multiplayer.Session.DysonSpheres.IncomingDysonSwarmPacket.On())
        {
            switch (packet.Event)
            {
                case SwarmRemoveOrbitEvent.Remove:
                    sphere.swarm.RemoveOrbit(packet.OrbitId);
                    break;
                case SwarmRemoveOrbitEvent.Enable:
                case SwarmRemoveOrbitEvent.Disable:
                    sphere.swarm.SetOrbitEnable(packet.OrbitId, packet.Event == SwarmRemoveOrbitEvent.Enable);
                    break;
                case SwarmRemoveOrbitEvent.RemoveSails:
                    sphere.swarm.RemoveSailsByOrbit(packet.OrbitId);
                    break;
                case SwarmRemoveOrbitEvent.MakeSailsEternal:
                    sphere.swarm.SetAllSailsEternal();
                    var editor = UIRoot.instance?.uiGame?.dysonEditor;
                    if (editor?.selection?.viewStar?.index == packet.StarIndex)
                        editor.controlPanel?.inspector?.sphereInfo?.RefreshSailHistogram();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(packet), "Unknown SwarmRemoveOrbitEvent type: " + packet.Event);
            }
            if (packet.Event != SwarmRemoveOrbitEvent.MakeSailsEternal) DysonSphereManager.ClearSelection(packet.StarIndex);
        }
        if (IsHost)
        {
            // Eternal-life requests have no client prediction; include their author.
            if (packet.Event == SwarmRemoveOrbitEvent.MakeSailsEternal)
                Multiplayer.Session.DysonSpheres.SendPacketToDysonSphere(packet, packet.StarIndex);
            else Multiplayer.Session.DysonSpheres.SendPacketToDysonSphereExcept(packet, packet.StarIndex, conn);
        }
    }
}
