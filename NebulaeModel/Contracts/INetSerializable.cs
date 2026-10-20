
#region

#endregion

namespace NebulaeModel.Contracts;

internal interface INetSerializable
{
    void Serialize(INetDataWriter writer);

    void Deserialize(INetDataReader reader);
}
