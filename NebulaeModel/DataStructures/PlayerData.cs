#region

using System;
using System.IO;
using NebulaeModel.DataStructures;
using NebulaeModel.Contracts;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;

#endregion

namespace NebulaeModel.DataStructures;
internal class PlayerData : IPlayerData
{
    public PlayerData()
    {
        Appearance = null;
        DIYAppearance = null;
        DIYItemId = Array.Empty<int>();
        DIYItemValue = Array.Empty<int>();
        DashboardData = null;
        VegetableCollectionData = Array.Empty<byte>();
    }

    public PlayerData(ushort playerId, int localPlanetId, string username = null, Float3 localPlanetPosition = new(),
        Double3 position = new(), Float3 rotation = new(), Float3 bodyRotation = new())
    {
        PlayerId = playerId;
        LocalPlanetId = localPlanetId;
        Username = !string.IsNullOrWhiteSpace(username) ? username : $"Player {playerId}";
        LocalPlanetPosition = localPlanetPosition;
        UPosition = position;
        Rotation = rotation;
        BodyRotation = bodyRotation;
        Mecha = new MechaData();
        Appearance = null;
        DIYAppearance = null;
        DIYItemId = Array.Empty<int>();
        DIYItemValue = Array.Empty<int>();
        DashboardData = null;
        VegetableCollectionData = Array.Empty<byte>();
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public string Username { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int LocalPlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public Float3 LocalPlanetPosition { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public Double3 UPosition { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public Float3 Rotation { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public Float3 BodyRotation { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public IMechaData Mecha { get; set; }
    /// <summary>Star this seat is in: a star id, -1 while in space, 0 while still unknown. Serialized in the payload tail (see Serialize).</summary>
    [NebulaeModel.Networking.WireOrder(8)]
    public int LocalStarId { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public MechaAppearance Appearance { get; set; }
    [NebulaeModel.Networking.WireOrder(10)]
    public MechaAppearance DIYAppearance { get; set; }
    [NebulaeModel.Networking.WireOrder(11)]
    public int[] DIYItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(12)]
    public int[] DIYItemValue { get; set; }
    [NebulaeModel.Networking.WireOrder(13)]
    public byte[] DashboardData { get; set; }
    [NebulaeModel.Networking.WireOrder(14)]
    public PlayerLifeData Life { get; set; } = new();

    /// <summary>Updates the locally simulated personal state of this authenticated seat.</summary>
    public bool TryApplyPersonalSnapshot(PlayerData snapshot)
    {
        if (snapshot?.Life == null || snapshot.Mecha?.ReactorStorage == null ||
            snapshot.Mecha.Inventory == null || snapshot.Life.Revision <= Life.Revision ||
            snapshot.Life.DeathCount < Life.DeathCount) return false;
        // Keep connection identity, accepted movement and server-owned statistics/preferences.
        // These checkpoints never seed or overwrite the authority resource ledger.
        var fight = CombatAuthoritative ? Mecha.FightData : null;
        var life = CombatAuthoritative ? Life : null;
        Mecha = snapshot.Mecha;
        if (fight != null) Mecha.FightData = fight;
        Life = snapshot.Life;
        if (life != null)
        {
            Life.IsAlive = life.IsAlive;
            Life.DeathCount = life.DeathCount;
            Life.InvincibleTicks = life.InvincibleTicks;
        }
        return true;
    }
    // Session-only ownership; persisted fight values remain in the existing save layout.
    [NebulaeModel.Networking.WireOrder(15)]
    public bool CombatAuthoritative { get; set; }
    [NebulaeModel.Networking.WireOrder(16)]
    public EMovementState AcceptedMovementState { get; set; }
    [NebulaeModel.Networking.WireOrder(17)]
    public bool AcceptedWarping { get; set; }
    [NebulaeModel.Networking.WireOrder(18)]
    public Double3 AcceptedVelocityU { get; set; }
    [NebulaeModel.Networking.WireOrder(19)]
    public Float3 AcceptedVelocityL { get; set; }
    [NebulaeModel.Networking.WireOrder(20)]
    public long AcceptedMovementTick { get; set; }
    [NebulaeModel.Networking.WireOrder(21)]
    public byte[] PersonalKillData { get; set; } = Array.Empty<byte>();
    [NebulaeModel.Networking.WireOrder(22)]
    public byte[] VegetableCollectionData { get; set; } = Array.Empty<byte>();
    [NebulaeModel.Networking.WireOrder(23)]
    public string PersistentId { get; set; } = "";
    [NebulaeModel.Networking.WireOrder(24)]
    public bool SessionCounted { get; set; }

    public void Serialize(INetDataWriter writer)
    {
        writer.Put(Username);
        writer.Put(PlayerId);
        writer.Put(LocalPlanetId);
        LocalPlanetPosition.Serialize(writer);
        UPosition.Serialize(writer);
        Rotation.Serialize(writer);
        BodyRotation.Serialize(writer);
        Mecha.Serialize(writer);
        writer.Put(Appearance != null);
        if (Appearance != null)
        {
            using var ms = new MemoryStream();
            using (var wr = new BinaryWriter(ms))
            {
                Appearance.Export(wr);
            }
            var export = ms.ToArray();
            writer.Put(export.Length);
            writer.Put(export);
        }
        writer.Put(DIYAppearance != null);
        if (DIYAppearance != null)
        {
            using var ms = new MemoryStream();
            using (var wr = new BinaryWriter(ms))
            {
                DIYAppearance.Export(wr);
            }
            var export = ms.ToArray();
            writer.Put(export.Length);
            writer.Put(export);
        }
        writer.Put(DIYItemId.Length);
        for (var i = 0; i < DIYItemId.Length; i++)
        {
            writer.Put(DIYItemId[i]);
            writer.Put(DIYItemValue[i]);
        }
        writer.Put(DashboardData != null);
        if (DashboardData != null)
        {
            writer.Put(DashboardData.Length);
            writer.Put(DashboardData);
        }
        Life.Serialize(writer);
        writer.Put(PersonalKillData.Length);
        writer.Put(PersonalKillData);
        writer.Put(VegetableCollectionData.Length);
        writer.Put(VegetableCollectionData);
        // Flight-state tail: lets a rejoining player resume sailing/flying where they left off.
        writer.Put((byte)AcceptedMovementState);
        writer.Put(AcceptedWarping);
        AcceptedVelocityU.Serialize(writer);
        AcceptedVelocityL.Serialize(writer);
        // Local-star tail: star broadcasts, foundation builds and battle visuals all filter on the
        // seat's star, so it has to survive the save instead of waiting for the client to re-announce
        // it after loading. -1 means "in space", 0 means "not known yet".
        writer.Put(LocalStarId);
    }

    public void Deserialize(INetDataReader reader)
    {
        ReadPosition(reader);
        Mecha = new MechaData();
        Mecha.Deserialize(reader);
        ReadAppearanceAndDashboard(reader);
        ReadLife(reader);
        var length = ReadVegetationLength(reader);
        VegetableCollectionData = new byte[length];
        reader.GetBytes(VegetableCollectionData, length);
        ReadFlightState(reader);
        LocalStarId = reader.GetInt();
    }

    private void ReadPosition(INetDataReader reader)
    {
        Username = reader.GetString();
        PlayerId = reader.GetUShort();
        LocalPlanetId = reader.GetInt();
        LocalPlanetPosition = reader.GetFloat3();
        UPosition = reader.GetDouble3();
        Rotation = reader.GetFloat3();
        BodyRotation = reader.GetFloat3();
    }

    private void ReadAppearanceAndDashboard(INetDataReader reader)
    {
        Appearance = reader.GetBool() ? MechaAppearanceCodec.Read(reader.GetBytesWithLength()) : null;
        DIYAppearance = reader.GetBool() ? MechaAppearanceCodec.Read(reader.GetBytesWithLength()) : null;
        var DIYItemLen = reader.GetInt();
        if (DIYItemLen < 0 || DIYItemLen > reader.AvailableBytes / (sizeof(int) * 2))
            throw new ParseException("Invalid DIY item count");
        DIYItemId = new int[DIYItemLen];
        DIYItemValue = new int[DIYItemLen];
        for (var i = 0; i < DIYItemLen; i++)
        {
            DIYItemId[i] = reader.GetInt();
            DIYItemValue[i] = reader.GetInt();
        }
        var isDashboardPresent = reader.GetBool();
        if (isDashboardPresent)
        {
            DashboardData = reader.GetBytesWithLength();
        }
        else
        {
            DashboardData = null;
        }
    }

    private void ReadLife(INetDataReader reader)
    {
        Life = new PlayerLifeData();
        Life.Deserialize(reader);
        var killLength = reader.GetInt();
        if (killLength < 0 || killLength > 128 * 1024 * 1024 || killLength > reader.AvailableBytes)
            throw new ParseException("Invalid personal kill statistics size");
        PersonalKillData = new byte[killLength];
        reader.GetBytes(PersonalKillData, killLength);
    }

    private static int ReadVegetationLength(INetDataReader reader)
    {
        var length = reader.GetInt();
        if (length < 0 || length > VegetableCollectionState.MaxBytes)
            throw new InvalidDataException("Invalid vegetation collection size");
        return length;
    }

    private void ReadFlightState(INetDataReader reader)
    {
        var movementState = (EMovementState)reader.GetByte();
        if (movementState < EMovementState.Walk || movementState > EMovementState.Sail)
            throw new ParseException("Invalid player movement state");
        AcceptedMovementState = movementState;
        AcceptedWarping = reader.GetBool();
        AcceptedVelocityU = reader.GetDouble3();
        AcceptedVelocityL = reader.GetFloat3();
    }

    public IPlayerData CreateCopyWithoutMechaData()
    {
        var copy = new PlayerData(PlayerId, LocalPlanetId, Username, LocalPlanetPosition, UPosition, Rotation, BodyRotation);
        copy.DashboardData = DashboardData;
        copy.Life = Life;
        copy.VegetableCollectionData = VegetableCollectionData;
        // The join broadcast seeds every other client's model of this player, and the star drives
        // name tags, battle visuals and broadcast filtering on those clients.
        copy.LocalStarId = LocalStarId;
        return copy;
    }

    // Disk import is the only reader that accepts historical player layouts.
    public void Import(INetDataReader reader, int revision)
    {
        Life = new PlayerLifeData();
        PersonalKillData = Array.Empty<byte>();
        VegetableCollectionData = Array.Empty<byte>();
        AcceptedMovementState = EMovementState.Walk;
        AcceptedWarping = false;
        AcceptedVelocityU = default;
        AcceptedVelocityL = default;
        LocalStarId = 0;
        if (revision >= 8)
        {
            ImportPlayer(reader, revision);
            return;
        }
        Username = reader.GetString();
        PlayerId = reader.GetUShort();
        LocalPlanetId = reader.GetInt();
        if (revision < 7)
        {
            // MechaColors is obsoleted by MechaAppearance
            var mechaColors = new Float4[reader.GetInt()];
            for (var i = 0; i < mechaColors.Length; i++)
            {
                mechaColors[i] = reader.GetFloat4();
            }
        }
        LocalPlanetPosition = reader.GetFloat3();
        UPosition = reader.GetDouble3();
        Rotation = reader.GetFloat3();
        BodyRotation = reader.GetFloat3();
        MechaData mechaData = new();
        mechaData.Import(reader, revision);
        Mecha = mechaData;
        if (revision >= 5)
        {
            var isAppearancePresent = reader.GetBool();
            if (isAppearancePresent)
            {
                var len = reader.GetInt();
                var data = new byte[len];
                reader.GetBytes(data, len);
                using var ms = new MemoryStream(data);
                using var br = new BinaryReader(ms);
                Appearance = new MechaAppearance();
                Appearance.Init();
                Appearance.Import(br);
            }
        }
        if (revision < 6)
        {
            return;
        }
        {
            var isDIYAppearancePresent = reader.GetBool();
            if (isDIYAppearancePresent)
            {
                var len = reader.GetInt();
                var data = new byte[len];
                reader.GetBytes(data, len);
                using var ms = new MemoryStream(data);
                using var br = new BinaryReader(ms);
                DIYAppearance = new MechaAppearance();
                DIYAppearance.Init();
                DIYAppearance.Import(br);
            }
            var DIYItemLen = reader.GetInt();
            DIYItemId = new int[DIYItemLen];
            DIYItemValue = new int[DIYItemLen];
            for (var i = 0; i < DIYItemLen; i++)
            {
                DIYItemId[i] = reader.GetInt();
                DIYItemValue[i] = reader.GetInt();
            }
        }
    }

    private void ImportPlayer(INetDataReader reader, int revision)
    {
        ReadPosition(reader);
        var mecha = new MechaData();
        mecha.Import(reader, revision);
        Mecha = mecha;
        ReadAppearanceAndDashboard(reader);
        if (revision >= 9) ReadLife(reader);
        else if (Mecha.FightData != null && Mecha.FightData.Hp == 0 && GameMain.mainPlayer != null)
            Mecha.FightData.Hp = GameMain.mainPlayer.mecha.hpMaxApplied;

        // Older saves can omit this section. Flight and star fields are selected by the
        // save revision, never by bytes belonging to the next player in the shared stream.
        if (revision >= 10 && reader.AvailableBytes >= sizeof(int))
        {
            var length = ReadVegetationLength(reader);
            if (reader.AvailableBytes >= length)
            {
                VegetableCollectionData = new byte[length];
                reader.GetBytes(VegetableCollectionData, length);
            }
        }
        if (revision >= 11) ReadFlightState(reader);
        if (revision >= 12) LocalStarId = reader.GetInt();
    }
}
