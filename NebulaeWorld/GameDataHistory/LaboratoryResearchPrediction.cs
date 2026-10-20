using NebulaeModel.Authority;

namespace NebulaeWorld.GameDataHistory;

/// <summary>Shared native research budget for the client's loaded factories, separate from global facts.</summary>
internal sealed class LaboratoryResearchPrediction
{
    private AuthorityEpoch epoch;
    private int tech;
    private TechState anchor, predicted;

    public TechState Read(AuthorityEpoch world, int id, in TechState authoritative)
    {
        if (epoch != world || tech != id || anchor.curLevel != authoritative.curLevel ||
            anchor.hashUploaded != authoritative.hashUploaded || anchor.hashNeeded != authoritative.hashNeeded ||
            anchor.unlocked != authoritative.unlocked || anchor.maxLevel != authoritative.maxLevel)
        {
            epoch = world; tech = id; anchor = authoritative; predicted = authoritative;
            // FactorySystem must await the host's queue transition even if an unlock arrived first.
            predicted.unlocked = false;
        }
        return predicted;
    }
    public void Write(int id, in TechState state)
    {
        if (id > 0 && id == tech) predicted = state;
    }
    public void Clear() { epoch = default; tech = 0; anchor = predicted = default; }
}
