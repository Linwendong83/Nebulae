#region

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Universe;
using UnityEngine;

#endregion

namespace NebulaeWorld.Universe;

internal class LaunchManager
{
    private HashSet<int> planetIds = []; //Client side
    private readonly Dictionary<int, long> baselines = [];

    public ConcurrentBag<DysonLaunchData.Projectile> ProjectileBag { get; set; } = [];
    public ConcurrentDictionary<int, DysonLaunchData> Snapshots { get; set; } = new();

    public void Dispose()
    {
        ProjectileBag = null;
        Snapshots = null;
        planetIds = null;
        baselines.Clear();
    }

    public void Register(int starIndex)
    {
        if (Snapshots.IsEmpty)
        {
            // Clear remaining projectile data in bag
            while (ProjectileBag.TryTake(out _)) { }
        }
        Snapshots.TryAdd(starIndex, new DysonLaunchData(starIndex));
    }

    public void Unregister(int starIndex)
    {
        Snapshots.TryRemove(starIndex, out _);
        baselines.Remove(starIndex);
    }

    public void SetBaseline(int starIndex, long tick)
    {
        baselines[starIndex] = tick;
        if (!Snapshots.TryGetValue(starIndex, out var pending)) return;
        pending.BulletList = pending.BulletList.Skip(pending.BulletCursor).Where(p => p.Tick > tick).ToList();
        pending.RocketList = pending.RocketList.Skip(pending.RocketCursor).Where(p => p.Tick > tick).ToList();
        pending.BulletCursor = pending.RocketCursor = 0;
    }

    public void CollectProjectile()
    {
        if (Snapshots.IsEmpty)
        {
            return;
        }
        // Try to take out all projectiles produced in this tick
        while (ProjectileBag.TryTake(out var data))
        {
            var starId = data.PlanetId / 100 - 1;
            if (!Snapshots.TryGetValue(starId, out var snapshot))
            {
                // If the dyson sphere has no subscribers anymore, skip this data
                continue;
            }
            if (data.LayerId == 0)
            {
                data.Tick = GameMain.gameTick;
                snapshot.BulletList.Add(data);
            }
            else
            {
                data.Tick = GameMain.gameTick;
                snapshot.RocketList.Add(data);
            }
        }

    }

    public void SendBroadcastIfNeeded()
    {
        if (Snapshots.IsEmpty)
        {
            return;
        }

        //Broadcast the update packet to subscribers
        foreach (var snapshot in Snapshots.Values)
        {
            var count = Math.Max(snapshot.BulletList.Count, snapshot.RocketList.Count);
            for (var offset = 0; offset < count; offset += DysonLaunchData.MaxProjectiles)
            {
                var batch = new DysonLaunchData(snapshot.StarIndex)
                {
                    BulletList = snapshot.BulletList.Skip(offset).Take(DysonLaunchData.MaxProjectiles).ToList(),
                    RocketList = snapshot.RocketList.Skip(offset).Take(DysonLaunchData.MaxProjectiles).ToList()
                };
                Multiplayer.Session.DysonSpheres.SendPacketToDysonSphere(new DysonLaunchDataPacket(batch),
                    snapshot.StarIndex);
            }
        }

        // Clear snapshots
        foreach (var snapshot in Snapshots.Values)
        {
            snapshot.BulletList.Clear();
            snapshot.RocketList.Clear();
        }
    }

    public void ImportPacket(DysonLaunchDataPacket packet)
    {
        var incoming = packet.Data;
        if (incoming == null || incoming.StarIndex < 0 || incoming.StarIndex >= GameMain.data.dysonSpheres.Length ||
            !baselines.TryGetValue(incoming.StarIndex, out var baseline)) return;
        var pending = Snapshots.GetOrAdd(incoming.StarIndex, i => new DysonLaunchData(i));
        pending.BulletList = pending.BulletList.Skip(pending.BulletCursor).Concat(incoming.BulletList.Where(p => p.Tick > baseline)).ToList();
        pending.RocketList = pending.RocketList.Skip(pending.RocketCursor).Concat(incoming.RocketList.Where(p => p.Tick > baseline)).ToList();
        pending.BulletCursor = pending.RocketCursor = 0;
    }

    public void LaunchProjectile()
    {
        if (Snapshots.IsEmpty) return;
        var removals = new List<int>();
        // Membership can change between two network packets.
        planetIds.Clear();
        for (var i = 0; i < GameMain.data.factoryCount; i++)
            if (GameMain.data.factories[i] != null) planetIds.Add(GameMain.data.factories[i].planetId);
        foreach (var snapshot in Snapshots.Values)
        {
            var sphere = GameMain.data.dysonSpheres[snapshot.StarIndex];
            if (sphere == null)
            {
                removals.Add(snapshot.StarIndex);
                continue;
            }
            while (snapshot.BulletCursor < snapshot.BulletList.Count)
            {
                if (snapshot.BulletList[snapshot.BulletCursor].Tick <= GameMain.gameTick)
                {
                    // Only fire when the planet factory isn't loaded 
                    if (!planetIds.Contains(snapshot.BulletList[snapshot.BulletCursor].PlanetId))
                    {
                        AddBullet(sphere.swarm, snapshot.BulletList[snapshot.BulletCursor]);
                    }
                    snapshot.BulletCursor++;
                }
                else
                {
                    break;
                }
            }
            while (snapshot.RocketCursor < snapshot.RocketList.Count)
            {
                if (snapshot.RocketList[snapshot.RocketCursor].Tick <= GameMain.gameTick)
                {
                    // Only fire when the planet factory isn't loaded
                    if (!planetIds.Contains(snapshot.RocketList[snapshot.RocketCursor].PlanetId))
                    {
                        AddRocket(sphere, snapshot.RocketList[snapshot.RocketCursor]);
                    }
                    snapshot.RocketCursor++;
                }
                else
                {
                    break;
                }
            }
            if (snapshot.BulletCursor == snapshot.BulletList.Count && snapshot.RocketCursor == snapshot.RocketList.Count)
            {
                // snapshot is empty, can be removed now
                removals.Add(snapshot.StarIndex);
            }
        }
        foreach (var star in removals) Snapshots.TryRemove(star, out _);
    }

    private static void AddBullet(DysonSwarm swarm, DysonLaunchData.Projectile projectile)
    {
        ref var astroPoses = ref GameMain.data.galaxy.astrosData;
        int orbitId = projectile.TargetId;
        if (!swarm.OrbitExist(orbitId))
        {
            return;
        }
        var starPos = astroPoses[projectile.PlanetId / 100 * 100].uPos;
        SailBullet bullet = default;
        bullet.lBegin = projectile.LocalPos;
        bullet.uBegin = astroPoses[projectile.PlanetId].uPos +
                        Maths.QRotateLF(astroPoses[projectile.PlanetId].uRot, projectile.LocalPos);
        bullet.uEnd = starPos + VectorLF3.Cross(swarm.orbits[orbitId].up, starPos - bullet.uBegin).normalized *
            swarm.orbits[orbitId].radius;
        bullet.maxt = (float)((bullet.uEnd - bullet.uBegin).magnitude / 4000.0);
        bullet.t = (float)Math.Max(0, GameMain.gameTick - projectile.Tick) / 60f;
        bullet.uEndVel = VectorLF3.Cross(bullet.uEnd - starPos, swarm.orbits[orbitId].up).normalized *
                         Math.Sqrt(swarm.dysonSphere.gravity / swarm.orbits[orbitId].radius);
        swarm.AddBullet(bullet, orbitId);
    }

    private static void AddRocket(DysonSphere sphere, DysonLaunchData.Projectile projectile)
    {
        ref var astroPoses = ref GameMain.data.galaxy.astrosData;
        var layerId = projectile.LayerId;
        var nodeId = projectile.TargetId;
        var node = sphere.FindNode(layerId, nodeId);
        if (node == null)
        {
            return;
        }
        DysonRocket rocket = default;
        rocket.planetId = projectile.PlanetId;
        rocket.uPos = astroPoses[projectile.PlanetId].uPos + Maths.QRotateLF(astroPoses[projectile.PlanetId].uRot,
            projectile.LocalPos + projectile.LocalPos.normalized * 6.1f);
        rocket.uRot = astroPoses[projectile.PlanetId].uRot * Maths.SphericalRotation(projectile.LocalPos, 0f) *
                      Quaternion.Euler(-90f, 0f, 0f);
        rocket.uVel = rocket.uRot * Vector3.forward;
        rocket.uSpeed = 0f;
        rocket.launch = projectile.LocalPos.normalized;
        sphere.AddDysonRocket(rocket, node);
    }
}
