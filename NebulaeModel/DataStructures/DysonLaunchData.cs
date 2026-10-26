#region

using System.Collections.Generic;
using System;
using NebulaeModel.Contracts;
using NebulaeModel.Networking.Serialization;
using UnityEngine;

#endregion

namespace NebulaeModel.DataStructures;
internal class DysonLaunchData(int starIndex) : INetSerializable
{
    public const int MaxProjectiles = 4096;
    public DysonLaunchData() : this(0) { }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; } = starIndex;
    [NebulaeModel.Networking.WireOrder(1)]
    public List<Projectile> BulletList { get; set; } = [];
    [NebulaeModel.Networking.WireOrder(2)]
    public List<Projectile> RocketList { get; set; } = [];
    [NebulaeModel.Networking.WireOrder(3)]
    public int BulletCursor { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int RocketCursor { get; set; }
    public void Serialize(INetDataWriter writer)
    {
        if (BulletList.Count > MaxProjectiles || RocketList.Count > MaxProjectiles)
            throw new ArgumentOutOfRangeException(nameof(BulletList), "Launch batch exceeds packet limit");
        writer.Put(StarIndex);
        writer.Put((ushort)BulletList.Count);
        for (ushort i = 0; i < (ushort)BulletList.Count; i++)
        {
            var data = BulletList[i];
            writer.Put((byte)(data.PlanetId % 100));
            writer.Put(data.Tick);
            writer.Put((byte)data.LayerId);
            writer.Put(data.TargetId);
            writer.Put(data.LocalPos.ToFloat3());
        }
        writer.Put((ushort)RocketList.Count);
        for (ushort i = 0; i < (ushort)RocketList.Count; i++)
        {
            var data = RocketList[i];
            writer.Put((byte)(data.PlanetId % 100));
            writer.Put(data.Tick);
            writer.Put((byte)data.LayerId);
            writer.Put(data.TargetId);
            writer.Put(data.LocalPos.ToFloat3());
        }
    }

    public void Deserialize(INetDataReader reader)
    {
        StarIndex = reader.GetInt();
        var starId = (StarIndex + 1) * 100;
        int count = reader.GetUShort();
        if (count > MaxProjectiles) throw new ParseException("Invalid solar sail launch count");
        BulletList = new List<Projectile>(count);
        for (var i = 0; i < count; i++)
        {
            var data = new Projectile
            {
                PlanetId = reader.GetByte() + starId,
                Tick = reader.GetLong(),
                LayerId = reader.GetByte(),
                TargetId = reader.GetInt(),
                LocalPos = reader.GetFloat3().ToVector3()
            };
            BulletList.Add(data);
        }
        count = reader.GetUShort();
        if (count > MaxProjectiles) throw new ParseException("Invalid rocket launch count");
        RocketList = new List<Projectile>(count);
        for (ushort i = 0; i < count; i++)
        {
            var data = new Projectile
            {
                PlanetId = reader.GetByte() + starId,
                Tick = reader.GetLong(),
                LayerId = reader.GetByte(),
                TargetId = reader.GetInt(),
                LocalPos = reader.GetFloat3().ToVector3()
            };
            RocketList.Add(data);
        }
    }

    internal struct Projectile
    {
        public int PlanetId;
        public long Tick;
        public int LayerId;
        public int TargetId;
        public Vector3 LocalPos;
    }
}
