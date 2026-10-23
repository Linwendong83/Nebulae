using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Authority;

namespace NebulaeModel.Authority;

/// <summary>Shares layouts and validated parses of immutable canonical state buffers.</summary>
internal static class AuthorityStateFields
{
    private class Description { public PoolKind Kind; public WireField[] Fields; }
    private sealed class GroundDescription : Description { public GroundEnemyState State; }
    private sealed class SpaceDescription : Description { public SpaceEnemyState State; }
    private static readonly ConditionalWeakTable<byte[], Description> descriptions = new();
    private static readonly Dictionary<(PoolKind Kind, int Variant, int Unit, int Turret), Description> layouts = new();
    private static readonly object layoutGate = new();
    [ThreadStatic] private static Capture capture;
    private static readonly WireField[] factoryFields = CreateFactoryFields();
    private sealed class Capture
    {
        public byte[] Source;
        public readonly List<WireField> Fields = new();
    }

    internal static void Read(byte[] source, int offset, int length, int elementSize = 0)
    {
        if (capture != null && ReferenceEquals(source, capture.Source))
            capture.Fields.Add(new WireField(offset, length, elementSize));
    }

    public static WireField[] Describe(PoolKind kind, byte[] state)
    {
        if (state == null) return null;
        if (kind is PoolKind.Entity or PoolKind.Vegetable or PoolKind.Vein or PoolKind.Prebuild)
            return FactoryCombatStateCodec.HasValidLayout(state) ? factoryFields : null;
        if (descriptions.TryGetValue(state, out var cached))
            return cached.Kind == kind ? cached.Fields : DescribeCore(kind, state);
        return DescribeMissing(kind, state);
    }

    private static WireField[] CreateFactoryFields()
    {
        FactoryCombatStateCodec.TryEncode(default, out var bytes);
        return DescribeCore(PoolKind.Entity, bytes);
    }

    private static WireField[] DescribeMissing(PoolKind kind, byte[] state)
    {
        var description = descriptions.GetValue(state, bytes => CreateDescription(kind, bytes));
        return description.Kind == kind ? description.Fields : DescribeCore(kind, state);
    }

    private static WireField[] DescribeCore(PoolKind kind, byte[] state)
        => CreateDescription(kind, state).Fields;

    // Encoders call this only after finishing and validating their fresh buffer. Its layout is
    // shared by shape; no decode is needed for the next revision with the same native lengths.
    public static void RememberEncoded(PoolKind kind, byte[] state)
    {
        if (!TryShape(kind, state, out var shape)) return;
        Description layout;
        lock (layoutGate) layouts.TryGetValue(shape, out layout);
        if (layout == null)
        {
            var parsed = CreateDescription(kind, state);
            if (parsed.Fields == null) return;
            layout = ShareLayout(shape, parsed.Fields);
        }
        descriptions.Add(state, layout);
    }

    // These are canonical-buffer APIs, not raw wire decoders. The replica owns these immutable
    // arrays throughout validation, mirror installation and runtime auditing.
    public static bool TryReadGround(byte[] state, out GroundEnemyState value)
    {
        value = default;
        if (state == null) return false;
        if (descriptions.TryGetValue(state, out var cached) && cached is GroundDescription ground)
        { value = ground.State; return true; }
        var parsed = CreateDescription(PoolKind.GroundEnemy, state);
        if (parsed is not GroundDescription valid || parsed.Fields == null) return false;
        descriptions.Remove(state); descriptions.GetValue(state, _ => parsed);
        value = valid.State; return true;
    }

    public static bool TryReadSpace(byte[] state, out SpaceEnemyState value)
    {
        value = default;
        if (state == null) return false;
        if (descriptions.TryGetValue(state, out var cached) && cached is SpaceDescription space)
        { value = space.State; return true; }
        var parsed = CreateDescription(PoolKind.SpaceEnemy, state);
        if (parsed is not SpaceDescription valid || parsed.Fields == null) return false;
        descriptions.Remove(state); descriptions.GetValue(state, _ => parsed);
        value = valid.State; return true;
    }

    private static Description ShareLayout((PoolKind Kind, int Variant, int Unit, int Turret) shape, WireField[] fields)
    {
        lock (layoutGate)
        {
            if (layouts.TryGetValue(shape, out var shared)) return shared;
            shared = new Description { Kind = shape.Kind, Fields = fields };
            // The native codecs are bounded, but hostile combinations must not grow a permanent cache.
            if (layouts.Count < 256) layouts.Add(shape, shared);
            return shared;
        }
    }

    private static bool TryShape(PoolKind kind, byte[] bytes,
        out (PoolKind Kind, int Variant, int Unit, int Turret) shape)
    {
        shape = default;
        if (bytes == null || bytes.Length == 0) return false;
        var variant = bytes[0]; var native = 0; int size;
        if (kind == PoolKind.GroundEnemy)
        {
            if (!GroundEnemyKinds.IsDefined((GroundEnemyKind)variant)) return false;
            size = GroundEnemyStateCodec.FixedSize;
            if (bytes.Length < size) return false;
            native = BitConverter.ToInt32(bytes, size - 8);
        }
        else if (kind == PoolKind.SpaceEnemy)
        {
            if (!SpaceEnemyKinds.IsDefined((SpaceEnemyKind)variant)) return false;
            size = SpaceEnemyStateCodec.SizeFor((SpaceEnemyKind)variant);
            if (bytes.Length < size) return false;
            if ((SpaceEnemyKind)variant == SpaceEnemyKind.SpaceUnit)
                native = BitConverter.ToInt32(bytes, SpaceEnemyStateCodec.FixedSize + 24);
        }
        else return false;
        if (native < 0 || native > EnemyUnitStateCodec.MaxBytes || size + native > bytes.Length) return false;
        var turret = BitConverter.ToInt32(bytes, size - 4 + native);
        if (turret < 0 || turret > 512 || size + native + turret != bytes.Length) return false;
        shape = (kind, variant, native, turret); return true;
    }

    private static Description CreateDescription(PoolKind kind, byte[] state)
    {
        Description known = null;
        var hasShape = TryShape(kind, state, out var shape);
        if (hasShape) lock (layoutGate) layouts.TryGetValue(shape, out known);
        var previous = capture;
        var current = known == null ? new Capture { Source = state } : null;
        capture = current;
        try
        {
            GroundEnemyState ground = default; SpaceEnemyState space = default;
            var valid = kind switch
            {
                PoolKind.Entity or PoolKind.Vegetable or PoolKind.Vein or PoolKind.Prebuild =>
                    FactoryCombatStateCodec.TryDecode(state, 0, state.Length, out _, out _),
                PoolKind.GroundEnemy => GroundEnemyStateCodec.TryDecode(state, 0, state.Length, out ground, out _),
                PoolKind.SpaceEnemy => SpaceEnemyStateCodec.TryDecode(state, 0, state.Length, out space, out _),
                PoolKind.GroundCraft or PoolKind.SpaceCraft => CraftStateCodec.TryDecode(state, 0, state.Length, out _, out _),
                PoolKind.Turret => TurretStateCodec.TryDecode(state, out _),
                PoolKind.Base => ReadBase(state),
                PoolKind.Ruin => ReadRuin(state),
                PoolKind.HiveSummary => ReadHive(state),
                _ => false
            };
            var fields = known?.Fields;
            if (valid && fields == null && FieldDeltaCodec.ValidLayout(state, current.Fields)) fields = current.Fields.ToArray();
            if (!valid || fields == null) return new Description { Kind = kind };
            if (hasShape) fields = ShareLayout(shape, fields).Fields;
            return kind switch
            {
                PoolKind.GroundEnemy => new GroundDescription { Kind = kind, Fields = fields, State = ground },
                PoolKind.SpaceEnemy => new SpaceDescription { Kind = kind, Fields = fields, State = space },
                _ => new Description { Kind = kind, Fields = fields }
            };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OverflowException)
        { return new Description { Kind = kind }; }
        finally { capture = previous; }
    }

    private static bool ReadBase(byte[] state) { GroundBaseStateCodec.Decode(state); return true; }
    private static bool ReadRuin(byte[] state) { RuinStateCodec.Decode(state); return true; }

    private static bool ReadHive(byte[] state)
    {
        using var stream = new MemoryStream(state, false);
        using var reader = new AuthorityFieldReader(stream, state);
        reader.ReadInt32(); reader.ReadInt32();
        reader.ReadBoolean(); reader.ReadBoolean(); reader.ReadBoolean(); reader.ReadInt32();
        var evolve = new EvolveData(); evolve.Import(reader);
        reader.ReadInt32();
        for (var i = 0; i < 3; i++) reader.ReadDouble();
        reader.ReadInt32(); reader.ReadInt32();
        for (var i = 0; i < 3; i++) reader.ReadSingle();
        reader.ReadDouble(); reader.ReadSingle();
        for (var i = 0; i < 4; i++) reader.ReadSingle();
        for (var i = 0; i < 4; i++) reader.ReadDouble();
        for (var form = 0; form < 3; form++) HiveFormationStateCodec.Read(reader, form);
        return stream.Position == stream.Length;
    }
}

/// <summary>Recording is enabled only while describing the matching canonical state buffer.</summary>
internal sealed class AuthorityFieldReader(Stream stream, byte[] source) : BinaryReader(stream)
{
    private void Record(int length, int elementSize = 0) =>
        AuthorityStateFields.Read(source, (int)BaseStream.Position - length, length, elementSize);
    public override byte ReadByte() { var value = base.ReadByte(); Record(1); return value; }
    public override bool ReadBoolean() { var value = base.ReadBoolean(); Record(1); return value; }
    public override short ReadInt16() { var value = base.ReadInt16(); Record(2); return value; }
    public override ushort ReadUInt16() { var value = base.ReadUInt16(); Record(2); return value; }
    public override int ReadInt32() { var value = base.ReadInt32(); Record(4); return value; }
    public override uint ReadUInt32() { var value = base.ReadUInt32(); Record(4); return value; }
    public override long ReadInt64() { var value = base.ReadInt64(); Record(8); return value; }
    public override ulong ReadUInt64() { var value = base.ReadUInt64(); Record(8); return value; }
    public override float ReadSingle() { var value = base.ReadSingle(); Record(4); return value; }
    public override double ReadDouble() { var value = base.ReadDouble(); Record(8); return value; }
    public override byte[] ReadBytes(int count) { var value = base.ReadBytes(count); Record(value.Length, 1); return value; }
}
