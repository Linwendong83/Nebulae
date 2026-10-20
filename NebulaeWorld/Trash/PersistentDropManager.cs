using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NebulaeModel.Packets.Trash;
using NebulaeModel.Utils;
using NebulaeWorld.GameStates;
using UnityEngine;

namespace NebulaeWorld.Trash;

internal sealed class PersistentDropManager : IDisposable
{
    private const int MarkerLow = -1708469010;
    private const int MarkerHigh = -1708469011;
    private readonly Dictionary<string, DropRecord> serverRecords = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PersistentDropPacket> pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> displayed = new(StringComparer.Ordinal);
    private readonly Queue<string> displayOrder = new();
    private long sequence;
    private long lastSend;
    private bool initialized;
    private string path;

    private void Initialize()
    {
        if (initialized) return;
        initialized = true;
        var server = Multiplayer.Session.IsServer;
        path = server ? Path.Combine(LegacyDataMigration.DataDirectory(GameConfig.gameSaveFolder), SaveManager.WorldId, "drops.log") :
            Path.Combine(LegacyDataMigration.DataDirectory(GameConfig.propertyFolder), AtomicFile.IdentityFileName(MetadataManager.LocalIdentity), SaveManager.WorldId + ".drops");
        foreach (var bytes in BinaryRecordLog.Read(path))
        {
            using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
            var record = new DropRecord
            {
                Sequence = reader.ReadInt64(),
                Owner = reader.ReadString(),
                Packet = new PersistentDropPacket
                {
                    OperationId = reader.ReadString(),
                    Ordinal = reader.ReadInt32(),
                    Acknowledgement = reader.ReadBoolean()
                }
            };
            var length = reader.ReadInt32();
            if (length < 0 || length > 4096) throw new InvalidDataException("Invalid persistent drop payload");
            record.Packet.Data = reader.ReadBytes(length);
            var key = Key(record.Packet);
            if (server)
            {
                serverRecords[key] = record;
                sequence = Math.Max(sequence, record.Sequence);
            }
            else if (record.Packet.Acknowledgement) pending.Remove(key);
            else pending[key] = record.Packet;
        }
        if (server)
            foreach (var record in serverRecords.Values.OrderBy(x => x.Sequence)) ApplyServer(record);
    }

    public int Capture(in TrashObject obj, in TrashData data)
    {
        Initialize();
        var transactions = Multiplayer.Session.PropertyTransactions;
        var packet = new PersistentDropPacket
        {
            OperationId = transactions.ActiveOperationId,
            Ordinal = transactions.TrashOrdinal++,
            Data = Encode(obj, data)
        };
        if (Multiplayer.Session.IsServer) return Accept(packet, MetadataManager.LocalIdentity);
        var key = Key(packet);
        if (!pending.ContainsKey(key))
        {
            Append(new DropRecord { Packet = packet, Owner = MetadataManager.LocalIdentity });
            pending.Add(key, packet);
        }
        Multiplayer.Session.Network.SendPacket(pending[key]);
        return 0;
    }

    public int Accept(PersistentDropPacket packet, string owner)
    {
        Initialize();
        if (!Multiplayer.Session.PropertyTransactions.OwnsCommittedOperation(packet.OperationId, owner)) return 0;
        if (packet.Ordinal < 0 || packet.Ordinal > 4096) throw new InvalidDataException("Invalid drop ordinal");
        Decode(packet.Data, out _, out _);
        var key = Key(packet);
        if (serverRecords.TryGetValue(key, out var existing)) return existing.Packet.TrashId;
        var record = new DropRecord { Sequence = ++sequence, Owner = owner, Packet = packet };
        Append(record); // Commit the exact drop before creating the world object.
        serverRecords.Add(key, record);
        ApplyServer(record);
        return record.Packet.TrashId;
    }

    private void ApplyServer(DropRecord record)
    {
        if (record.Sequence <= Watermark()) return;
        Decode(record.Packet.Data, out var obj, out var data);
        using (Multiplayer.Session.Trashes.IsIncomingRequest.On())
            record.Packet.TrashId = GameMain.data.trashSystem.container.NewTrash(obj, data);
        if (record.Packet.TrashId < 0) throw new InvalidOperationException("Could not create a committed inventory drop");
        GameMain.history.featureValues[MarkerLow] = (int)record.Sequence;
        GameMain.history.featureValues[MarkerHigh] = (int)(record.Sequence >> 32);
        Multiplayer.Session.Server.SendPacket(record.Packet);
    }

    public void Receive(PersistentDropPacket packet)
    {
        Initialize();
        var key = Key(packet);
        if (packet.Acknowledgement)
        {
            if (pending.Remove(key)) Append(new DropRecord
            {
                Owner = MetadataManager.LocalIdentity,
                Packet = new PersistentDropPacket
                { OperationId = packet.OperationId, Ordinal = packet.Ordinal, Acknowledgement = true, Data = Array.Empty<byte>() }
            });
            return;
        }
        if (packet.TrashId < 0 || !displayed.Add(key)) return;
        displayOrder.Enqueue(key);
        if (displayOrder.Count > 16384) displayed.Remove(displayOrder.Dequeue());
        var container = GameMain.data.trashSystem.container;
        if (packet.TrashId < container.trashObjPool.Length && container.trashObjPool[packet.TrashId].item > 0) return;
        Decode(packet.Data, out var obj, out var data);
        data.warningId = -1;
        TrashManager.SetNextTrashId(packet.TrashId);
        using (Multiplayer.Session.Trashes.IsIncomingRequest.On()) container.NewTrash(obj, data);
        Multiplayer.Session.Trashes.ClientTrashCount++;
    }

    public void GameTick()
    {
        if (!Multiplayer.Session.IsGameLoaded) return;
        Initialize();
        if (Multiplayer.Session.IsServer || DateTime.UtcNow.Ticks - lastSend < TimeSpan.TicksPerSecond * 2) return;
        lastSend = DateTime.UtcNow.Ticks;
        foreach (var packet in pending.Values) Multiplayer.Session.Network.SendPacket(packet);
    }

    private void Append(DropRecord record)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        var packet = record.Packet;
        writer.Write(record.Sequence); writer.Write(record.Owner); writer.Write(packet.OperationId);
        writer.Write(packet.Ordinal); writer.Write(packet.Acknowledgement); writer.Write(packet.Data.Length); writer.Write(packet.Data);
        BinaryRecordLog.Append(path, stream.ToArray());
    }

    private static long Watermark()
    {
        GameMain.history.featureValues.TryGetValue(MarkerLow, out var low);
        GameMain.history.featureValues.TryGetValue(MarkerHigh, out var high);
        return ((long)high << 32) | (uint)low;
    }

    private static string Key(PersistentDropPacket packet)
    {
        if (!Guid.TryParseExact(packet.OperationId, "N", out _)) throw new InvalidDataException("Invalid drop operation");
        return packet.OperationId + ":" + packet.Ordinal;
    }

    private static byte[] Encode(in TrashObject obj, in TrashData data)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(obj.item); w.Write(obj.count); w.Write(obj.inc);
        w.Write(data.life); w.Write(data.nearPlanetId); w.Write(data.nearStarId); w.Write(data.nearStarGravity);
        w.Write((double)data.uPos.x); w.Write((double)data.uPos.y); w.Write((double)data.uPos.z);
        w.Write(data.uRot.x); w.Write(data.uRot.y); w.Write(data.uRot.z); w.Write(data.uRot.w);
        w.Write((double)data.uVel.x); w.Write((double)data.uVel.y); w.Write((double)data.uVel.z);
        w.Write((double)data.uAgl.x); w.Write((double)data.uAgl.y); w.Write((double)data.uAgl.z);
        return stream.ToArray();
    }

    private static void Decode(byte[] bytes, out TrashObject obj, out TrashData data)
    {
        using var stream = new MemoryStream(bytes, false); using var r = new BinaryReader(stream);
        var item = r.ReadInt32(); var count = r.ReadInt32(); var inc = r.ReadInt32();
        if (LDB.items.Select(item) == null || count <= 0 || inc < 0) throw new InvalidDataException("Invalid inventory drop");
        data = new TrashData
        {
            life = r.ReadInt32(),
            nearPlanetId = r.ReadInt32(),
            nearStarId = r.ReadInt32(),
            nearStarGravity = r.ReadDouble(),
            uPos = new VectorLF3(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()),
            uRot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            uVel = new VectorLF3(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()),
            uAgl = new VectorLF3(r.ReadDouble(), r.ReadDouble(), r.ReadDouble())
        };
        if (stream.Position != stream.Length || double.IsNaN(data.uPos.x) || double.IsInfinity(data.uPos.x))
            throw new InvalidDataException("Invalid inventory drop pose");
        obj = new TrashObject(item, count, inc,
            Maths.QInvRotateLF(GameMain.data.relativeRot, data.uPos - GameMain.data.relativePos),
            Quaternion.Inverse(GameMain.data.relativeRot) * data.uRot);
    }

    public void Dispose() { pending.Clear(); serverRecords.Clear(); displayed.Clear(); displayOrder.Clear(); }
    private sealed class DropRecord
    {
        public long Sequence;
        public string Owner;
        public PersistentDropPacket Packet;
    }
}
