#region

using System;
using System.Collections.Generic;
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

    // Client side: the last hive debuff coefficient the host sent for each subscribed star. The
    // coefficient is a property of the host's gamma counts, which the replica does not carry, so it
    // is the one dyson-energy fact that has to travel. The periodic recompute of the sphere's own
    // energy reads it back, which is what keeps a freshly added sail from being debuffed with a
    // stale coefficient until the next packet.
    private readonly Dictionary<int, double> debuffCoefs = [];

    // Colour painting is the one editor action the player performs continuously, so it is the one that
    // needs a sender-side accumulator rather than a packet per component.
    private readonly Dictionary<(int StarIndex, int LayerId, byte Type, int Index), uint> pendingColors = [];
    private readonly Dictionary<(int StarIndex, int LayerId, byte Type, int Index), uint> lastSentColors = [];

    // Sandbox sail throwing adds sails one at a time from the throw loop, so they accumulate here and
    // leave as one packet per frame.
    private readonly List<DysonSailBatchEntry> pendingSails = [];
    private int pendingSailStar = -1;

    public bool IsNormal { get; set; } = true; //Client side: is the spheres data normal or desynced
    public bool InBlueprint { get; set; } //In the processing of importing blueprint
    public int RequestingIndex { get; set; } = -1; //StarIndex of the dyson sphere requesting

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
            return;
        }

        var type = new List<byte>(pendingColors.Count);
        var index = new List<int>(pendingColors.Count);
        var rgba = new List<byte>(pendingColors.Count * 4);
        var starIndex = 0;
        var layerId = 0;
        foreach (var pair in pendingColors)
        {
            if (lastSentColors.TryGetValue(pair.Key, out var previous) && previous == pair.Value)
            {
                continue;
            }
            starIndex = pair.Key.StarIndex;
            layerId = pair.Key.LayerId;
            type.Add(pair.Key.Type);
            index.Add(pair.Key.Index);
            rgba.Add((byte)(pair.Value & 0xFF));
            rgba.Add((byte)((pair.Value >> 8) & 0xFF));
            rgba.Add((byte)((pair.Value >> 16) & 0xFF));
            rgba.Add((byte)((pair.Value >> 24) & 0xFF));
            lastSentColors[pair.Key] = pair.Value;
        }
        pendingColors.Clear();

        if (type.Count == 0)
        {
            // Nothing changed since the last flush, so the stroke is over and the comparison memory can
            // go with it.
            lastSentColors.Clear();
            return;
        }

        var packet = new DysonSphereColorChangeBatchPacket(starIndex, layerId, type.ToArray(), index.ToArray(),
            rgba.ToArray());
        if (Multiplayer.Session.IsServer)
        {
            SendPacketToDysonSphere(packet, starIndex);
        }
        else
        {
            Multiplayer.Session.Network.SendPacket(packet);
        }
    }

    /// <summary>
    ///     Record one thrown sail for the next batch. All sails of a frame belong to one star, so the
    ///     star is taken from the first sail of the batch.
    /// </summary>
    public void RegisterSolarSail(int starIndex, ref DysonSail sail, int orbitId, long expiryTime)
    {
        if (pendingSails.Count == 0)
        {
            pendingSailStar = starIndex;
        }
        pendingSails.Add(new DysonSailBatchEntry(sail, orbitId, expiryTime));
    }

    /// <summary>
    ///     Send every sail thrown since the last flush as one packet.
    /// </summary>
    public void GameTick()
    {
        FlushColors();
        if (pendingSails.Count == 0)
        {
            return;
        }

        var count = pendingSails.Count;
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
            var entry = pendingSails[i];
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
        pendingSails.Clear();

        var packet = new DysonSailBatchPacket(pendingSailStar, orbitId, expiryTime, st, px, py, pz, vx, vy, vz, gs);
        if (Multiplayer.Session.IsServer)
        {
            SendPacketToDysonSphere(packet, pendingSailStar);
        }
        else
        {
            Multiplayer.Session.Network.SendPacket(packet);
        }
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
        conn.SendPacket(statusPackets.Find(x => x.StarIndex == starIndex));
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

    /// <summary>
    ///     Last hive debuff coefficient the host reported for a star, or 1 (no debuff) before the
    ///     first packet. Client side only; the host reads the live property.
    /// </summary>
    public double DebuffCoef(int starIndex) => debuffCoefs.TryGetValue(starIndex, out var coef) ? coef : 1.0;

    /// <summary>
    ///     Store a reported coefficient and fold it into the sphere's aggregate generation at once,
    ///     so a gamma-driven change shows up without waiting for the next local recompute.
    /// </summary>
    public void SetDebuffCoef(int starIndex, double coef)
    {
        debuffCoefs[starIndex] = coef;
        var dysonSphere = GameMain.data.dysonSpheres[starIndex];
        if (dysonSphere != null)
        {
            dysonSphere.energyGenCurrentTick = (long)(dysonSphere.energyGenOriginalCurrentTick * coef);
        }
    }

    public void UpdateSphereStatusIfNeeded()
    {
        foreach (var packet in statusPackets)
        {
            var dysonSphere = GameMain.data.dysonSpheres[packet.StarIndex];
            var debuffCoef = dysonSphere.energyDFHivesDebuffCoef;
            //Update dyson sphere when the status changes.
            //Generation, radius and the original (pre-debuff) total are not compared: they are
            //derived from the sphere structure, which every peer replicates and computes for itself.
            if (packet.EnergyReqCurrentTick == dysonSphere.energyReqCurrentTick &&
                packet.DebuffCoef == debuffCoef)
            {
                continue;
            }
            packet.EnergyReqCurrentTick = dysonSphere.energyReqCurrentTick;
            packet.DebuffCoef = debuffCoef;
            SendPacketToDysonSphere(packet, packet.StarIndex);
        }
    }

    public void RequestDysonSphere(int starIndex, bool showInfo = true)
    {
        var starData = GameMain.galaxy.stars[starIndex];
        RequestingIndex = starIndex;
        Multiplayer.Session.Network.SendPacket(new DysonSphereLoadRequest(starData.index, DysonSphereRequestEvent.Load));
        ClearSelection(starIndex);
        if (showInfo)
        {
            InGamePopup.ShowInfo("Loading".Translate(),
                string.Format("Loading Dyson sphere {0}, please wait".Translate(), starData.displayName), null);
        }
    }

    public void UnloadRemoteDysonSpheres()
    {
        //The editor will throw errors if there are no dyson spheres available
        var currentId = GameMain.localStar?.index ?? UIRoot.instance.uiGame.dysonEditor.selection.viewStar?.index ?? -1;
        for (var i = 0; i < GameMain.data.dysonSpheres.Length; i++)
        {
            if (GameMain.data.dysonSpheres[i] == null || i == currentId)
            {
                continue;
            }
            Log.Info($"Unload DysonSphere at system {GameMain.galaxy.stars[i].displayName} (Index: {i})");
            Multiplayer.Session.Network.SendPacket(new DysonSphereLoadRequest(i, DysonSphereRequestEvent.Unload));
            GameMain.data.dysonSpheres[i] = null;
        }
        IsNormal = true;
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
                "Reload".Translate(), () => RequestDysonSphere(starIndex));
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

    public static void ClearSelection(int starIndex, int layerId = -1)
    {
        var selection = UIRoot.instance.uiGame.dysonEditor.selection;
        if (selection.viewStar == null || selection.viewStar.index != starIndex)
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
