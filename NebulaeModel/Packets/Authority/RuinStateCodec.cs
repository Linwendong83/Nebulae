using System;
using System.IO;
using UnityEngine;

namespace NebulaeModel.Packets.Authority;

internal static class RuinStateCodec
{
    // Native rendering/hash/collider handles are local and are never transmitted.
    public static byte[] Encode(in RuinData ruin)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(ruin.id); writer.Write(ruin.modelIndex); writer.Write(ruin.lifeTime);
        writer.Write(ruin.pos.x); writer.Write(ruin.pos.y); writer.Write(ruin.pos.z);
        writer.Write(ruin.rot.x); writer.Write(ruin.rot.y); writer.Write(ruin.rot.z); writer.Write(ruin.rot.w);
        return stream.ToArray();
    }
    public static RuinData Decode(byte[] bytes)
    {
        if (bytes?.Length != 38) throw new InvalidDataException("Invalid ruin payload.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = new NebulaeModel.Authority.AuthorityFieldReader(stream, bytes);
        var ruin = new RuinData
        {
            id = reader.ReadInt32(),
            modelIndex = reader.ReadInt16(),
            lifeTime = reader.ReadInt32(),
            pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            rot = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())
        };
        bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        if (ruin.id <= 0 || ruin.id > 1000000 || ruin.modelIndex <= 0 || ruin.modelIndex >= 2048 ||
            !Finite(ruin.pos.x) || !Finite(ruin.pos.y) || !Finite(ruin.pos.z) ||
            !Finite(ruin.rot.x) || !Finite(ruin.rot.y) || !Finite(ruin.rot.z) || !Finite(ruin.rot.w))
            throw new InvalidDataException("Invalid ruin state.");
        return ruin;
    }
}
