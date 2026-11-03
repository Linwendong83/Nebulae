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
        // modelIndex 0 is a legal ruin, not a corrupt one: a belt killed by the dark fog keeps a
        // loot prefab whose ruin id is 0, so PlanetFactory.KillEntityFinally records modelArray[0]
        // (null) as the model and a lifeTime of -1. The game itself renders nothing for it. This
        // used to be refused, which made every Ruin baseline containing such a wreck uninstalled
        // forever and retried once per frame by the client.
        if (ruin.id <= 0 || ruin.id > 1000000 || ruin.modelIndex < 0 || ruin.modelIndex >= ModelProto.MAX_ARRAY_SIZE ||
            !Finite(ruin.pos.x) || !Finite(ruin.pos.y) || !Finite(ruin.pos.z) ||
            !Finite(ruin.rot.x) || !Finite(ruin.rot.y) || !Finite(ruin.rot.z) || !Finite(ruin.rot.w))
            // Components, not Vector3/Quaternion.ToString(): Unity's formatter walks state a
            // diagnostic must not depend on, and a diagnostic must never throw.
            throw new InvalidDataException(
                $"Invalid ruin state. id={ruin.id} modelIndex={ruin.modelIndex} lifeTime={ruin.lifeTime} " +
                $"pos=({ruin.pos.x:R},{ruin.pos.y:R},{ruin.pos.z:R}) " +
                $"rot=({ruin.rot.x:R},{ruin.rot.y:R},{ruin.rot.z:R},{ruin.rot.w:R})");
        return ruin;
    }
}
