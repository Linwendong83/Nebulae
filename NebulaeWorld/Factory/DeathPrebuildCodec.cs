using System.IO;
using UnityEngine;

namespace NebulaeWorld.Factory;

// Only portable construction facts. Native pool, renderer, warning and repair indexes
// are allocated locally by AddPrebuildDataWithComponents, never imported from the host.
internal static class DeathPrebuildCodec
{
    internal const int MaxBytes = 1024 * 1024;
    public static byte[] Encode(in PrebuildData p)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(p.id); w.Write(p.protoId); w.Write(p.modelIndex); w.Write(p.ruinId);
        Write(w, p.pos); Write(w, p.rot); Write(w, p.pos2); Write(w, p.rot2);
        w.Write(p.tilt); w.Write(p.isDestroyed); w.Write(p.itemRequired);
        w.Write(p.pickOffset); w.Write(p.insertOffset); w.Write(p.recipeId); w.Write(p.filterId);
        w.Write(p.isWarningSetted); w.Write(p.paramCount);
        for (var i = 0; i < p.paramCount; i++) w.Write(p.parameters[i]);
        w.Write(p.content ?? "");
        if (stream.Length > MaxBytes) throw new InvalidDataException("Destroyed building parameters are too large.");
        return stream.ToArray();
    }

    public static PrebuildData Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return default;
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Destroyed building parameters are too large.");
        using var stream = new MemoryStream(bytes, false); using var r = new BinaryReader(stream);
        var p = new PrebuildData
        {
            id = r.ReadInt32(),
            protoId = r.ReadInt16(),
            modelIndex = r.ReadInt16(),
            ruinId = r.ReadInt32(),
            pos = Vector(r),
            rot = Rotation(r),
            pos2 = Vector(r),
            rot2 = Rotation(r),
            tilt = r.ReadSingle(),
            isDestroyed = r.ReadBoolean(),
            itemRequired = r.ReadInt32(),
            pickOffset = r.ReadInt16(),
            insertOffset = r.ReadInt16(),
            recipeId = r.ReadInt32(),
            filterId = r.ReadInt32(),
            isWarningSetted = r.ReadBoolean(),
            paramCount = r.ReadInt32()
        };
        if (p.id <= 0 || p.id > 0xffffff || p.protoId <= 0 || p.modelIndex <= 0 || !p.isDestroyed ||
            p.paramCount < 0 || (long)p.paramCount * 4 >= stream.Length - stream.Position)
            throw new InvalidDataException("Invalid destroyed building parameters.");
        p.parameters = new int[p.paramCount];
        for (var i = 0; i < p.paramCount; i++) p.parameters[i] = r.ReadInt32();
        p.content = r.ReadString();
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing destroyed building data.");
        return p;
    }
    private static void Write(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
    private static void Write(BinaryWriter w, Quaternion v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); w.Write(v.w); }
    private static Vector3 Vector(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    private static Quaternion Rotation(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}
