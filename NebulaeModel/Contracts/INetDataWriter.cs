namespace NebulaeModel.Contracts;

internal interface INetDataWriter
{
    byte[] Data { get; }
    int Length { get; }

    void Put(float value);
    void Put(double value);
    void Put(long value);
    void Put(int value);
    void Put(ushort value);
    void Put(byte value);
    void Put(byte[] data, int offset, int length);
    void Put(byte[] data);
    void Put(bool value);
    void Put(string value);

    void Put<T>(T obj) where T : INetSerializable;
}
