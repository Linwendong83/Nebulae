#region

using NebulaeModel.DataStructures;
using NebulaeModel.Contracts;

#endregion

namespace NebulaeModel.Contracts;

internal interface IPlayerData : INetSerializable
{
    string Username { get; set; }
    ushort PlayerId { get; set; }
    int LocalPlanetId { get; set; }
    Float3 LocalPlanetPosition { get; set; }
    Double3 UPosition { get; set; }
    Float3 Rotation { get; set; }
    Float3 BodyRotation { get; set; }
    int LocalStarId { get; set; }

    IMechaData Mecha { get; set; }
    MechaAppearance Appearance { get; set; }
    MechaAppearance DIYAppearance { get; set; }
    int[] DIYItemId { get; set; }
    int[] DIYItemValue { get; set; }
    byte[] DashboardData { get; set; }

    IPlayerData CreateCopyWithoutMechaData();
}
