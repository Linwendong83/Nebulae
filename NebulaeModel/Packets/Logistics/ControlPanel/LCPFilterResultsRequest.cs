using System;
using NebulaeModel.DataStructures;

namespace NebulaeModel.Packets.Logistics.ControlPanel;

[NebulaeModel.Networking.PacketId(9005698533248266780UL)]
internal class LCPFilterResultsRequest
{
    public LCPFilterResultsRequest() { }

    public LCPFilterResultsRequest(in ControlPanelFilter controlPanelFilter)
    {
        TypeFilter = (int)controlPanelFilter.typeFilter;
        AstorFilter = controlPanelFilter.astroFilter;
        if (controlPanelFilter.itemsFilter != null)
        {
            ItemsFilter = new int[controlPanelFilter.itemsFilter.Length];
            Array.Copy(controlPanelFilter.itemsFilter, ItemsFilter, ItemsFilter.Length);
        }
        else
        {
            ItemsFilter = Array.Empty<int>();
        }
        StateFilter = controlPanelFilter.stateFilter;
        SearchFilter = controlPanelFilter.searchFilter;
        SortMethod = (short)controlPanelFilter.sortMethod;

        LocalPlanetAstroId = GameMain.data.localPlanet?.astroId ?? 0;
        LocalStarAstroId = GameMain.data.localStar?.astroId ?? 0;
        PlayerUposition = new Double3(GameMain.mainPlayer.uPosition.x, GameMain.mainPlayer.uPosition.y, GameMain.mainPlayer.uPosition.z);
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int TypeFilter { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int AstorFilter { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int[] ItemsFilter { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int StateFilter { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public string SearchFilter { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public short SortMethod { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public int LocalPlanetAstroId { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public int LocalStarAstroId { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public Double3 PlayerUposition { get; set; }
}
