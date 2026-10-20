using System.Collections.Generic;

namespace NebulaeModel.Packets.Logistics.ControlPanel;

[NebulaeModel.Networking.PacketId(15221600028427021008UL)]
internal class LCPFilterResultsResponse
{
    public LCPFilterResultsResponse() { }

    public LCPFilterResultsResponse(List<ControlPanelAstroData> sortedAstros, List<ControlPanelTarget> targets)
    {
        SortedAstroIds = new int[sortedAstros.Count];
        for (var i = 0; i < sortedAstros.Count; i++)
        {
            SortedAstroIds[i] = sortedAstros[i].astroId;
        }

        var resultTargetCount = targets.Count;
        ObjTypes = new short[resultTargetCount];
        ObjIds = new int[resultTargetCount];
        AstroIds = new int[resultTargetCount];
        EntryTypes = new short[resultTargetCount];
        for (var i = 0; i < resultTargetCount; i++)
        {
            var target = targets[i];
            ObjTypes[i] = (short)target.objType;
            ObjIds[i] = target.objId;
            AstroIds[i] = target.astroId;
            EntryTypes[i] = (short)target.entryType;
        }
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int[] SortedAstroIds { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public short[] ObjTypes { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int[] ObjIds { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int[] AstroIds { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public short[] EntryTypes { get; set; }
}
