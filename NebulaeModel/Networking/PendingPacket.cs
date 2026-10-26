namespace NebulaeModel.Networking;

internal struct PendingPacket
{
    public byte[] Data { get; set; }
    public object UserData { get; set; }
    public double EnqueuedAt { get; }

    public PendingPacket(byte[] data, object userData)
    {
        Data = data;
        UserData = userData;
        EnqueuedAt = Logger.Diagnostics.Now;
    }
}
