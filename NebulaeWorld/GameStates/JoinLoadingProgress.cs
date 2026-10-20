using System;
using System.Globalization;

namespace NebulaeWorld.GameStates;

internal enum JoinLoadingStage { ReceivingWorld, LoadingPlanet, ReceivingFactory, LoadingModels, Preparing }

/// <summary>A display snapshot. No elapsed-time estimates or transport envelope bytes.</summary>
internal readonly struct JoinLoadingProgress
{
    public JoinLoadingProgress(JoinLoadingStage stage, long received = 0, long total = 0, int completedSteps = 0)
    { Stage = stage; Received = received; Total = total; CompletedSteps = completedSteps; }

    public JoinLoadingStage Stage { get; }
    public long Received { get; }
    public long Total { get; }
    public int CompletedSteps { get; }

    public string Format(Func<string, string> translate)
    {
        var key = Stage switch
        {
            JoinLoadingStage.ReceivingWorld => Total > 0 ? "Receiving star cluster data {0:F1}/{1:F1} MB" : "Receiving star cluster data",
            JoinLoadingStage.LoadingPlanet => "Loading planet",
            JoinLoadingStage.ReceivingFactory => Total > 0 ? "Receiving factory data {0:F1}/{1:F1} MB" : "Receiving factory data",
            JoinLoadingStage.LoadingModels => "Loading factory models {0}/8",
            _ => "Preparing game"
        };
        var text = translate(key);
        if (Stage == JoinLoadingStage.LoadingModels)
            return string.Format(CultureInfo.InvariantCulture, text, CompletedSteps);
        return Total > 0
            ? string.Format(CultureInfo.InvariantCulture, text, Received / 1000000d, Total / 1000000d)
            : text;
    }
}
