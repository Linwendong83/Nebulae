#region

using NebulaeModel.Networking;
using NebulaeModel.Logger;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe.Editor;
internal class DysonSphereAddShellProcessor : PacketProcessor<DysonSphereAddShellPacket>
{
    protected override void ProcessPacket(DysonSphereAddShellPacket packet, NebulaeConnection conn)
    {
        var layer = GameMain.data.dysonSpheres[packet.StarIndex]?.GetLayer(packet.LayerId);
        if (layer == null)
        {
            return;
        }
        using (Multiplayer.Session.DysonSpheres.IsIncomingRequest.On())
        {
            var shellId = layer.shellRecycleCursor > 0 ? layer.shellRecycle[layer.shellRecycleCursor - 1] : layer.shellCursor;
            if (shellId != packet.ShellId || layer.NewDysonShell(packet.ProtoId, [.. packet.NodeIds]) == 0)
            {
                Log.Warn($"Cannot add shell[{packet.ShellId}] on layer[{layer.id}], starIndex[{packet.StarIndex}]");
                Multiplayer.Session.DysonSpheres.HandleDesync(packet.StarIndex, conn);
                return;
            }
        }
        if (IsHost)
        {
            Multiplayer.Session.DysonSpheres.SendPacketToDysonSphereExcept(packet, packet.StarIndex, conn);
        }
    }
}
