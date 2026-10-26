using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NebulaeModel.Packets.Combat;
using NebulaeModel.Utils;

namespace NebulaeWorld.Combat;

/// <summary>Native weapon code operates on disposable inventory copies, never checkpoint inventory.</summary>
internal sealed class PlayerWeaponResources
{
    [ThreadStatic] public static bool Active;
    private readonly Dictionary<PlayerShotId, Reservation> pending = new();
    private long appliedRevision;
    private Mecha localMecha;
    private long? localHostTick;
    private int version, displayVersion = -1, displaySlot;
    private long displayLife;
    private Magazine displayMagazine, displayBaseline;
    public CombatRecoveryView Recovery { get; } = new();
    internal readonly struct Magazine
    {
        public Magazine(Mecha mecha) { Item = mecha.ammoItemId; Bullets = mecha.ammoBulletCount; Inc = mecha.ammoInc; }
        public int Item { get; }
        public int Bullets { get; }
        public int Inc { get; }
        public bool Same(Magazine other) => Item == other.Item && Bullets == other.Bullets && Inc == other.Inc;
    }
    internal sealed class Reservation
    {
        public PlayerShotId Shot;
        public EAmmoType Ammo;
        public bool Bomb;
        public int Item, Slot;
        public double Core;
        public long Laser, Shield;
        public long SettledRevision;
        public long Tick;
        public long LifeVersion;
        public bool? Recharging;
    }
    public int PendingCount => pending.Count;
    public bool Contains(PlayerShotId shot) => pending.ContainsKey(shot);
    public void Reserve(PlayerShotId shot, EAmmoType ammo, bool bomb, int item, int slot = 0)
    {
        var mecha = localMecha ?? GameMain.mainPlayer?.mecha;
        pending[shot] = new Reservation
        {
            Shot = shot,
            Ammo = ammo,
            Bomb = bomb,
            Item = item,
            Slot = slot,
            Tick = GameMain.gameTick,
            LifeVersion = mecha == null ? 0 : Recovery.GetLifeVersion(mecha)
        };
        version++;
    }
    public void Reject(PlayerShotId shot)
    {
        if (!pending.TryGetValue(shot, out var r)) return;
        if (localMecha != null && r.LifeVersion == Recovery.GetLifeVersion(localMecha)) Recovery.Refund(localMecha, r.Laser, r.Shield, r.Core);
        pending.Remove(shot);
        version++;
    }
    public void Settle(PlayerShotId shot, long revision)
    {
        if (pending.TryGetValue(shot, out var item)) item.SettledRevision = revision;
        var count = pending.Count;
        Applied(appliedRevision);
        if (pending.Count != count && localMecha != null) Rebase(localMecha, localHostTick);
    }
    public void Applied(long revision)
    {
        appliedRevision = Math.Max(appliedRevision, revision);
        var before = pending.Count;
        foreach (var key in pending.Where(x => x.Value.SettledRevision > 0 && x.Value.SettledRevision <= appliedRevision).Select(x => x.Key).ToArray()) pending.Remove(key);
        if (pending.Count != before) version++;
    }
    public void Clear() { pending.Clear(); appliedRevision = 0; Recovery.Clear(); localMecha = null; localHostTick = null; version++; }
    public void Rebase(Mecha mecha, long? hostTick = null)
    {
        version++;
        var own = mecha == GameMain.mainPlayer?.mecha;
        if (own && hostTick.HasValue) localHostTick = hostTick;
        var life = Recovery.GetLifeVersion(mecha);
        // Keep old reservations until their settlement arrives, but their costs and recharge
        // locks must not be replayed into a respawned mecha.
        Recovery.Rebase(mecha, hostTick, own ? pending.Values.Where(r => r.LifeVersion == life).Select(r =>
            new CombatRecoveryView.Expenditure(r.Tick, r.Laser, r.Shield, r.Core, r.Recharging)) : null);
    }
    public Scope Enter(Mecha mecha) => new(this, mecha);

    internal Magazine DisplayMagazine(Mecha mecha)
    {
        if (Active || !ReferenceEquals(mecha, localMecha) || pending.Count == 0) return new Magazine(mecha);
        if (displayVersion == version && displaySlot == mecha.ammoSelectSlot && displayLife == Recovery.GetLifeVersion(mecha) &&
            displayBaseline.Same(new Magazine(mecha))) return displayMagazine;
        // Reuse native replay after rejection or a new checkpoint; Dispose restores every real resource.
        using (Enter(mecha)) { }
        return displayMagazine;
    }

    private static Magazine ForDisplay(Mecha mecha) => mecha != null && Multiplayer.IsActive && Multiplayer.Session.IsClient &&
        Multiplayer.Session.Authority.IsActive && !Active
            ? Multiplayer.Session.PlayerWeapons.Resources.DisplayMagazine(mecha) : new Magazine(mecha);
    public static int DisplayAmmoItem(Mecha mecha) => ForDisplay(mecha).Item;
    public static int DisplayAmmoBullets(Mecha mecha) => ForDisplay(mecha).Bullets;
    public static int DisplayAmmoInc(Mecha mecha) => ForDisplay(mecha).Inc;

    // Match PlayerAction_Combat.AmmoFireProcedure: the refill belongs to this shot,
    // before either a host checkpoint or the next prediction observes the magazine.
    internal static void ConsumeRound(Mecha mecha)
    {
        mecha.ammoBulletCount = Math.Max(0, mecha.ammoBulletCount - 1);
        if (mecha.ammoBulletCount == 0) mecha.LoadAmmo();
    }

    private static StorageComponent Copy(StorageComponent stock)
    {
        if (stock == null) return null;
        using var s = new MemoryStream(); using var w = new BinaryWriter(s);
        stock.Export(w); s.Position = 0;
        var result = new StorageComponent(stock.size); using var r = new BinaryReader(s); result.Import(r); return result;
    }

    public sealed class Scope : IDisposable
    {
        private readonly PlayerWeaponResources owner;
        private readonly Mecha mecha;
        private readonly StorageComponent ammo, bombs, package;
        private readonly int bullets, item, inc, selected;
        private readonly long laser, shield;
        private readonly double core;
        private readonly bool charging;
        private readonly HashSet<PlayerShotId> previous;
        private double predictedCore;
        private long predictedLaser, predictedShield;
        private bool ready;
        private bool predictedCharging;
        public Scope(PlayerWeaponResources owner, Mecha mecha)
        {
            this.owner = owner; this.mecha = mecha;
            owner.localMecha = mecha;
            ammo = mecha.ammoStorage; bombs = mecha.bombStorage; package = mecha.player.package;
            bullets = mecha.ammoBulletCount; item = mecha.ammoItemId; inc = mecha.ammoInc; selected = mecha.ammoSelectSlot;
            laser = mecha.laserEnergy; shield = mecha.energyShieldEnergy; core = mecha.coreEnergy; charging = mecha.laserRecharging;
            previous = new HashSet<PlayerShotId>(owner.pending.Keys);
            Active = true;
            try
            {
                mecha.ammoStorage = Copy(ammo); mecha.bombStorage = Copy(bombs); mecha.player.SetHiddenProperty(nameof(global::Player.package), Copy(package));
                var view = owner.Recovery.Get(mecha);
                mecha.laserEnergy = view.laserEnergy; mecha.energyShieldEnergy = view.energyShieldEnergy;
                mecha.coreEnergy = view.coreEnergy; mecha.laserRecharging = view.laserRecharging;
                var life = owner.Recovery.GetLifeVersion(mecha);
                foreach (var reservation in owner.pending.Values.Where(x => x.LifeVersion == life).OrderBy(x => x.Shot.Sequence)) Replay(mecha, reservation);
                // Historical slots belong to those shots. The callback must see the pilot's latest choice.
                mecha.ammoSelectSlot = selected;
                predictedCore = mecha.coreEnergy;
                predictedLaser = mecha.laserEnergy; predictedShield = mecha.energyShieldEnergy;
                predictedCharging = mecha.laserRecharging;
                ready = true;
            }
            catch { Dispose(); throw; }
        }
        private static void Replay(Mecha mecha, Reservation r)
        {
            if (r.Bomb)
            {
                if (mecha.bombStorage.grids[0].count == 0 && mecha.autoReplenishAmmo) mecha.AutoReplenishBomb();
                var itemId = r.Item; var count = 1; mecha.bombStorage.TakeItemFromGrid(0, ref itemId, ref count, out _);
            }
            else if (r.Ammo != EAmmoType.Laser && r.Ammo != EAmmoType.None)
            {
                if (r.Slot > 0) mecha.ammoSelectSlot = r.Slot;
                if (mecha.ammoBulletCount == 0 || r.Item > 0 && mecha.ammoItemId != r.Item) mecha.LoadAmmo();
                ConsumeRound(mecha);
            }
        }
        public void Dispose()
        {
            try
            {
                var created = owner.pending.Values.Where(x => !previous.Contains(x.Shot)).ToArray();
                if (created.Length > 0)
                    foreach (var r in created)
                    {
                        r.Core = Math.Max(0, predictedCore - mecha.coreEnergy) / created.Length;
                        r.Laser = Math.Max(0, predictedLaser - mecha.laserEnergy) / created.Length;
                        r.Shield = Math.Max(0, predictedShield - mecha.energyShieldEnergy) / created.Length;
                        r.Recharging = r.Ammo == EAmmoType.Laser ? mecha.laserRecharging : null;
                    }
                var spentLaser = predictedLaser - mecha.laserEnergy; var spentShield = predictedShield - mecha.energyShieldEnergy;
                var spentCore = predictedCore - mecha.coreEnergy; var recharging = mecha.laserRecharging;
                var magazine = new Magazine(mecha);
                var nextSelected = ready ? mecha.ammoSelectSlot : selected;
                mecha.ammoStorage = ammo; mecha.bombStorage = bombs; mecha.player.SetHiddenProperty(nameof(global::Player.package), package);
                mecha.ammoBulletCount = bullets; mecha.ammoItemId = item; mecha.ammoInc = inc; mecha.ammoSelectSlot = nextSelected;
                mecha.laserEnergy = laser; mecha.energyShieldEnergy = shield; mecha.coreEnergy = core; mecha.laserRecharging = charging;
                if (ready) owner.Recovery.Spend(mecha, created.Length > 0 ? spentLaser : 0, created.Length > 0 ? spentShield : 0,
                    created.Length > 0 ? spentCore : 0,
                    recharging != predictedCharging || created.Any(r => r.Ammo == EAmmoType.Laser) ? recharging : null);
                if (ready)
                {
                    owner.displayMagazine = magazine; owner.displayBaseline = new Magazine(mecha);
                    owner.displaySlot = nextSelected; owner.displayLife = owner.Recovery.GetLifeVersion(mecha); owner.displayVersion = owner.version;
                }
            }
            finally { Active = false; }
        }
    }
}
