#region

using System.Collections.Generic;
using NebulaeModel.Contracts;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Players;
using NebulaeWorld;

#endregion

namespace NebulaeNetwork.PacketProcessors.Players;

internal class PlayerMovementProcessor : PacketProcessor<PlayerMovement>
{
    private readonly Dictionary<ushort, PlayerMovementRelay> movementRelays = [];

    protected override void ProcessPacket(PlayerMovement packet, NebulaeConnection conn)
    {
        // A client sends this to the host and never receives it back: what the host relays is the
        // quantised display form, handled by PlayerMovementDisplayProcessor. Anything else arriving here
        // would be a client's own packet echoed by a broadcast router, which has no meaning to apply.
        if (!IsHost)
        {
            return;
        }
        if (!packet.HasFinitePose() ||
            packet.LocalPlanetId > 0 && GameMain.galaxy.PlanetById(packet.LocalPlanetId) == null)
        {
            return;
        }

        var player = Players.Get(conn);
        if (player == null)
        {
            return;
        }

        packet.PlayerId = player.Id;
        var previousStar = player.Data.LocalStarId;
        var data = (NebulaeModel.DataStructures.PlayerData)player.Data;
        // The client reports its own exact velocities; the seat keeps the latest
        // accepted sample so host combat simulation and rejoin restore stay accurate.
        data.AcceptedVelocityU = packet.VelocityU;
        data.AcceptedVelocityL = packet.VelocityL;
        data.AcceptedMovementTick = GameMain.gameTick;
        data.AcceptedMovementState = packet.MovementState;
        data.AcceptedWarping = (packet.Flags & PlayerMovement.EFlags.warping) != 0;
        packet.BuildArea = UnityEngine.Mathf.Clamp(packet.BuildArea, 0, GameMain.mainPlayer.mecha.buildArea);
        packet.ConstructionDroneCount = System.Math.Max(0,
            System.Math.Min(packet.ConstructionDroneCount, GameMain.mainPlayer.mecha.constructionModule.droneCount));
        player.Data.LocalPlanetId = packet.LocalPlanetId;
        // A validated ground pose also establishes the containing star. This repairs
        // stale arrival metadata before routing visuals and initial combat samples.
        if (packet.LocalPlanetId > 0)
        {
            player.Data.LocalStarId = GameMain.galaxy.PlanetById(packet.LocalPlanetId).star.id;
        }
        player.Data.UPosition = packet.UPosition;
        player.Data.Rotation = packet.Rotation;
        player.Data.BodyRotation = packet.BodyRotation;
        player.Data.LocalPlanetPosition = packet.LocalPlanetPosition;
        Multiplayer.Session.BuildDispatch.UpdateRemoteBuilder(player.Id, packet.BuildArea,
            packet.ConstructionDroneCount, packet.ConstructionDronesEnabled,
            packet.CanLaunchConstructionDrone);
        if (previousStar != player.Data.LocalStarId)
        {
            Multiplayer.Session.BattleVisuals.SendInitial(conn, player.Data.LocalStarId);
            Multiplayer.Session.PlayerWeapons.SendInitial(conn, player.Data.LocalStarId);
        }

        // What the other players receive is the presentation of this pose, not the pose the host keeps.
        // Relaying the quantised display form is what keeps a room's movement traffic from scaling with
        // the number of fields the host needs for itself.
        RelayDisplay(player, packet);
    }

    /// <summary>
    ///     Forward a player's pose to the players who can see them, and their map position to the rest.
    /// </summary>
    private void RelayDisplay(INebulaePlayer player, PlayerMovement packet)
    {
        var display = new PlayerMovementDisplay(packet);
        // The host also simulates remote bodies; combat reads this movement snapshot.
        Multiplayer.Session.World.UpdateRemotePlayerRealtimeState(display);
        if (!movementRelays.TryGetValue(player.Id, out var relay))
            movementRelays[player.Id] = relay = new PlayerMovementRelay();
        relay.Send(Server, display, player.Data.LocalStarId, UnityEngine.Time.time, player.Connection);

        // Entries for players who left are only reachable by id, so they are swept when the table grows
        // past the connected count by a wide margin.
        if (movementRelays.Count > Players.Connected.Count * 2 + 8)
        {
            SweepMapPositionEntries();
        }
    }

    private void SweepMapPositionEntries()
    {
        var connected = new HashSet<ushort>();
        foreach (var kvp in Players.Connected)
        {
            connected.Add(kvp.Value.Id);
        }
        var stale = new List<ushort>();
        foreach (var pair in movementRelays)
        {
            if (!connected.Contains(pair.Key))
            {
                stale.Add(pair.Key);
            }
        }
        foreach (var id in stale)
        {
            movementRelays.Remove(id);
        }
    }
}
