using NebulaeModel.Authority;

namespace NebulaeTests.Fixtures;

internal sealed class RecordingObserver : IReplicaMirrorObserver
{
    private readonly string name;
    private readonly List<string> applied;
    public RecordingObserver(string name, List<string> applied)
    {
        this.name = name;
        this.applied = applied;
    }
    public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state) => applied.Add(name);
    public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members) { }
    public void OnMemberRemoved(ScopeKey scope, in ObjectKey key) { }
}
