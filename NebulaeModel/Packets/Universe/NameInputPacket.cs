using NebulaeModel.Networking;
#region

using System.Collections.Generic;
#endregion

namespace NebulaeModel.Packets.Universe;

// Packet for name input for Planets and Stars
[NebulaeModel.Networking.PacketId(4851945548044146230UL)]
internal class NameInputPacket
{
    public NameInputPacket() { }

    public NameInputPacket(string name, int starId, int planetId)
    {
        Names = new[] { name };
        StarIds = new[] { starId };
        PlanetIds = new[] { planetId };
    }

    public NameInputPacket(in GalaxyData galaxy)
    {
        var names = new List<string>();
        var starIds = new List<int>();
        var planetIds = new List<int>();

        foreach (var s in galaxy.stars)
        {
            if (!string.IsNullOrEmpty(s.overrideName))
            {
                names.Add(s.overrideName);
                starIds.Add(s.id);
                planetIds.Add(SessionSentinels.PLANET_NONE);
            }
            foreach (var p in s.planets)
            {
                if (string.IsNullOrEmpty(p.overrideName))
                {
                    continue;
                }
                names.Add(p.overrideName);
                starIds.Add(SessionSentinels.STAR_NONE);
                planetIds.Add(p.id);
            }
        }

        Names = names.ToArray();
        StarIds = starIds.ToArray();
        PlanetIds = planetIds.ToArray();
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public string[] Names { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int[] PlanetIds { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int[] StarIds { get; set; }
}
