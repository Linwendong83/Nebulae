using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Logistics.ControlPanel;
using UnityEngine;

namespace NebulaeWorld.Logistics;

internal sealed class FacilityDirectoryManager : IDisposable
{
    private readonly HashSet<object> consumers = new();
    private readonly HashSet<INebulaeConnection> viewers = new();
    private readonly Dictionary<INebulaeConnection, FacilityRowsSubscription> rows = new();
    private readonly Dictionary<INebulaeConnection, Dictionary<string, byte[]>> sentRows = new();
    private readonly List<FacilityDirectoryEntry> entries = new();
    private readonly List<UIControlPanelObjectEntry> matchedEntries = new();
    private (int Index, int AstroId, int ObjId, EControlPanelEntryType Type)[] lastRows = [];
    private FacilityDirectoryPacket snapshot;
    private float lastUpdate;
    private int generation;
    private string rowTargets = "";
    public long Revision { get; private set; }
    public bool HasSnapshot { get; private set; }
    public Action<INebulaeConnection, LCPObjectEntryRequest> SendRow { get; set; }

    public void Subscribe(object consumer)
    {
        if (!consumers.Add(consumer) || consumers.Count != 1) return;
        Multiplayer.Session.Network.SendPacket(new FacilityDirectoryRequest { Subscribe = true });
    }
    public void Unsubscribe(object consumer)
    {
        if (consumers.Remove(consumer) && consumers.Count == 0)
            Multiplayer.Session.Network.SendPacket(new FacilityDirectoryRequest { Subscribe = false });
    }
    public void Subscribe(INebulaeConnection connection, bool subscribe)
    {
        if (!subscribe) { viewers.Remove(connection); return; }
        Capture();
        viewers.Add(connection);
        connection.SendPacket(snapshot);
    }
    public void SetRows(INebulaeConnection connection, FacilityRowsSubscription packet)
    {
        var count = packet.Indices?.Length ?? -1;
        if (count < 0 || count > 512 || packet.Planets?.Length != count || packet.Entities?.Length != count || packet.Types?.Length != count) return;
        if (count == 0) { rows.Remove(connection); sentRows.Remove(connection); return; }
        // Serializer reuses packet instances and arrays; subscriptions own their copy.
        rows[connection] = new FacilityRowsSubscription
        {
            Generation = packet.Generation,
            Indices = (int[])packet.Indices.Clone(),
            Planets = (int[])packet.Planets.Clone(),
            Entities = (int[])packet.Entities.Clone(),
            Types = (short[])packet.Types.Clone()
        };
        sentRows.Remove(connection);
        PublishRows(connection, rows[connection]);
    }
    public void Remove(INebulaeConnection connection) { viewers.Remove(connection); rows.Remove(connection); sentRows.Remove(connection); }
    public void Update()
    {
        if (Time.realtimeSinceStartup - lastUpdate < 1) return;
        lastUpdate = Time.realtimeSinceStartup;
        foreach (var connection in viewers.Where(c => !c.IsAlive).ToArray()) Remove(connection);
        foreach (var connection in rows.Keys.Where(c => !c.IsAlive).ToArray()) Remove(connection);
        if (viewers.Count > 0) Capture();
        FlushRows();
    }
    internal void FlushRows() { foreach (var pair in rows) PublishRows(pair.Key, pair.Value); }
    private void PublishRows(INebulaeConnection connection, FacilityRowsSubscription subscription)
    {
        for (var i = 0; i < subscription.Indices.Length; i++)
            SendRow?.Invoke(connection, new LCPObjectEntryRequest
            {
                Generation = subscription.Generation,
                Index = subscription.Indices[i],
                AstroId = subscription.Planets[i],
                ObjId = -subscription.Entities[i],
                EntryType = subscription.Types[i]
            });
    }
    public void SendChanged<T>(INebulaeConnection connection, T packet, int index) where T : class, new()
    {
        if (!sentRows.TryGetValue(connection, out var sent)) sentRows[connection] = sent = new Dictionary<string, byte[]>();
        var key = typeof(T).FullName + ":" + index;
        var bytes = Multiplayer.Session.Network.WriteOnce(packet);
        if (sent.TryGetValue(key, out var previous) && FieldDeltaCodec.Equal(bytes, previous)) return;
        sent[key] = bytes;
        connection.SendRawPacket(bytes);
    }
    public void UpdateVisibleRows(UIControlPanelWindow window)
    {
        // Gather the visible remote rows without sorting or allocating. The panel can sit
        // open for a long time, and the old Where/OrderBy/ToArray/string.Join ran on every
        // single frame it was open.
        matchedEntries.Clear();
        if (window.active)
        {
            foreach (var entry in window.objectEntryPool)
            {
                if (entry == null || !entry.active) continue;
                var target = entry.target;
                if (GameMain.galaxy.PlanetById(target.astroId)?.factory != null) continue;
                if (target.objId <= 0 || target.entryType == EControlPanelEntryType.Marker) continue;
                matchedEntries.Add(entry);
            }
        }

        // Fast path: same membership, same pool order and same values as the last send.
        if (matchedEntries.Count == lastRows.Length)
        {
            var unchanged = true;
            for (var i = 0; i < matchedEntries.Count; i++)
            {
                var entry = matchedEntries[i];
                var row = lastRows[i];
                if (entry.index != row.Index || entry.target.astroId != row.AstroId ||
                    entry.target.objId != row.ObjId || entry.target.entryType != row.Type)
                {
                    unchanged = false;
                    break;
                }
            }
            if (unchanged) return;
        }

        // Slow path: values, membership or pool order changed. The ordered signature keeps
        // the comparison order-insensitive, exactly like the original one.
        var targets = matchedEntries.OrderBy(e => e.index).ToArray();
        var signature = string.Join(";", targets.Select(e => $"{e.index}:{e.target.astroId}:{e.target.objId}:{(int)e.target.entryType}"));
        if (signature != rowTargets)
        {
            rowTargets = signature;
            Multiplayer.Session.Network.SendPacket(new FacilityRowsSubscription
            {
                Generation = ++generation,
                Indices = targets.Select(e => e.index).ToArray(),
                Planets = targets.Select(e => e.target.astroId).ToArray(),
                Entities = targets.Select(e => e.target.objId).ToArray(),
                Types = targets.Select(e => (short)e.target.entryType).ToArray()
            });
        }
        lastRows = matchedEntries.Select(e => (e.index, e.target.astroId, e.target.objId, e.target.entryType)).ToArray();
    }
    public bool AcceptRow(int packetGeneration) => packetGeneration == generation;

    private void Capture()
    {
        using var writer = new BinaryUtils.Writer();
        var data = CaptureEntries();
        writer.BinaryWriter.Write(data.Count);
        foreach (var entry in data) entry.Write(writer.BinaryWriter);
        var bytes = writer.CloseAndGetBytes();
        if (snapshot != null && FieldDeltaCodec.Equal(bytes, snapshot.Data)) return;
        snapshot = new FacilityDirectoryPacket { Revision = ++Revision, Data = bytes };
        byte[] encoded = null;
        foreach (var viewer in viewers)
        {
            encoded ??= Multiplayer.Session.Network.WriteOnce(snapshot);
            viewer.SendRawPacket(encoded);
        }
    }
    internal static List<FacilityDirectoryEntry> CaptureEntries()
    {
        var result = new List<FacilityDirectoryEntry>();
        if (GameMain.data?.factories == null) return result;
        for (var f = 0; f < GameMain.data.factoryCount; f++)
        {
            var factory = GameMain.data.factories[f];
            if (factory == null) continue;
            var transport = factory.transport;
            for (var i = 1; i < transport.stationCursor; i++)
            {
                var station = transport.stationPool[i];
                if (station == null || station.id != i) continue;
                var type = station.isStellar ? (station.isCollector ? EControlPanelEntryType.OrbitCollector : EControlPanelEntryType.InterstellarStation) :
                    (station.isVeinCollector ? EControlPanelEntryType.VeinCollector : EControlPanelEntryType.LocalStation);
                var entry = Entry(factory, station.entityId, i, type); entry.GId = station.gid;
                var storage = station.storage ?? Array.Empty<StationStore>();
                entry.Items = storage.Select(s => s.itemId).ToArray(); entry.LocalModes = storage.Select(s => (int)s.localLogic).ToArray();
                entry.RemoteModes = storage.Select(s => (int)s.remoteLogic).ToArray(); result.Add(entry);
            }
            for (var i = 1; i < transport.dispenserCursor; i++)
            {
                var dispenser = transport.dispenserPool[i];
                if (dispenser == null || dispenser.id != i) continue;
                var entry = Entry(factory, dispenser.entityId, i, EControlPanelEntryType.Dispenser);
                entry.Items = new[] { dispenser.filter }; entry.LocalModes = new[] { 0 }; entry.RemoteModes = new[] { 0 };
                entry.PlayerMode = (int)dispenser.playerMode; entry.StorageMode = (int)dispenser.storageMode; result.Add(entry);
            }
            var markers = factory.digitalSystem.markers;
            for (var i = 1; i < markers.cursor; i++)
            {
                var marker = markers[i]; if (marker == null || marker.id != i) continue;
                var entry = Entry(factory, marker.entityId, i, EControlPanelEntryType.Marker);
                entry.Name = marker.name; entry.Tags = marker.tags ?? ""; entry.Comment = factory.ReadMarkerComment(i); result.Add(entry);
            }
        }
        return result;
    }
    private static FacilityDirectoryEntry Entry(PlanetFactory factory, int entity, int id, EControlPanelEntryType type) =>
        new()
        {
            PlanetId = factory.planetId,
            EntityId = entity,
            Id = id,
            Type = type,
            ProtoId = factory.entityPool[entity].protoId,
            Name = factory.ReadExtraInfoOnEntity(entity) ?? ""
        };

    public void Receive(FacilityDirectoryPacket packet)
    {
        if (packet.Revision <= Revision || packet.Data == null) return;
        using var reader = new BinaryUtils.Reader(packet.Data);
        var count = reader.BinaryReader.ReadInt32();
        if (count < 0 || count > 1000000) throw new InvalidDataException("Invalid facility count");
        var received = new List<FacilityDirectoryEntry>(count);
        for (var i = 0; i < count; i++) received.Add(FacilityDirectoryEntry.Read(reader.BinaryReader));
        entries.Clear(); entries.AddRange(received); Revision = packet.Revision; HasSnapshot = true;
        var ui = UIRoot.instance?.uiGame;
        if (ui?.controlPanelWindow != null) ui.controlPanelWindow.needDetermineFilterResults = true;
        foreach (var panel in new[] { ui?.stationWindow?.uiRoutePanel, ui?.controlPanelWindow?.stationInspector?.uiRoutePanel })
            if (panel != null && panel.active)
            {
                panel.RefreshSearchEntries();
                if (panel.searchNameInputField.isFocused) panel.OnSearchNameInputChanged(panel.searchNameInputField.text);
            }
    }
    public void DetermineResults(UIControlPanelWindow window)
    {
        var localPlanet = GameMain.localPlanet?.astroId ?? 0; var localStar = GameMain.localStar?.astroId ?? 0;
        // Fresh local configuration wins over the periodically replicated directory.
        var local = CaptureEntries(); var loaded = new HashSet<int>(GameMain.data.factories.Take(GameMain.data.factoryCount).Where(f => f != null).Select(f => f.planetId));
        var all = entries.Where(e => !loaded.Contains(e.PlanetId)).Concat(local).ToArray();
        window.ResetObjectEntryPool(); window.ClearFilterResults(); window.sortedAstros.Clear();
        var groups = all.GroupBy(e => e.PlanetId).ToList();
        if ((window.filter.sortMethod & ControlPanelFilter.ESortMethod.AstroDistance) != 0)
            groups.Sort((a, b) => Distance(a.Key).CompareTo(Distance(b.Key)));
        else groups.Sort((a, b) => a.Key.CompareTo(b.Key));
        foreach (var group in groups)
        {
            var filter = window.filter;
            if (filter.astroFilter != -1 && filter.astroFilter != group.Key && filter.astroFilter != group.Key / 100 * 100 &&
                !(filter.astroFilter == 0 && (group.Key == localPlanet || (localPlanet == 0 && group.Key / 100 * 100 == localStar)))) continue;
            var matched = group.Where(e => e.Matches(filter)).OrderBy(e => Category(e.Type)).ThenBy(e => e.Id).ToList();
            if (matched.Count == 0) continue;
            if ((filter.sortMethod & ControlPanelFilter.ESortMethod.EntityName) != 0)
                matched.Sort((a, b) => Category(a.Type) != Category(b.Type) ? Category(a.Type).CompareTo(Category(b.Type)) : window.CompareEntityName(a.Name, b.Name));
            window.sortedAstros.Add(new ControlPanelAstroData(group.Key, Distance(group.Key)));
            var planet = new ControlPanelTarget(EObjectType.None, 0, group.Key, EControlPanelEntryType.Planet);
            window.AddFilterResult(ref planet, true);
            var visible = group.Key == localPlanet ? !window.astroExpands.Contains(0) : window.astroExpands.Contains(group.Key);
            foreach (var entry in matched)
            {
                var target = new ControlPanelTarget(EObjectType.None, entry.EntityId, entry.PlanetId, entry.Type);
                window.AddFilterResult(ref target, visible);
            }
        }
        window.resultGeneration++; window.needDetermineEntryVisible = true; window.needDetermineFilterResults = false;
        window.ReconnSelectionOnDetermineResults();
    }
    private static double Distance(int planet) => (GameMain.data.spaceSector.galaxyAstros[planet].uPos - GameMain.mainPlayer.uPosition).sqrMagnitude;
    private static int Category(EControlPanelEntryType type) => type <= EControlPanelEntryType.VeinCollector ? 0 : (int)type;
    public IEnumerable<FacilityDirectoryEntry> Search(string text, int planet, bool exact) => entries.Where(e => e.GId > 0 &&
        e.PlanetId != planet && !string.IsNullOrEmpty(e.Name) && (exact ? e.Name == text : e.Name.IndexOf(text, StringComparison.Ordinal) >= 0));
    public void Dispose() { consumers.Clear(); viewers.Clear(); rows.Clear(); sentRows.Clear(); entries.Clear(); SendRow = null; }
}
