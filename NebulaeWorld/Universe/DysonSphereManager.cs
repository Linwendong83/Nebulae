#region

using System;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Universe;
using NebulaeModel.Packets.Universe.Editor;

#endregion

namespace NebulaeWorld.Universe;

internal class DysonSphereManager : IDisposable
{
    public readonly ToggleSwitch IncomingDysonSwarmPacket = new();

    public readonly ToggleSwitch IsIncomingRequest = new();

    private readonly List<DysonSphereStatusPacket> statusPackets = []; //Server side
    private readonly ThreadSafe threadSafe = new();

    private readonly Dictionary<int, int> requests = [];
    private readonly HashSet<int> ready = [];
    private GameData requestWorld;
    private int nextRequestId;
    private int loadingNoticeStar = -1;
    private int requestedEditorStar = -1;
    private bool openEditorOnLoad;

    // Colour painting is the one editor action the player performs continuously, so it is the one that
    // needs a sender-side accumulator rather than a packet per component.
    private readonly Dictionary<(int StarIndex, int LayerId, byte Type, int Index), uint> pendingColors = [];
    private readonly Dictionary<(int StarIndex, int LayerId, byte Type, int Index), uint> lastSentColors = [];

    // Sandbox sail throwing adds sails one at a time from the throw loop, so they accumulate here and
    // leave as one packet per frame.
    private readonly Dictionary<int, List<DysonSailBatchEntry>> pendingSails = [];

    public bool IsNormal { get; set; } = true; //Client side: is the spheres data normal or desynced
    public bool InBlueprint { get; set; } //In the processing of importing blueprint
    public bool IsReady(int starIndex) => ReferenceEquals(requestWorld, GameMain.data) && ready.Contains(starIndex);
    public bool IsRequesting(int starIndex) => ReferenceEquals(requestWorld, GameMain.data) && requests.ContainsKey(starIndex);
    public bool Expects(int starIndex, int requestId) => ReferenceEquals(requestWorld, GameMain.data) &&
        requests.TryGetValue(starIndex, out var id) && id == requestId;

    public StarData GetEditorStar()
    {
        if (GameMain.localStar != null) return GameMain.localStar;
        var spheres = GameMain.data?.dysonSpheres;
        if (spheres != null)
            foreach (var sphere in spheres)
                if (sphere?.starData != null && IsReady(sphere.starData.index)) return sphere.starData;
        return GameMain.galaxy?.stars?.FirstOrDefault();
    }

    public void WaitForEditor(int starIndex, bool openWhenLoaded)
    {
        requestedEditorStar = starIndex;
        openEditorOnLoad = openWhenLoaded;
        RequestDysonSphere(starIndex);
    }

    public bool CompleteEditorRequest(int starIndex, out bool open)
    {
        open = false;
        if (requestedEditorStar != starIndex || !IsReady(starIndex)) return false;
        open = openEditorOnLoad;
        requestedEditorStar = -1;
        openEditorOnLoad = false;
        return true;
    }

    public void CancelEditorRequest(int starIndex)
    {
        if (requestedEditorStar != starIndex) return;
        requestedEditorStar = -1;
        openEditorOnLoad = false;
    }

    public bool ClearLoadingNotice(int starIndex)
    {
        if (loadingNoticeStar != starIndex) return false;
        loadingNoticeStar = -1;
        return true;
    }

    private void ShowLoadingNotice(int starIndex)
    {
        if (loadingNoticeStar == starIndex) return;
        if (loadingNoticeStar >= 0) InGamePopup.FadeOut();
        loadingNoticeStar = starIndex;
        InGamePopup.ShowInfo("Loading".Translate(),
            string.Format("Loading Dyson sphere {0}, please wait".Translate(), GameMain.galaxy.stars[starIndex].displayName),
            "Cancel".Translate(), () =>
            {
                CancelEditorRequest(starIndex);
                ClearLoadingNotice(starIndex);
            });
    }

    /// <summary>
    ///     Record one component's colour for the next batch. A later colour for the same component
    ///     replaces the earlier one, which is what makes holding the brush over a single component cost
    ///     one packet instead of one per frame.
    /// </summary>
    public void RegisterColorChange(int starIndex, int layerId, byte type, int index, uint rgba)
    {
        pendingColors[(starIndex, layerId, type, index)] = rgba;
    }

    /// <summary>
    ///     Send every recorded colour change whose colour differs from the last one transmitted.
    /// </summary>
    /// <remarks>
    ///     The comparison against what was last sent is what stops a held brush from repainting the same
    ///     component at the tick rate. The memory of sent colours is dropped as soon as a flush finds
    ///     nothing to do, so it spans one painting stroke rather than the whole session.
    /// </remarks>
    private void FlushColors()
    {
        if (pendingColors.Count == 0)
        {
            lastSentColors.Clear();
            return;
        }
        foreach (var group in pendingColors.GroupBy(p => (p.Key.StarIndex, p.Key.LayerId)))
        {
            var entries = group.Where(pair => !lastSentColors.TryGetValue(pair.Key, out var previous) || previous != pair.Value).ToArray();
            for (var offset = 0; offset < entries.Length; offset += DysonSphereColorChangeBatchPacket.MaxEntries)
            {
                var count = Math.Min(DysonSphereColorChangeBatchPacket.MaxEntries, entries.Length - offset);
                var type = new byte[count]; var index = new int[count]; var rgba = new byte[count * 4];
                for (var i = 0; i < count; i++)
                {
                    var pair = entries[offset + i];
                    type[i] = pair.Key.Type; index[i] = pair.Key.Index;
                    for (var b = 0; b < 4; b++) rgba[i * 4 + b] = (byte)(pair.Value >> (b * 8));
                    lastSentColors[pair.Key] = pair.Value;
                }
                var packet = new DysonSphereColorChangeBatchPacket(group.Key.StarIndex, group.Key.LayerId, type, index, rgba);
                if (Multiplayer.Session.IsServer) SendPacketToDysonSphere(packet, group.Key.StarIndex);
                else Multiplayer.Session.Network.SendPacket(packet);
            }
        }
        pendingColors.Clear();
    }

    public void InvalidateColor(int star, int layer, byte type, int index) => lastSentColors.Remove((star, layer, type, index));

    /// <summary>
    ///     Record a thrown sail in its star's batch. Each wire batch is bounded independently.
    /// </summary>
    public void RegisterSolarSail(int starIndex, ref DysonSail sail, int orbitId, long expiryTime)
    {
        if (!pendingSails.TryGetValue(starIndex, out var entries)) pendingSails[starIndex] = entries = [];
        entries.Add(new DysonSailBatchEntry(sail, orbitId, expiryTime));
    }

    /// <summary>
    ///     Send bounded batches for every star touched since the last flush.
    /// </summary>
    public void GameTick()
    {
        FlushColors();
        if (pendingSails.Count == 0)
        {
            return;
        }

        foreach (var group in pendingSails)
            for (var offset = 0; offset < group.Value.Count; offset += DysonSailBatchPacket.MaxSails)
            {
                var count = Math.Min(DysonSailBatchPacket.MaxSails, group.Value.Count - offset);
                var orbitId = new int[count];
                var expiryTime = new long[count];
                var st = new float[count];
                var px = new float[count];
                var py = new float[count];
                var pz = new float[count];
                var vx = new float[count];
                var vy = new float[count];
                var vz = new float[count];
                var gs = new float[count];
                for (var i = 0; i < count; i++)
                {
                    var entry = group.Value[offset + i];
                    orbitId[i] = entry.OrbitId;
                    expiryTime[i] = entry.ExpiryTime;
                    st[i] = entry.Sail.st;
                    px[i] = entry.Sail.px;
                    py[i] = entry.Sail.py;
                    pz[i] = entry.Sail.pz;
                    vx[i] = entry.Sail.vx;
                    vy[i] = entry.Sail.vy;
                    vz[i] = entry.Sail.vz;
                    gs[i] = entry.Sail.gs;
                }
                var packet = new DysonSailBatchPacket(group.Key, orbitId, expiryTime, st, px, py, pz, vx, vy, vz, gs);
                if (Multiplayer.Session.IsServer)
                {
                    SendPacketToDysonSphere(packet, group.Key);
                }
                else
                {
                    Multiplayer.Session.Network.SendPacket(packet);
                }
            }
        pendingSails.Clear();
    }

    private readonly struct DysonSailBatchEntry
    {
        public DysonSailBatchEntry(DysonSail sail, int orbitId, long expiryTime)
        {
            Sail = sail;
            OrbitId = orbitId;
            ExpiryTime = expiryTime;
        }

        public DysonSail Sail { get; }
        public int OrbitId { get; }
        public long ExpiryTime { get; }
    }

    public void Dispose()
    {
        loadingNoticeStar = requestedEditorStar = -1;
        openEditorOnLoad = false;
        requests.Clear(); ready.Clear(); pendingSails.Clear(); pendingColors.Clear(); lastSentColors.Clear();
        requestWorld = null;
        statusPackets.Clear();
        using (GetSubscribers(out var subscribers)) subscribers.Clear();
        GC.SuppressFinalize(this);
    }

    private Locker GetSubscribers(out Dictionary<int, List<INebulaeConnection>> subscribers)
    {
        return threadSafe.Subscribers.GetLocked(out subscribers);
    }

    /// <summary>
    ///     Send packet to all Clients that subscribe to target Dyson Sphere
    /// </summary>
    public void SendPacketToDysonSphere<T>(T packet, int starIndex) where T : class, new()
    {
        using (GetSubscribers(out var subscribers))
        {
            if (!subscribers.TryGetValue(starIndex, out var value))
            {
                return;
            }
            SendToSubscribers(value, packet, null);
        }
    }

    public void SendPacketToDysonSphereExcept<T>(T packet, int starIndex, INebulaeConnection exception) where T : class, new()
    {
        using (GetSubscribers(out var subscribers))
        {
            if (!subscribers.TryGetValue(starIndex, out var value))
            {
                return;
            }
            SendToSubscribers(value, packet, exception);
        }
    }

    /// <summary>
    ///     Serialize once and hand the same bytes to every subscriber. The serializer is not thread-safe,
    ///     so the write goes through the host's locked helper rather than a per-connection SendPacket.
    /// </summary>
    private static void SendToSubscribers<T>(List<INebulaeConnection> connections, T packet, INebulaeConnection exception)
        where T : class, new()
    {
        byte[] rawData = null;
        foreach (var conn in connections)
        {
            if (conn.Equals(exception))
            {
                continue;
            }
            rawData ??= Multiplayer.Session.Server.WriteOnce(packet);
            conn.SendRawPacket(rawData);
        }
    }

    public void RegisterPlayer(INebulaeConnection conn, int starIndex)
    {
        using var locker = GetSubscribers(out var subscribers);
        if (!subscribers.TryGetValue(starIndex, out var value))
        {
            value = [];
            subscribers.Add(starIndex, value);
            statusPackets.Add(new DysonSphereStatusPacket(GameMain.data.dysonSpheres[starIndex]));
            Multiplayer.Session.Launch.Register(starIndex);
        }
        if (value.Contains(conn))
        {
            return;
        }
        value.Add(conn);
        conn.SendPacket(new DysonSphereStatusPacket(GameMain.data.dysonSpheres[starIndex]));
    }

    public void UnRegisterPlayer(INebulaeConnection conn, int starIndex)
    {
        using var locker = GetSubscribers(out var subscribers);
        if (!subscribers.TryGetValue(starIndex, out var value))
        {
            return;
        }

        value.Remove(conn);
        if (value.Count != 0)
        {
            return;
        }
        subscribers.Remove(starIndex);
        statusPackets.Remove(statusPackets.Find(x => x.StarIndex == starIndex));
        Multiplayer.Session.Launch.Unregister(starIndex);
    }

    public void UnRegisterPlayer(INebulaeConnection conn)
    {
        using var locker = GetSubscribers(out var subscribers);
        var removals = new List<int>();
        foreach (var item in subscribers)
        {
            item.Value.Remove(conn);
            if (item.Value.Count == 0)
            {
                removals.Add(item.Key);
            }
        }
        foreach (var starIndex in removals)
        {
            subscribers.Remove(starIndex);
            statusPackets.Remove(statusPackets.Find(x => x.StarIndex == starIndex));
            Multiplayer.Session.Launch.Unregister(starIndex);
        }
    }

    public void UpdateSphereStatusIfNeeded()
    {
        for (var i = 0; i < statusPackets.Count; i++)
        {
            var previous = statusPackets[i];
            var sphere = GameMain.data.dysonSpheres[previous.StarIndex];
            if (sphere == null) continue;
            var packet = new DysonSphereStatusPacket(sphere);
            if (previous.SameValues(packet)) continue;
            statusPackets[i] = packet;
            SendPacketToDysonSphere(packet, packet.StarIndex);
        }
    }

    public void RequestDysonSphere(int starIndex, bool showInfo = true, bool query = false, bool force = false)
    {
        var world = GameMain.data;
        // The menu demo can keep ticking while a replacement client connects. Its factories
        // must not reserve request IDs or fetch data for a world that is about to be destroyed.
        if (world?.galaxy == null || GameMain.instance?.isMenuDemo == true) return;
        if (!ReferenceEquals(requestWorld, world))
        {
            requests.Clear(); ready.Clear();
            requestWorld = world;
        }
        var starData = world.galaxy.stars[starIndex];
        if (!force && IsRequesting(starIndex))
        {
            if (showInfo) ShowLoadingNotice(starIndex);
            return;
        }
        var id = checked(++nextRequestId);
        requests[starIndex] = id;
        Multiplayer.Session.Network.SendPacket(new DysonSphereLoadRequest(starData.index,
            query ? DysonSphereRequestEvent.Query : DysonSphereRequestEvent.Load, id));
        ClearSelection(starIndex);
        if (showInfo) ShowLoadingNotice(starIndex);
    }

    public bool Commit(int starIndex, int requestId, DysonSphere sphere, long snapshotTick)
    {
        if (!Expects(starIndex, requestId)) return false;
        var old = GameMain.data.dysonSpheres[starIndex];
        GameMain.data.dysonSpheres[starIndex] = sphere;
        requests.Remove(starIndex);
        ready.Add(starIndex);
        Multiplayer.Session.Launch.SetBaseline(starIndex, snapshotTick);
        IsNormal = true;
        ReleaseSphere(old);
        return true;
    }

    public bool FailRequest(int starIndex, int requestId)
    {
        // Even a response arriving between worlds must release its own request. A newer
        // request has a different ID and must remain intact.
        if (!requests.TryGetValue(starIndex, out var id) || id != requestId) return false;
        requests.Remove(starIndex);
        CancelEditorRequest(starIndex);
        return true;
    }

    public void UnloadRemoteDysonSpheres()
    {
        if (GameMain.instance?.isMenuDemo == true) return;
        //The editor will throw errors if there are no dyson spheres available
        var currentId = GameMain.localStar?.index ?? UIRoot.instance?.uiGame?.dysonEditor?.selection?.viewStar?.index ?? -1;
        for (var i = 0; i < GameMain.data.dysonSpheres.Length; i++)
        {
            if (i == currentId || (GameMain.data.dysonSpheres[i] == null && !IsRequesting(i)))
            {
                continue;
            }
            Multiplayer.Session.Network.SendPacket(new DysonSphereLoadRequest(i, DysonSphereRequestEvent.Unload));
            var old = GameMain.data.dysonSpheres[i];
            GameMain.data.dysonSpheres[i] = null;
            requests.Remove(i); ready.Remove(i);
            CancelEditorRequest(i);
            if (ClearLoadingNotice(i)) InGamePopup.FadeOut();
            Multiplayer.Session.Launch.Unregister(i);
            ReleaseSphere(old);
        }
        IsNormal = true;
    }

    internal static void ReleaseSphere(DysonSphere sphere)
    {
        if (sphere == null) return;
        try { sphere.Free(); }
        catch (Exception error) { Log.Warn(error); }
    }

    public void HandleDesync(int starIndex, INebulaeConnection conn)
    {
        if (Multiplayer.Session.LocalPlayer.IsHost)
        {
            //Notify packet author that its building request did not success
            conn.SendPacket(new DysonSphereData(starIndex, Array.Empty<byte>(), DysonSphereRespondEvent.Desync));
        }
        else
        {
            //Notify client that desync happened
            if (!IsNormal)
            {
                return;
            }
            IsNormal = false;
            InGamePopup.ShowWarning("Desync".Translate(),
                string.Format("Dyson sphere id[{0}] {1} is desynced.".Translate(), starIndex,
                    GameMain.galaxy.stars[starIndex].displayName),
                "Reload".Translate(), () => RequestDysonSphere(starIndex, force: true));
        }
    }

    public static int QueryOrbitId(DysonSwarm swarm)
    {
        //Return the next available orbit Id
        var orbitId = swarm.orbitCursor <= 20 ? swarm.orbitCursor : -1;
        for (var i = 1; i < swarm.orbitCursor; i++)
        {
            if (swarm.orbits[i].id != 0)
            {
                continue;
            }
            orbitId = i;
            break;
        }
        return orbitId;
    }

    public static float[] SailLifeDistribution(DysonSwarm swarm, int division, long tick, float life,
        ICollection<int> orbits, out float maxCount)
    {
        maxCount = 0;
        if (division <= 1 || swarm?.expiryOrder == null) return null;
        var result = new float[division];
        var gap = Math.Max(1.0, life * 60.0 / division);
        for (var cursor = swarm.expiryCursor; cursor != swarm.expiryEnding; cursor = (cursor + 1) % swarm.sailCapacity)
        {
            var entry = swarm.expiryOrder[cursor];
            if (entry.index < 0 || entry.index >= swarm.sailCursor || entry.time == 0 ||
                swarm.sailInfos[entry.index].orbit == 0 ||
                (orbits != null && !orbits.Contains((int)swarm.sailInfos[entry.index].orbit))) continue;
            var remaining = (entry.time - (double)tick) / gap;
            var bucket = remaining <= 0 ? 0 : remaining >= division - 1 ? division - 1 : (int)remaining;
            result[bucket]++;
            maxCount = Math.Max(maxCount, result[bucket]);
        }
        return result;
    }

    public static void ClearSelection(int starIndex, int layerId = -1)
    {
        var selection = UIRoot.instance?.uiGame?.dysonEditor?.selection;
        if (selection?.viewStar == null || selection.viewStar.index != starIndex)
        {
            return;
        }
        if (layerId == -1)
        {
            selection.ClearAllSelection();
        }
        else if (selection.IsLayerSelected(layerId))
        {
            selection.ClearComponentSelection();
        }
    }

    private sealed class ThreadSafe
    {
        internal readonly Dictionary<int, List<INebulaeConnection>> Subscribers = new();
    }
}
