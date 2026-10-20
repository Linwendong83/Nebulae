using System;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;

namespace NebulaeWorld.Planet;

/// <summary>Main-thread requests share the game's existing background terrain/model loader.</summary>
internal sealed class HostPlanetPreparation
{
    private sealed class Request
    {
        public PlanetData Planet;
        public bool WasWanted;
        public long Started;
        public bool ReadyRecorded;
        public readonly HashSet<INebulaeConnection> Prefetchers = new();
        public readonly Dictionary<INebulaeConnection, (Action Ready, Action<Exception> Failed)> Waiters = new();
    }
    private readonly Dictionary<int, Request> requests = new();
    private readonly LoadingTimings timings;
    private readonly Action<PlanetData> load;
    public HostPlanetPreparation(LoadingTimings timings, Action<PlanetData> load = null)
    { this.timings = timings; this.load = load ?? (planet => planet.Load()); }

    public void Prepare(PlanetData planet, INebulaeConnection connection)
    {
        if (planet == null) return;
        if (requests.TryGetValue(planet.id, out var existing))
        {
            existing.Prefetchers.Add(connection);
            return;
        }
        if (IsReady(planet)) return;
        var request = GetRequest(planet, out var created);
        request.Prefetchers.Add(connection);
        Start(request, created);
    }

    public void RequestFactory(PlanetData planet, INebulaeConnection connection, Action ready, Action<Exception> failed)
    {
        // Hosts retain terrain meshes after hiding remote planets. Re-entering one must not
        // regenerate that retained data (or replace data while an existing factory uses it).
        if (!requests.ContainsKey(planet.id) && IsReady(planet)) { ready(); return; }
        var request = GetRequest(planet, out var created);
        request.Prefetchers.Remove(connection);
        request.Waiters[connection] = (ready, failed);
        if (IsReady(planet)) Finish(request, null);
        else Start(request, created);
    }

    private Request GetRequest(PlanetData planet, out bool created)
    {
        created = !requests.TryGetValue(planet.id, out var request);
        if (created)
        {
            request = new Request { Planet = planet, WasWanted = planet.wanted, Started = LoadingTimings.Now };
            requests.Add(planet.id, request);
        }
        return request;
    }

    private void Start(Request request, bool created)
    {
        try { if (created) load(request.Planet); }
        catch (Exception error) { Finish(request, error); }
    }

    public void Forget(INebulaeConnection connection)
    {
        foreach (var request in requests.Values.ToArray())
        {
            request.Waiters.Remove(connection);
            request.Prefetchers.Remove(connection);
            if (request.Waiters.Count == 0 && request.Prefetchers.Count == 0) Release(request);
        }
    }

    public void Update()
    {
        if (requests.Count == 0) return;
        foreach (var request in requests.Values.ToArray())
        {
            if (LoadingTimings.Elapsed(request.Started) > 120000)
                Finish(request, new TimeoutException($"Terrain preparation timed out on planet {request.Planet.id}"));
            else if (IsReady(request.Planet)) Finish(request, null);
        }
    }

    internal static bool IsReady(PlanetData planet) => planet.data != null &&
        (planet.loaded || (!planet.loading && planet.factory != null && planet.meshes != null && planet.meshes.Length > 0 &&
                          planet.meshColliders != null && planet.meshes.All(mesh => !ReferenceEquals(mesh, null))));

    private void Finish(Request request, Exception error)
    {
        if (!request.ReadyRecorded)
        {
            timings.Record("host-terrain-ready", LoadingTimings.Elapsed(request.Started));
            request.ReadyRecorded = true;
        }
        var waiters = request.Waiters.ToArray();
        request.Waiters.Clear();
        foreach (var waiter in waiters)
        {
            if (!waiter.Key.IsAlive) continue;
            try
            {
                if (error != null) waiter.Value.Failed(error);
                else waiter.Value.Ready();
            }
            catch (Exception failure) { waiter.Value.Failed(failure); }
        }
        if (error != null || request.Prefetchers.Count == 0) Release(request);
    }

    private void Release(Request request)
    {
        requests.Remove(request.Planet.id);
        if (request.WasWanted || GameMain.localStar == request.Planet.star) return;
        // The background loader observes wanted before publishing its model. Cancelling
        // a prefetch must also release a request that has not finished generation yet.
        request.Planet.wanted = false;
        if (!request.Planet.loading) request.Planet.Unload();
    }

    public void Clear()
    {
        foreach (var request in requests.Values.ToArray()) Release(request);
    }
}
