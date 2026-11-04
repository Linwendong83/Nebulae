#region

using NebulaeModel.Packets.Universe;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic.UI;

/// <summary>
///     Shares marker memo changes that are made outside the marker window (the checkboxes of the
///     marker tooltip and of the marker detail nodes). Those write the todo content directly, so
///     there is no <see cref="UIMarkerDesc" /> end-edit event to hook.
/// </summary>
internal static class MarkerPacketSync
{
    internal static void SendTodo(MarkerComponent marker)
    {
        if (!Multiplayer.IsActive || Multiplayer.Session.Warning.IsIncomingMarkerPacket) return;
        if (marker == null || marker.todo == null || marker.id <= 0 || marker.astroId <= 0) return;
        Multiplayer.Session.Network.SendPacket(new MarkerSettingUpdatePacket(
            marker.astroId, marker.id, MarkerSettingEvent.SetTodoContent,
            stringValue: marker.todo.content, colorData: marker.todo.contentColorIndex));
    }

    /// <summary>Resolves a marker from the global pool entry the marker UI addresses by gid.</summary>
    internal static MarkerComponent FindByGid(int gid)
    {
        var pool = GameMain.data?.galacticDigital?.markerPool;
        if (pool == null || gid <= 0 || gid >= pool.Length) return null;
        var marker = pool[gid];
        return marker != null && marker.gid == gid ? marker : null;
    }
}
