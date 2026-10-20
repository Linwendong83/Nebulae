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

    public void LogDebug(object data)
    {
        logger.LogDebug(data);
    }

    public void LogError(object data)
    {
        logger.LogError(data);
    }

    public void LogInfo(object data)
    {
        logger.LogInfo(data);
    }

    public void LogWarning(object data)
    {
        logger.LogWarning(data);
    }
}
