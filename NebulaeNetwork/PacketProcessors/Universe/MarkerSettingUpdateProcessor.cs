#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe;
internal class MarkerSettingUpdateProcessor : PacketProcessor<MarkerSettingUpdatePacket>
{
    protected override void ProcessPacket(MarkerSettingUpdatePacket packet, NebulaeConnection conn)
    {
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        var marker = FindMarker(factory, packet);
        if (marker == null)
        {
            return;
        }

        if (IsHost)
        {
            Server.SendPacketExclude(packet, conn);
        }

        using (Multiplayer.Session.Warning.IsIncomingMarkerPacket.On())
        {
            switch (packet.Event)
            {
                case MarkerSettingEvent.SetName:
                    marker.name = packet.StringValue;
                    break;

                case MarkerSettingEvent.SetWord:
                    marker.word = packet.StringValue;
                    break;

                case MarkerSettingEvent.SetTags:
                    marker.tags = packet.StringValue;
                    break;

                case MarkerSettingEvent.SetTodoContent:
                    // A marker only gets its todo the first time its memo is opened. Receivers apply
                    // the same vanilla rule the sender used: owner id is the marker's entity id,
                    // not its global marker id.
                    marker.todo ??= GameMain.data?.galacticDigital?.AddTodoModule(ETodoModuleOwnerType.Entity,
                        marker.entityId);
                    if (marker.todo != null)
                    {
                        marker.todo.content = packet.StringValue;
                        if (packet.ColorData != null)
                        {
                            marker.todo.contentColorIndex = packet.ColorData;
                        }
                    }
                    break;

                case MarkerSettingEvent.SetIcon:
                    marker.icon = packet.IntValue;
                    break;

                case MarkerSettingEvent.SetColor:
                    marker.color = (byte)packet.IntValue;
                    break;

                case MarkerSettingEvent.SetVisibility:
                    marker.visibility = (EMarkerVisibility)packet.IntValue;
                    // Vanilla derives the offline flag from the visibility; the power state and the
                    // 3D display radius follow it, so peers must not keep the old value.
                    marker.offline = marker.visibility == EMarkerVisibility.None;
                    break;

                case MarkerSettingEvent.SetDetailLevel:
                    marker.detailLevel = (EMarkerDetailLevel)packet.IntValue;
                    break;

                case MarkerSettingEvent.SetHeight:
                    marker.SetHeight(factory.entityPool, packet.FloatValue);
                    break;

                case MarkerSettingEvent.SetRadius:
                    marker.SetRadius(packet.FloatValue);
                    break;

                case MarkerSettingEvent.SetDigitalSignalId:
                    marker.SetDigitalSignalId(packet.IntValue);
                    break;

                case MarkerSettingEvent.SetDFAttractionFlags:
                    marker.SetDFAttractionFlags(packet.IntValue);
                    break;
            }

            if (factory != null)
            {
                try
                {
                    marker.InternalUpdate(factory.digitalSystem?.digitalSignalPool);
                }
                catch
                {
                    // The marker state is already applied; only the signal index refresh can fail
                    // here, and a stale index is repaired by the next update.
                }
            }

            // Update UI Window too if it is viewing the current marker.
            var window = UIRoot.instance?.uiGame?.markerWindow;
            if (window != null && window.active && window.markerId == packet.MarkerId && window.factory == factory)
            {
                window.markerDesc?.Refresh();
            }
        }
    }

    /// <summary>
    ///     Resolves the marker of the packet. Marker tooltips can be clicked from the star map
    ///     while the planet's factory is not resident; the global marker pool holds the same
    ///     <see cref="MarkerComponent" /> instance, so a memo update keeps working there.
    /// </summary>
    private static MarkerComponent FindMarker(PlanetFactory factory, MarkerSettingUpdatePacket packet)
    {
        var markers = factory?.digitalSystem?.markers;
        if (markers != null && packet.MarkerId > 0 && packet.MarkerId < markers.cursor)
        {
            var marker = markers.buffer[packet.MarkerId];
            if (marker != null && marker.id == packet.MarkerId)
            {
                return marker;
            }
        }

        if (packet.Event != MarkerSettingEvent.SetTodoContent) return null;
        var pool = GameMain.data?.galacticDigital?.markerPool;
        if (pool == null) return null;
        for (var i = 1; i < pool.Length; i++)
        {
            var candidate = pool[i];
            if (candidate != null && candidate.gid == i && candidate.astroId == packet.PlanetId &&
                candidate.id == packet.MarkerId)
            {
                return candidate;
            }
        }
        return null;
    }
}
