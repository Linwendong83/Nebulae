#region

using BepInEx.Logging;
using NebulaeModel.Logger;

#endregion

namespace NebulaePatcher.Logger;

internal class BepInExLogger : ILogger
{
    private readonly ManualLogSource logger;

    public BepInExLogger(ManualLogSource logger)
    {
        this.logger = logger;
    }

    // The stamp lives at the sink, not in the message, so the error report keeps its own ISO time and
    // LastErrorMsg stays free of transport detail.
    public void LogDebug(object data)
    {
        logger.LogDebug(LogTimestamp.Stamp(data));
    }

    public void LogError(object data)
    {
        logger.LogError(LogTimestamp.Stamp(data));
    }

    public void LogInfo(object data)
    {
        logger.LogInfo(LogTimestamp.Stamp(data));
    }

    public void LogWarning(object data)
    {
        logger.LogWarning(LogTimestamp.Stamp(data));
    }
}
