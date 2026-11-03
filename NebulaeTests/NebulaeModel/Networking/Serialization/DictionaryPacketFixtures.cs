using global::NebulaeModel.Contracts;

namespace NebulaeTests.NebulaeModel.Networking.Serialization;

public sealed class SampleCustomClass : IEquatable<SampleCustomClass>
{
    [global::NebulaeModel.Networking.WireOrder(0)]
    public int Value { get; set; }

    public bool Equals(SampleCustomClass? other) => other is not null && Value == other.Value;
    public override bool Equals(object? obj) => obj is SampleCustomClass other && Equals(other);
    public override int GetHashCode() => Value;
}

internal sealed class SampleNetSerializableClass : INetSerializable, IEquatable<SampleNetSerializableClass>
{
    public int value;

    public void Serialize(INetDataWriter writer) => writer.Put(value);
    public void Deserialize(INetDataReader reader) => value = reader.GetInt();
    public bool Equals(SampleNetSerializableClass? other) => other is not null && value == other.value;
    public override bool Equals(object? obj) => obj is SampleNetSerializableClass other && Equals(other);
    public override int GetHashCode() => value;
}

internal struct SampleNetSerializableStruct : INetSerializable, IEquatable<SampleNetSerializableStruct>
{
    public int value;

    public void Serialize(INetDataWriter writer) => writer.Put(value);
    public void Deserialize(INetDataReader reader) => value = reader.GetInt();
    public bool Equals(SampleNetSerializableStruct other) => value == other.value;
    public override bool Equals(object? obj) => obj is SampleNetSerializableStruct other && Equals(other);
    public override int GetHashCode() => value;
}
