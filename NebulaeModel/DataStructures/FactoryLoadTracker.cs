using System.Collections.Generic;

namespace NebulaeModel.DataStructures;

internal enum FactoryLoadPhase { Waiting, Deferred, Buffered, Queued, Importing, Modeling }

/// <summary>Main-thread load ownership. Cancelled requests are drained before requesting that planet again.</summary>
internal sealed class FactoryLoadTracker
{
    private readonly Dictionary<int, FactoryLoadPhase> loads = new();
    private readonly HashSet<int> abandonedResponses = new();
    private readonly HashSet<int> paused = new();
    public bool Paused => paused.Count != 0;
    public bool Contains(int planet) => loads.ContainsKey(planet);
    public bool Begin(int planet, bool needsSnapshot)
    {
        if (loads.ContainsKey(planet)) return false;
        loads[planet] = needsSnapshot ? (abandonedResponses.Contains(planet) ? FactoryLoadPhase.Deferred : FactoryLoadPhase.Waiting) : FactoryLoadPhase.Queued;
        return !needsSnapshot || loads[planet] == FactoryLoadPhase.Waiting;
    }
    public bool Receive(int planet, out bool requestAgain)
    {
        requestAgain = false;
        if (abandonedResponses.Remove(planet))
        {
            if (loads.TryGetValue(planet, out var phase) && phase == FactoryLoadPhase.Deferred)
            { loads[planet] = FactoryLoadPhase.Waiting; requestAgain = true; }
            return false;
        }
        if (!loads.TryGetValue(planet, out var current) || current != FactoryLoadPhase.Waiting) return false;
        loads[planet] = FactoryLoadPhase.Buffered;
        return true;
    }
    public bool TryQueue(int planet)
    {
        if (!loads.TryGetValue(planet, out var phase) || phase != FactoryLoadPhase.Buffered) return false;
        loads[planet] = FactoryLoadPhase.Queued;
        return true;
    }
    public void Pause(int planet) => paused.Add(planet);
    public void SetPhase(int planet, FactoryLoadPhase phase) { if (loads.ContainsKey(planet)) loads[planet] = phase; }
    public void Complete(int planet) { loads.Remove(planet); paused.Remove(planet); }
    public void Cancel(int planet)
    {
        if (loads.TryGetValue(planet, out var phase) && phase == FactoryLoadPhase.Waiting) abandonedResponses.Add(planet);
        Complete(planet);
    }
    public void Clear() { loads.Clear(); abandonedResponses.Clear(); paused.Clear(); }
}
