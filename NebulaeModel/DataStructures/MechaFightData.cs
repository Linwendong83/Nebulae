#region
using System;
using System.IO;
using NebulaeModel.Contracts;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Authority;
#endregion

namespace NebulaeModel.DataStructures
{
    internal class MechaFightData : IMechaFightData
    {
        private const int WeaponSettingsMarker = 0x57504E31;
        public MechaFightData()
        {
            // This is needed for the serialization and deserialization
            AmmoStorage = new StorageComponent(3);
            BombStorage = new StorageComponent(1);
            FighterStorage = new StorageComponent(5);
            GroundCombatModule = new CombatModuleComponent();
            SpaceCombatModule = new CombatModuleComponent();

            GroundCombatModule.Reset();
            SpaceCombatModule.Reset();
            GroundCombatModule.Init(GameMain.data);
            SpaceCombatModule.Init(GameMain.data);
            GroundCombatModule.Setup(1, GameMain.data);
            SpaceCombatModule.Setup(3, GameMain.data);
        }
        public MechaFightData(Player player)
        {
            AutoReplenishFuel = player.mecha.autoReplenishFuel;
            AutoReplenishAmmo = player.mecha.autoReplenishAmmo;
            AutoReplenishHangar = player.mecha.autoReplenishHangar;
            Hp = player.mecha.hp;
            EnergyShieldEnergy = player.mecha.energyShieldEnergy;
            AmmoItemId = player.mecha.ammoItemId;
            AmmoInc = player.mecha.ammoInc;
            AmmoBulletCount = player.mecha.ammoBulletCount;
            AmmoSelectSlot = player.mecha.ammoSelectSlot;
            AmmoSelectSlotState = player.mecha.ammoSelectSlotState;
            LaserActiveState = player.mecha.laserActiveState;
            BombActive = player.mecha.bombActive;
            AmmoMuzzleFire = player.mecha.ammoMuzzleFire;
            AmmoRoundFire = player.mecha.ammoRoundFire;
            AmmoMuzzleIndex = player.mecha.ammoMuzzleIndex;
            LaserActive = player.mecha.laserActive;
            LaserRecharging = player.mecha.laserRecharging;
            LaserEnergy = player.mecha.laserEnergy;
            LaserFire = player.mecha.laserFire;
            BombFire = player.mecha.bombFire;
            AmmoStorage = player.mecha.ammoStorage;
            BombStorage = player.mecha.bombStorage;
            AmmoHatredTarget = player.mecha.ammoHatredTarget;
            LaserHatredTarget = player.mecha.laserHatredTarget;
            FighterStorage = player.mecha.fighterStorage;
            GroundCombatModule = player.mecha.groundCombatModule;
            SpaceCombatModule = player.mecha.spaceCombatModule;
        }
        public bool AutoReplenishFuel { get; set; }
        public bool AutoReplenishAmmo { get; set; }
        public bool AutoReplenishHangar { get; set; }
        public int Hp { get; set; }
        public long EnergyShieldEnergy { get; set; }
        public int AmmoItemId { get; set; }
        public int AmmoInc { get; set; }
        public int AmmoBulletCount { get; set; }
        public int AmmoSelectSlot { get; set; }
        public int AmmoSelectSlotState { get; set; }
        public int LaserActiveState { get; set; }
        public bool BombActive { get; set; }
        public int AmmoMuzzleFire { get; set; }
        public int AmmoRoundFire { get; set; }
        public int AmmoMuzzleIndex { get; set; }
        public bool LaserActive { get; set; }
        public bool LaserRecharging { get; set; }
        public long LaserEnergy { get; set; }
        public int LaserFire { get; set; }
        public int BombFire { get; set; }
        public StorageComponent AmmoStorage { get; set; }
        public StorageComponent BombStorage { get; set; }
        public EnemyHatredTarget AmmoHatredTarget { get; set; }
        public EnemyHatredTarget LaserHatredTarget { get; set; }
        public StorageComponent FighterStorage { get; set; }
        public CombatModuleComponent GroundCombatModule { get; set; }
        public CombatModuleComponent SpaceCombatModule { get; set; }
        /// <summary>
        /// Reusable export buffer, one per thread.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Serializing the same fight data more than once is the common case, not the exception: the
        /// host rebuilds a player's <see cref="MechaFightData"/> every tick and compares the encoded
        /// bytes every few ticks, and most of those comparisons end in "nothing changed" — the whole
        /// point of a reconciliation channel. Allocating a fresh stream, writer and copy for each of
        /// those throws the work away with the packet, so the buffers are reused instead.
        /// </para>
        /// <para>
        /// The buffer is per-thread because serialization also runs on connection threads, and it is
        /// reset rather than recreated, which is what keeps a long session from growing anything. The
        /// bytes written are a pure function of this object's fields, so reuse cannot change them.
        /// </para>
        /// </remarks>
        [ThreadStatic]
        private static MemoryStream exportStream;

        [ThreadStatic]
        private static BinaryWriter exportWriter;

        public void Serialize(INetDataWriter writer)
        {
            writer.Put(AutoReplenishFuel);
            writer.Put(AutoReplenishAmmo);
            writer.Put(AutoReplenishHangar);
            writer.Put(Hp);
            writer.Put(EnergyShieldEnergy);
            writer.Put(AmmoItemId);
            writer.Put(AmmoInc);
            writer.Put(AmmoBulletCount);
            writer.Put(AmmoSelectSlot);
            writer.Put(AmmoMuzzleFire);
            writer.Put(AmmoRoundFire);
            writer.Put(AmmoMuzzleIndex);
            writer.Put(LaserActive);
            writer.Put(LaserRecharging);
            writer.Put(LaserEnergy);
            writer.Put(LaserFire);
            writer.Put(BombFire);

            var ms = exportStream ??= new MemoryStream();
            var wr = exportWriter ??= new BinaryWriter(ms);
            // Rewind rather than replace: the previous call's bytes are simply overwritten.
            ms.SetLength(0);
            ms.Position = 0;
            AmmoStorage.Export(wr);
            BombStorage.Export(wr);
            AmmoHatredTarget.Export(wr);
            LaserHatredTarget.Export(wr);
            FighterStorage.Export(wr);
            GroundCombatModule.Export(wr);
            SpaceCombatModule.Export(wr);
            wr.Write(WeaponSettingsMarker);
            wr.Write(CombatWeaponSettings.Encode(0, AmmoSelectSlotState, LaserActiveState, BombActive));
            // The stream's own buffer is the image; copying it into a second array just to hand the
            // writer a byte[] would allocate exactly what the reuse above avoids.
            var buffer = ms.GetBuffer();
            var length = (int)ms.Length;
            writer.Put(length);
            writer.Put(buffer, 0, length);
        }
        public void Deserialize(INetDataReader reader)
        {
            AmmoStorage = new StorageComponent(3);
            BombStorage = new StorageComponent(1);
            AmmoHatredTarget = default(EnemyHatredTarget);
            LaserHatredTarget = default(EnemyHatredTarget);
            FighterStorage = new StorageComponent(5);
            GroundCombatModule = new CombatModuleComponent();
            SpaceCombatModule = new CombatModuleComponent();
            GroundCombatModule.Init(GameMain.data);
            SpaceCombatModule.Init(GameMain.data);

            AutoReplenishFuel = reader.GetBool();
            AutoReplenishAmmo = reader.GetBool();
            AutoReplenishHangar = reader.GetBool();
            Hp = reader.GetInt();
            EnergyShieldEnergy = reader.GetLong();
            AmmoItemId = reader.GetInt();
            AmmoInc = reader.GetInt();
            AmmoBulletCount = reader.GetInt();
            AmmoSelectSlot = reader.GetInt();
            AmmoMuzzleFire = reader.GetInt();
            AmmoRoundFire = reader.GetInt();
            AmmoMuzzleIndex = reader.GetInt();
            LaserActive = reader.GetBool();
            LaserRecharging = reader.GetBool();
            LaserEnergy = reader.GetLong();
            LaserFire = reader.GetInt();
            BombFire = reader.GetInt();

            var fightBytes = reader.GetBytesWithLength();
            using var ms = new MemoryStream(fightBytes);
            using var br = new BinaryReader(ms);

            BinaryUtils.RequireHeader(br, 2);
            AmmoStorage.Import(br);
            BinaryUtils.RequireHeader(br, 2);
            BombStorage.Import(br);
            AmmoHatredTarget.Import(br);
            LaserHatredTarget.Import(br);
            BinaryUtils.RequireHeader(br, 2);
            FighterStorage.Import(br);
            BinaryUtils.RequireHeader(br, 3);
            GroundCombatModule.Import(br);
            BinaryUtils.RequireHeader(br, 3);
            SpaceCombatModule.Import(br);
            AmmoSelectSlotState = LaserActiveState = 0;
            BombActive = false;
            // Older checkpoints end at the space module. Keep their original layout readable.
            if (ms.Position < ms.Length)
            {
                if (ms.Length - ms.Position != 8 || br.ReadInt32() != WeaponSettingsMarker)
                    throw new ParseException("Invalid mecha weapon settings");
                var flags = br.ReadInt32();
                if ((flags & ~CombatWeaponSettings.Mask) != 0 ||
                    !CombatWeaponSettings.TryDecode(flags, out var ammoMode, out var laserMode, out var bomb))
                    throw new ParseException("Invalid mecha weapon modes");
                AmmoSelectSlotState = ammoMode; LaserActiveState = laserMode; BombActive = bomb;
            }
            if (ms.Position != ms.Length) throw new ParseException("Trailing mecha combat data");
        }
        public void UpdateMech(Player destination)
        {
            destination.mecha.autoReplenishFuel = AutoReplenishFuel;
            destination.mecha.autoReplenishAmmo = AutoReplenishAmmo;
            destination.mecha.autoReplenishHangar = AutoReplenishHangar;
            destination.mecha.hp = Hp;
            destination.mecha.energyShieldEnergy = EnergyShieldEnergy;
            destination.mecha.ammoItemId = AmmoItemId;
            destination.mecha.ammoInc = AmmoInc;
            destination.mecha.ammoBulletCount = AmmoBulletCount;
            destination.mecha.ammoSelectSlot = AmmoSelectSlot;
            destination.mecha.ammoSelectSlotState = AmmoSelectSlotState;
            destination.mecha.laserActiveState = LaserActiveState;
            destination.mecha.bombActive = BombActive;
            destination.mecha.ammoMuzzleFire = AmmoMuzzleFire;
            destination.mecha.ammoRoundFire = AmmoRoundFire;
            destination.mecha.ammoMuzzleIndex = AmmoMuzzleIndex;
            destination.mecha.laserActive = LaserActive;
            destination.mecha.laserRecharging = LaserRecharging;
            destination.mecha.laserEnergy = LaserEnergy;
            destination.mecha.laserFire = LaserFire;
            destination.mecha.bombFire = BombFire;
            destination.mecha.ammoStorage = UpdateStorage(destination.mecha.ammoStorage, AmmoStorage);
            destination.mecha.bombStorage = UpdateStorage(destination.mecha.bombStorage, BombStorage);
            destination.mecha.ammoHatredTarget = AmmoHatredTarget;
            destination.mecha.laserHatredTarget = LaserHatredTarget;
            destination.mecha.fighterStorage = UpdateStorage(destination.mecha.fighterStorage, FighterStorage);
            destination.mecha.groundCombatModule = GroundCombatModule;
            destination.mecha.spaceCombatModule = SpaceCombatModule;
            // Disk checkpoints may have been read while the menu world still existed.
            // Init-time sector/history references belong to that world and can already be freed.
            if (GameMain.data != null)
            {
                GroundCombatModule?.AfterImport(GameMain.data);
                SpaceCombatModule?.AfterImport(GameMain.data);
            }
        }

        public static StorageComponent UpdateStorage(StorageComponent destination, StorageComponent source)
        {
            if (destination == null || ReferenceEquals(destination, source)) return source;
            // UIStorageGrid binds these objects when the mecha window opens. Replacing
            // them leaves subsequent deposits in an orphaned storage after the package pays.
            destination.SetSize(source.size);
            destination.type = source.type;
            destination.bans = source.bans;
            Array.Copy(source.grids, destination.grids, source.grids.Length);
            destination.ResetOptimizationFlags();
            // Facts need a UI refresh, but must not publish another personal transfer.
            destination.changed = true;
            return destination;
        }
    }
}
