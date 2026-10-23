#region

using System;
using System.Collections.Generic;
using NebulaeModel.Authority;

#endregion

namespace NebulaeWorld.Authority;

/// <summary>
/// Fans one <see cref="IHostWorldView"/> read out to several domain adapters.
/// </summary>
/// <remarks>
/// Each domain adapter owns one pool kind and answers <c>false</c> ("cannot read") for every other
/// kind. The composite returns the first adapter that can read the scope and reports unreadable only
/// when none can — which keeps "unreadable is not empty" per-domain: a planet whose factory is not
/// loaded is skipped, never published as a wave of deaths, no matter how many domains exist.
/// </remarks>
internal sealed class CompositeHostWorldView : IHostWorldView, IHostIncrementalWorldView, IHostSnapshotWorldView, IHostCaptureBatchWorldView
{
    private readonly List<IHostWorldView> views = [];
    private readonly Dictionary<ScopeKey, IHostWorldView> routes = new();

    public CompositeHostWorldView(params IHostWorldView[] views)
    {
        if (views == null || views.Length == 0) throw new ArgumentException("At least one world view is required.", nameof(views));
        this.views.AddRange(views);
    }

    public void BeginCapture(ScopeKey scope)
    {
        foreach (var view in views)
            if (view is IHostCaptureBatchWorldView batch) batch.BeginCapture(scope);
    }

    public void EndCapture()
    {
        foreach (var view in views)
            if (view is IHostCaptureBatchWorldView batch) batch.EndCapture();
    }

    public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
    {
        foreach (var view in views)
        {
            var count = members.Count;
            if (view.TryReadMembers(scope, members))
            {
                routes[scope] = view;
                return true;
            }
            if (members.Count != count)
            {
                members.RemoveRange(count, members.Count - count);
            }
        }
        return false;
    }

    public bool TryReadState(ObjectKey key, out byte[] state)
    {
        state = null;
        var scope = new ScopeKey(key.Kind, key.Scope);
        routes.TryGetValue(scope, out var cached);
        if (cached != null && cached.TryReadState(key, out state)) return true;
        foreach (var view in views)
        {
            if (ReferenceEquals(view, cached)) continue;
            if (view.TryReadState(key, out state))
            {
                routes[scope] = view;
                return true;
            }
        }
        return false;
    }

    public bool TryReadChanges(ScopeKey scope, long hostTick, List<ObjectKey> candidates, List<ObjectKey> removed)
    {
        routes.TryGetValue(scope, out var cached);
        if (cached is IHostIncrementalWorldView routed)
        {
            var count = candidates.Count; var retired = removed.Count;
            if (routed.TryReadChanges(scope, hostTick, candidates, removed)) return true;
            candidates.RemoveRange(count, candidates.Count - count); removed.RemoveRange(retired, removed.Count - retired);
        }
        foreach (var view in views)
        {
            if (ReferenceEquals(view, cached)) continue;
            if (view is not IHostIncrementalWorldView incremental) continue;
            var candidateCount = candidates.Count; var removedCount = removed.Count;
            if (incremental.TryReadChanges(scope, hostTick, candidates, removed)) { routes[scope] = view; return true; }
            candidates.RemoveRange(candidateCount, candidates.Count - candidateCount);
            removed.RemoveRange(removedCount, removed.Count - removedCount);
        }
        return false;
    }

    public bool TryReadSnapshotState(ObjectKey key, out byte[] state)
    {
        foreach (var view in views)
        {
            var read = view is IHostSnapshotWorldView snapshot
                ? snapshot.TryReadSnapshotState(key, out state)
                : view.TryReadState(key, out state);
            if (read) return true;
        }
        state = null;
        return false;
    }

    public void StateSampled(in ObjectKey key)
    {
        if (routes.TryGetValue(new ScopeKey(key.Kind, key.Scope), out var owner) && owner is IHostIncrementalWorldView routed)
        { routed.StateSampled(key); return; }
        foreach (var view in views)
            if (view is IHostIncrementalWorldView incremental) incremental.StateSampled(key);
    }
}

/// <summary>
/// Fans one replica-mirror notification out to several domain bindings.
/// </summary>
/// <remarks>
/// Each binding ignores scopes it does not own, so fan-out is safe: an Entity baseline reaches only
/// the factory combat binding and a GroundEnemy baseline only the ground binding. A throw in one
/// binding must not prevent the others from converging the same atomic baseline, so failures are
/// isolated per binding and counted rather than aborting the fan-out.
/// </remarks>
internal sealed class CompositeMirrorObserver : IReplicaMirrorObserver, IReplicaDeathObserver, IReplicaBaselineReadiness, IReplicaFrameObserver
{
    private readonly List<IReplicaMirrorObserver> observers = [];

    public CompositeMirrorObserver(params IReplicaMirrorObserver[] observers)
    {
        if (observers == null || observers.Length == 0) throw new ArgumentException("At least one observer is required.", nameof(observers));
        this.observers.AddRange(observers);
    }

    public long ObserverFailures { get; private set; }
    public bool EnforceReadiness { get; set; } = true;

    public bool IsReadyForBaseline(ScopeKey scope)
    {
        if (!EnforceReadiness) return true;
        foreach (var observer in observers)
            if (observer is IReplicaBaselineReadiness readiness && !readiness.IsReadyForBaseline(scope)) return false;
        return true;
    }

    public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state)
    {
        foreach (var observer in observers)
        {
            try
            {
                observer.OnStateApplied(scope, key, revision, state);
            }
            catch (Exception)
            {
                ObserverFailures++;
            }
        }
    }

    public void BeginReplicaFrame()
    {
        foreach (var observer in observers)
            if (observer is IReplicaFrameObserver frame)
                try { frame.BeginReplicaFrame(); } catch (Exception) { ObserverFailures++; }
    }

    public void EndReplicaFrame(Action<ScopeKey, Action> apply)
    {
        foreach (var observer in observers)
            if (observer is IReplicaFrameObserver frame)
                try { frame.EndReplicaFrame(apply); } catch (Exception) { ObserverFailures++; }
    }

    public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
    {
        foreach (var observer in observers)
        {
            try
            {
                observer.OnBaselineInstalled(scope, members);
            }
            catch (Exception error)
            {
                ObserverFailures++;
                if (ObserverFailures <= 4 || ObserverFailures % 120 == 0)
                    NebulaeModel.Logger.Log.Warn($"[authority] {observer.GetType().Name} failed to install {scope}: {error}");
                throw; // A failed game-pool installation must never produce a successful snapshot ack.
            }
        }
    }

    public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
    {
        foreach (var observer in observers)
        {
            try
            {
                observer.OnMemberRemoved(scope, key);
            }
            catch (Exception)
            {
                ObserverFailures++;
            }
        }
    }

    /// <summary>
    /// A published death, fanned out before the removal it precedes: the shell is still live while
    /// this runs, which is what makes a presentation possible at all. Only bindings that own a
    /// presentation implement it, and a failed one must not stop the removal that follows.
    /// </summary>
    public void OnMemberDied(ScopeKey scope, in ObjectKey key)
    {
        foreach (var observer in observers)
        {
            if (observer is not IReplicaDeathObserver death) continue;
            try
            {
                death.OnMemberDied(scope, key);
            }
            catch (Exception)
            {
                ObserverFailures++;
            }
        }
    }
}
