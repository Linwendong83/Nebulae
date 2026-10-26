namespace NebulaeModel.Contracts;

internal interface INetDataReader
{
    int AvailableBytes { get; }
    byte GetByte();

    bool GetBool();
    ushort GetUShort();
    long GetLong();
    int GetInt();
    float GetFloat();
    double GetDouble();

    string GetString();
    void GetBytes(byte[] destination, int count);
    byte[] GetBytesWithLength();
}
