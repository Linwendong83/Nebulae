#region

using System;
using System.Collections.Generic;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Factory.Belt;
#endregion

namespace NebulaeWorld.Factory;

internal class BeltManager : IDisposable
{
    private const int MAX_PUTDOWN_WAIT_TICK = 30;
    private List<BeltUpdate> beltPickupUpdates = [];
    private List<BeltUpdatePutItemOnPacket> beltPutdownPackets = [];
    private int putDownTimer = 0;

    // Sending side of the put-down path. Kept apart from beltPutdownPackets, which is the receiving
    // side's retry queue: one defers outgoing packets until the frame is over, the other retries an
    // incoming packet the local belt could not take yet.
    private readonly List<BeltUpdatePutItemOnPacket> pendingPutdowns = [];
    private int pendingPutdownPlanet = -1;

    public void Dispose()
    {
        beltPickupUpdates = null;
        beltPutdownPackets = null;
        GC.SuppressFinalize(this);
    }

    public void BeltPickupStarted()
    {
        beltPickupUpdates.Clear();
    }

    public void RegisterBeltPickupUpdate(int itemId, int count, int beltId)
    {
        if (Multiplayer.IsActive)
        {
            beltPickupUpdates.Add(new BeltUpdate(itemId, count, beltId));
        }
    }

    public void BeltPickupEnded()
    {
        if (GameMain.data.localPlanet != null)
        {
            Multiplayer.Session.Network.SendPacketToLocalStar(
                new BeltUpdatePickupItemsPacket(beltPickupUpdates.ToArray(), GameMain.data.localPlanet.id));
        }

        beltPickupUpdates.Clear();
    }

    public static bool TryPutItemOnBelt(BeltUpdatePutItemOnPacket packet)
    {
        using (Multiplayer.Session.Factories.IsIncomingRequest.On())
        {
            var cargoTraffic = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.cargoTraffic;
            bool ret;
            if (cargoTraffic == null)
            {
                // Ignore events on factories not loaded yet
                return true;
            }
            if (packet.ItemCount == 1)
            {
                ret = cargoTraffic.PutItemOnBelt(packet.BeltId, packet.ItemId, packet.ItemInc);
                return ret;
            }
            if (cargoTraffic.beltPool[packet.BeltId].id == 0 || cargoTraffic.beltPool[packet.BeltId].id != packet.BeltId)
            {
                return false;
            }
            var index = cargoTraffic.beltPool[packet.BeltId].segIndex + cargoTraffic.beltPool[packet.BeltId].segPivotOffset;
            ret = cargoTraffic.GetCargoPath(cargoTraffic.beltPool[packet.BeltId].segPathId)
                .TryInsertItem(index, packet.ItemId, packet.ItemCount, packet.ItemInc);
            return ret;
        }
    }

    public void RegiserbeltPutdownPacket(BeltUpdatePutItemOnPacket packet)
    {
        beltPutdownPackets.Add(packet);
    }

    /// <summary>
    ///     Record one item placed on a belt, to be reported with the rest of the frame's insertions.
    /// </summary>
    /// <remarks>
    ///     All insertions of a frame belong to one planet, so the planet is taken from the first entry.
    /// </remarks>
    public void RegisterBeltPutdownUpdate(BeltUpdatePutItemOnPacket packet)
    {
        if (pendingPutdowns.Count == 0)
        {
            pendingPutdownPlanet = packet.PlanetId;
        }
        pendingPutdowns.Add(packet);
    }

    /// <summary>
    ///     Report every recorded insertion as one packet. Called once per tick, so a drag that fills a
    ///     belt stack by stack costs one message instead of one per stack.
    /// </summary>
    private void FlushPutdownUpdates()
    {
        if (pendingPutdowns.Count == 0)
        {
            return;
        }
        var count = pendingPutdowns.Count;
        var beltId = new int[count];
        var itemId = new int[count];
        var itemCount = new byte[count];
        var itemInc = new byte[count];
        for (var i = 0; i < count; i++)
        {
            beltId[i] = pendingPutdowns[i].BeltId;
            itemId[i] = pendingPutdowns[i].ItemId;
            itemCount[i] = pendingPutdowns[i].ItemCount;
            itemInc[i] = pendingPutdowns[i].ItemInc;
        }
        var planetId = pendingPutdownPlanet;
        pendingPutdowns.Clear();

        var packet = new BeltUpdatePutItemsPacket(planetId, beltId, itemId, itemCount, itemInc);
        if (Multiplayer.Session.IsServer)
        {
            var starId = GameMain.galaxy.PlanetById(planetId).star.id;
            Multiplayer.Session.Network.SendPacketToStar(packet, starId);
        }
        else
        {
            Multiplayer.Session.Network.SendPacket(packet);
        }
    }

    public void GameTick()
    {
        FlushPutdownUpdates();
        if (beltPutdownPackets.Count <= 0)
        {
            return;
        }
        try
        {
            if (TryPutItemOnBelt(beltPutdownPackets[0]))
            {
                beltPutdownPackets.RemoveAt(0);
                putDownTimer = 0;
            }
            else if (++putDownTimer > MAX_PUTDOWN_WAIT_TICK)
            {
                Log.Warn(
                    $"Cannot put item{beltPutdownPackets[0].ItemId} on belt{beltPutdownPackets[0].BeltId}, planet{beltPutdownPackets[0].PlanetId}");
                beltPutdownPackets.RemoveAt(0);
                putDownTimer = 0;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(
                $"BeltManager error! Cannot put item{beltPutdownPackets[0].ItemId} on belt{beltPutdownPackets[0].BeltId}, planet{beltPutdownPackets[0].PlanetId}");
            Log.Warn(ex);
            beltPutdownPackets.Clear();
            putDownTimer = 0;
        }
    }
}
