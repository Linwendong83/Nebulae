namespace NebulaeModel.Networking;

internal interface INetPacketProcessor
{
    /// <summary>
    /// Whether or not packet processing is enabled
    /// </summary>
    bool EnablePacketProcessing { get; set; }

    void EnqueuePacketForProcessing(byte[] rawData, object userData);
    byte[] Write<T>(T packet) where T : class, new();
    void ProcessPacketQueue(double budgetMilliseconds = 0);
}
