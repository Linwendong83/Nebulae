#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe;
internal class PlanetMemoUpdateProcessor : PacketProcessor<PlanetMemoUpdatePacket>
{
    protected override void ProcessPacket(PlanetMemoUpdatePacket packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            Server.SendPacketExclude(packet, conn);
        }

        using (Multiplayer.Session.Warning.IsIncomingMarkerPacket.On())
        {
            var planet = GameMain.galaxy.PlanetById(packet.PlanetId);
            var digitalSystem = planet?.factory?.digitalSystem;

            if (digitalSystem != null)
            {
                // Factory loaded - update via digitalSystem.planetTodo
                if (digitalSystem.planetTodo == null)
                {
                    digitalSystem.planetTodo = GameMain.data.galacticDigital.AddTodoModule(
                        ETodoModuleOwnerType.Astro, packet.PlanetId);
                }

                if (digitalSystem.planetTodo != null)
                {
                    digitalSystem.planetTodo.content = packet.Content;
                    digitalSystem.planetTodo.hasReminder = packet.HasReminder;
                    if (packet.ColorData != null)
                    {
                        digitalSystem.planetTodo.contentColorIndex = packet.ColorData;
                    }
                }
            }
            else
            {
                // Factory not loaded - update global todos pool directly
                UpdateTodosPool(packet);
            }
        }
    }

    private static void UpdateTodosPool(PlanetMemoUpdatePacket packet)
    {
        var todos = GameMain.data?.galacticDigital?.todos;
        if (todos == null) return;

        // Search for existing todo
        for (int i = 1; i < todos.cursor; i++)
        {
            ref var todo = ref todos.buffer[i];
            if (todo.id == i && todo.ownerId == packet.PlanetId &&
                todo.ownerType == ETodoModuleOwnerType.Astro)
            {
                todo.content = packet.Content;
                todo.hasReminder = packet.HasReminder;
                if (packet.ColorData != null)
                {
                    todo.contentColorIndex = packet.ColorData;
                }
                return;
            }
        }

        // Create new todo if not found
        var newTodo = GameMain.data.galacticDigital.AddTodoModule(
            ETodoModuleOwnerType.Astro, packet.PlanetId);
        if (newTodo != null)
        {
            newTodo.content = packet.Content;
            newTodo.hasReminder = packet.HasReminder;
            if (packet.ColorData != null)
            {
                newTodo.contentColorIndex = packet.ColorData;
            }
        }

        // Refresh UI if it's open for this planet
        var uiTodoPanel = UIRoot.instance.uiGame.planetDetail.uiTodoPanel;
        if (uiTodoPanel.active && uiTodoPanel.astroId == packet.PlanetId)
        {
            uiTodoPanel.SetData(uiTodoPanel.astroId);
        }
    }
}
