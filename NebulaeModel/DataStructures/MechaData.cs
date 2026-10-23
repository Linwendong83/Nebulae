#region

using System.IO;
using System.Collections.Generic;
using NebulaeModel.DataStructures;
using NebulaeModel.Contracts;
using NebulaeModel.Networking;
using NebulaeModel.Utils;

#endregion

namespace NebulaeModel.DataStructures;
internal class MechaData : IMechaData
{
    private const int HandMarker = 0x48414E44;
    private const int ResearchMarker = 0x4C414250;
    private const int FuelMarker = 0x4655454C;
    public int ReactorItemId { get; set; }
    public int ReactorItemInc { get; set; }
    public Dictionary<int, int> ResearchPoints { get; } = new();
    public int HandItem { get; set; }
    public int HandCount { get; set; }
    public int HandInc { get; set; }
    public MechaData()
    {
        // This is needed for the serialization and deserialization
        Forge = new MechaForge { tasks = [] };
        TechBonuses = new PlayerTechBonuses();
    }

    public MechaData(Player player)
    {
        SandCount = player.sandCount;
        CoreEnergy = player.mecha.coreEnergy;
        ReactorEnergy = player.mecha.reactorEnergy;
        ReactorItemId = player.mecha.reactorItemId;
        ReactorItemInc = player.mecha.reactorItemInc;
        ReactorStorage = player.mecha.reactorStorage;
        WarpStorage = player.mecha.warpStorage;
        Forge = player.mecha.forge;
        Inventory = player.package;
        DeliveryPackage = player.deliveryPackage;
        HandItem = player.inhandItemId; HandCount = player.inhandItemCount; HandInc = player.inhandItemInc;
        if (HandCount == 0) HandItem = HandInc = 0;
        if (player.mecha.lab?.itemPoints != null)
            foreach (var item in player.mecha.lab.itemPoints.items) ResearchPoints[item.Key] = item.Value;
        ConstructionModule = player.mecha.constructionModule;
        FightData = new MechaFightData(player);
        // Capture the player's live bonuses (speeds, shield, fleets...) so periodic checkpoints
        // and server saves keep them instead of storing an all-zero bonus block.
        TechBonuses = new PlayerTechBonuses(player.mecha);
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public PlayerTechBonuses TechBonuses { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public long SandCount { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public double CoreEnergy { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public double ReactorEnergy { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public StorageComponent Inventory { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public DeliveryPackage DeliveryPackage { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public StorageComponent ReactorStorage { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public StorageComponent WarpStorage { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public MechaForge Forge { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public ConstructionModuleComponent ConstructionModule { get; set; }
    [NebulaeModel.Networking.WireOrder(10)]
    public IMechaFightData FightData { get; set; }

    public void Serialize(INetDataWriter writer)
    {
        TechBonuses.Serialize(writer);
        writer.Put(SandCount);
        writer.Put(CoreEnergy);
        writer.Put(ReactorEnergy);
        writer.Put(ReactorStorage != null);
        if (ReactorStorage == null)
        {
            return;
        }
        FightData.Serialize(writer);
        using var ms = new MemoryStream();
        using (var wr = new BinaryWriter(ms))
        {
            Inventory.Export(wr);
            DeliveryPackage.Export(wr);
            ReactorStorage.Export(wr);
            WarpStorage.Export(wr);
            Forge.Export(wr);
            ConstructionModule.Export(wr);
            wr.Write(HandMarker); wr.Write(HandItem); wr.Write(HandCount); wr.Write(HandInc);
            wr.Write(ResearchMarker); wr.Write(ResearchPoints.Count);
            foreach (var item in ResearchPoints) { wr.Write(item.Key); wr.Write(item.Value); }
            wr.Write(FuelMarker); wr.Write(ReactorItemId); wr.Write(ReactorItemInc);
        }
        var export = ms.ToArray();
        writer.Put(export.Length);
        writer.Put(export);
    }

    public void Deserialize(INetDataReader reader)
    {
        ReactorItemId = ReactorItemInc = 0;
        TechBonuses = new PlayerTechBonuses();
        TechBonuses.Deserialize(reader);
        SandCount = reader.GetLong();
        CoreEnergy = reader.GetDouble();
        ReactorEnergy = reader.GetDouble();
        var isPayloadPresent = reader.GetBool();
        if (!isPayloadPresent)
        {
            ReactorStorage = null;
            ResearchPoints.Clear(); HandItem = HandCount = HandInc = 0;
            return;
        }
        InitializePayload();
        FightData.Deserialize(reader);
        var mechaBytes = reader.GetBytesWithLength();
        using var ms = new MemoryStream(mechaBytes);
        using var br = new BinaryReader(ms);
        BinaryUtils.RequireHeader(br, 2);
        Inventory.Import(br);
        BinaryUtils.RequireHeader(br, 0);
        DeliveryPackage.Import(br);
        BinaryUtils.RequireHeader(br, 2);
        ReactorStorage.Import(br);
        BinaryUtils.RequireHeader(br, 2);
        WarpStorage.Import(br);
        BinaryUtils.RequireHeader(br, 0);
        Forge.Import(br);
        BinaryUtils.RequireHeader(br, 1);
        ConstructionModule.Import(br);
        ReadHand(br);
        ReadResearch(br);
        ReadFuel(br);
    }

    private void ReadHand(BinaryReader reader)
    {
        HandItem = HandCount = HandInc = 0;
        if (reader.BaseStream.Length - reader.BaseStream.Position < 16 || reader.ReadInt32() != HandMarker) throw new InvalidDataException("Invalid personal hand checkpoint.");
        HandItem = reader.ReadInt32(); HandCount = reader.ReadInt32(); HandInc = reader.ReadInt32();
        if (HandItem < 0 || HandCount < 0 || HandInc < 0 || (long)HandInc > (long)HandCount * 10 ||
            (HandCount == 0 && (HandItem != 0 || HandInc != 0)) || (HandCount > 0 && HandItem == 0)) throw new InvalidDataException("Invalid personal hand inventory.");
    }

    private void ReadResearch(BinaryReader reader)
    {
        ResearchPoints.Clear();
        if (reader.BaseStream.Length - reader.BaseStream.Position < 8 || reader.ReadInt32() != ResearchMarker) throw new InvalidDataException("Invalid personal research checkpoint.");
        var entries = reader.ReadInt32();
        if (entries < 0 || entries > 512 || reader.BaseStream.Length - reader.BaseStream.Position < (long)entries * 8) throw new InvalidDataException("Invalid research inventory length.");
        for (var i = 0; i < entries; i++)
        {
            var item = reader.ReadInt32(); var points = reader.ReadInt32();
            if (item <= 0 || points < 0 || ResearchPoints.ContainsKey(item)) throw new InvalidDataException("Invalid research inventory.");
            ResearchPoints.Add(item, points);
        }
    }

    private void ImportCheckpoints(BinaryReader reader)
    {
        HandItem = HandCount = HandInc = 0;
        ResearchPoints.Clear();
        if (reader.BaseStream.Position == reader.BaseStream.Length) return;
        ReadHand(reader);
        if (reader.BaseStream.Position == reader.BaseStream.Length) return;
        ReadResearch(reader);
        if (reader.BaseStream.Position < reader.BaseStream.Length) ReadFuel(reader);
    }

    private void ReadFuel(BinaryReader reader)
    {
        if (reader.BaseStream.Length - reader.BaseStream.Position != 12 || reader.ReadInt32() != FuelMarker)
            throw new InvalidDataException("Invalid reactor checkpoint.");
        ReactorItemId = reader.ReadInt32(); ReactorItemInc = reader.ReadInt32();
        if (ReactorItemId < 0 || ReactorItemInc < 0 || ReactorItemInc > 10 || ReactorItemId == 0 && ReactorItemInc != 0)
            throw new InvalidDataException("Invalid reactor fuel.");
    }

    private StorageComponent HandStorage(int item)
    {
        var storage = new StorageComponent(1);
        storage.grids[0] = new StorageComponent.GRID { itemId = HandCount > 0 ? HandItem : item, count = HandCount, inc = HandInc, stackSize = int.MaxValue };
        return storage;
    }
    public int TakeHand(int item, int count, out int inc)
    {
        inc = 0;
        if (item != HandItem || count <= 0 || count > HandCount) return 0;
        var storage = HandStorage(item);
        storage.TakeItemFromGrid(0, ref item, ref count, out inc);
        HandCount = storage.grids[0].count; HandInc = storage.grids[0].inc; HandItem = HandCount > 0 ? storage.grids[0].itemId : 0;
        return count;
    }
    public bool CreditHand(int item, int count, int inc)
    {
        if (item <= 0 || count <= 0 || inc < 0 || HandCount > 0 && HandItem != item ||
            (long)HandCount + count > int.MaxValue || (long)HandInc + inc > int.MaxValue) return false;
        var storage = HandStorage(item);
        if (storage.AddItemStacked(item, count, inc, out _) != count) return false;
        HandItem = item; HandCount = storage.grids[0].count; HandInc = storage.grids[0].inc; return true;
    }
    public void ApplyHand(Player player)
    {
        player.SetHandItemId_Unsafe(HandItem); player.SetHandItemCount_Unsafe(HandCount); player.SetHandItemInc_Unsafe(HandInc);
    }

    private void InitializePayload()
    {
        ReactorItemId = ReactorItemInc = 0;
        FightData = new MechaFightData();
        Inventory = new StorageComponent(4);
        DeliveryPackage = new DeliveryPackage();
        DeliveryPackage.Init();
        ReactorStorage = new StorageComponent(4);
        WarpStorage = new StorageComponent(1);
        Forge = new MechaForge { tasks = [], extraItems = new ItemBundle() };
        ConstructionModule = new ConstructionModuleComponent();
    }

    public void UpdateMech(Player destination)
    {
        using (var ms = new MemoryStream())
        {
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                Inventory.Export(writer);
            ms.Position = 0;
            using var reader = new BinaryReader(ms);
            destination.package.Import(reader);
            Inventory = destination.package;
        }
        using (var ms = new MemoryStream())
        {
            var bw = new BinaryWriter(ms);
            DeliveryPackage.Export(bw);
            ms.Seek(0, SeekOrigin.Begin);
            var br = new BinaryReader(ms);
            destination.deliveryPackage.Import(br);
            DeliveryPackage = destination.deliveryPackage;
        }
        destination.mecha.coreEnergy = CoreEnergy;
        destination.mecha.reactorEnergy = ReactorEnergy;
        destination.mecha.reactorItemId = ReactorItemId;
        destination.mecha.reactorItemInc = ReactorItemInc;
        destination.mecha.forge = Forge;
        destination.mecha.reactorStorage = ReactorStorage;
        destination.mecha.warpStorage = WarpStorage;
        destination.mecha.constructionModule = ConstructionModule;
        FightData.UpdateMech(destination);
        ApplyHand(destination);
        if (destination.mecha.lab?.itemPoints != null)
        {
            destination.mecha.lab.itemPoints.Clear();
            foreach (var item in ResearchPoints) destination.mecha.lab.itemPoints.Alter(item.Key, item.Value);
        }
        // Restoring a checkpoint is not a collection event or a new contribution to sand statistics.
        destination.SetHiddenProperty(nameof(Player.sandCount), System.Math.Max(0L, SandCount));
    }

    public void Import(INetDataReader reader, int revision)
    {
        ReactorItemId = ReactorItemInc = 0;
        TechBonuses = new PlayerTechBonuses();
        if (revision >= 8) TechBonuses.Deserialize(reader);
        else TechBonuses.Import(reader, revision);
        SandCount = revision >= 8 ? reader.GetLong() : reader.GetInt();
        CoreEnergy = reader.GetDouble();
        ReactorEnergy = reader.GetDouble();
        var isPayloadPresent = reader.GetBool();
        if (!isPayloadPresent)
        {
            ReactorStorage = null;
            ResearchPoints.Clear(); HandItem = HandCount = HandInc = 0;
            return;
        }
        InitializePayload();
        if (revision < 8 && GameMain.mainPlayer != null) FightData.Hp = GameMain.mainPlayer.mecha.hpMaxApplied;
        if (revision >= 8)
        {
            FightData.Deserialize(reader);
        }
        var mechaLength = reader.GetInt();
        var mechaBytes = new byte[mechaLength];
        reader.GetBytes(mechaBytes, mechaLength);
        using var ms = new MemoryStream(mechaBytes);
        using var br = new BinaryReader(ms);
        Inventory.Import(br);
        if (revision >= 7)
        {
            DeliveryPackage.Import(br);
        }
        ReactorStorage.Import(br);
        WarpStorage.Import(br);
        Forge.Import(br);
        if (revision >= 8)
        {
            ConstructionModule.Import(br);
        }
        ImportCheckpoints(br);
    }
}
