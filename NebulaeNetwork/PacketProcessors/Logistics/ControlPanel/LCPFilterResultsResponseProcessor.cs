#region

using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Logistics.ControlPanel;

#endregion

namespace NebulaeNetwork.PacketProcessors.Logistics.ControlPanel;
internal class LCPFilterResultsResponseProcessor : PacketProcessor<LCPFilterResultsResponse>
{
    protected override void ProcessPacket(LCPFilterResultsResponse packet, NebulaeConnection conn)
    {
        if (IsHost) return;

        // Modify from UIControlPanelWindow.DetermineFilterResults
        // Set sortedAstros and AddFilterResult by the response

        var window = UIRoot.instance.uiGame.controlPanelWindow;
        window.ResetObjectEntryPool();
        window.ClearFilterResults();
        window.sortedAstros.Clear();
        foreach (var astroId in packet.SortedAstroIds)
        {
            window.sortedAstros.Add(new ControlPanelAstroData(astroId));
        }

        var localPlanetId = GameMain.data.localPlanet?.astroId ?? 0;
        var resultCount = packet.EntryTypes.Length;
        for (var i = 0; i < resultCount; i++)
        {
            var controlPanelTarget = new ControlPanelTarget
            {
                objType = (EObjectType)packet.ObjTypes[i],
                objId = packet.ObjIds[i],
                astroId = packet.AstroIds[i],
                entryType = (EControlPanelEntryType)packet.EntryTypes[i]
            };

            var visible = ((localPlanetId == controlPanelTarget.astroId) ? (!window.astroExpands.Contains(0)) : window.astroExpands.Contains(controlPanelTarget.astroId));
            if (controlPanelTarget.entryType == EControlPanelEntryType.Planet) visible = true;
            window.AddFilterResult(ref controlPanelTarget, visible);
        }
        window.resultGeneration++;
        window.needDetermineEntryVisible = true;
        window.needDetermineFilterResults = false;
        window.ReconnSelectionOnDetermineResults();
    }
}
