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
    public CombatRecoveryView Recovery { get; } = new();
    internal sealed class Reservation
    {
        public PlayerShotId Shot;
        public EAmmoType Ammo;
        public bool Bomb, Local;
        public int Item, Slot;
        public double Core;
        public long Laser, Shield;
        public long SettledRevision;
        public long Tick;
        public bool? Recharging;
    }
    public int PendingCount => pending.Count;
    public bool Contains(PlayerShotId shot) => pending.ContainsKey(shot);
    public void Reserve(PlayerShotId shot, EAmmoType ammo, bool bomb, bool local, int item, int slot = 0)
    {
        pending[shot] = new Reservation { Shot = shot, Ammo = ammo, Bomb = bomb, Local = local, Item = item, Slot = slot, Tick = GameMain.gameTick };
    }
    public void Reject(PlayerShotId shot)
    {
        if (!pending.TryGetValue(shot, out var r)) return;
        if (localMecha != null) Recovery.Refund(localMecha, r.Laser, r.Shield, r.Core);
        pending.Remove(shot);
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
        foreach (var key in pending.Where(x => x.Value.SettledRevision > 0 && x.Value.SettledRevision <= appliedRevision).Select(x => x.Key).ToArray()) pending.Remove(key);
    }
    public void Clear() { pending.Clear(); appliedRevision = 0; Recovery.Clear(); localMecha = null; localHostTick = null; }
    public void Rebase(Mecha mecha, long? hostTick = null)
    {
        var own = mecha == GameMain.mainPlayer?.mecha;
        if (own && hostTick.HasValue) localHostTick = hostTick;
        Recovery.Rebase(mecha, hostTick, own ? pending.Values.Select(r =>
            new CombatRecoveryView.Expenditure(r.Tick, r.Laser, r.Shield, r.Core, r.Recharging)) : null);
    }
    public Scope Enter(Mecha mecha) => new(this, mecha);

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
                foreach (var reservation in owner.pending.Values.OrderBy(x => x.Shot.Sequence)) Replay(mecha, reservation);
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
                mecha.ammoStorage = ammo; mecha.bombStorage = bombs; mecha.player.SetHiddenProperty(nameof(global::Player.package), package);
                mecha.ammoBulletCount = bullets; mecha.ammoItemId = item; mecha.ammoInc = inc; mecha.ammoSelectSlot = selected;
                mecha.laserEnergy = laser; mecha.energyShieldEnergy = shield; mecha.coreEnergy = core; mecha.laserRecharging = charging;
                if (ready) owner.Recovery.Spend(mecha, created.Length > 0 ? spentLaser : 0, created.Length > 0 ? spentShield : 0,
                    created.Length > 0 ? spentCore : 0,
                    recharging != predictedCharging || created.Any(r => r.Ammo == EAmmoType.Laser) ? recharging : null);
            }
            finally { Active = false; }
        }
    }
}
