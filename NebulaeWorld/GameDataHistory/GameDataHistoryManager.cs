#region

using System;
using NebulaeModel.DataStructures;

#endregion

namespace NebulaeWorld.GameDataHistory;

internal class GameDataHistoryManager : IDisposable
{
    public readonly ToggleSwitch IsIncomingRequest = new();
    public readonly LaboratoryResearchPrediction LaboratoryResearch = new();

    public void Dispose()
    {
        LaboratoryResearch.Clear();
        GC.SuppressFinalize(this);
    }
}
