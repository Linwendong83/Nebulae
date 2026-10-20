using System;
using System.Collections.Generic;
using System.Linq;
using NebulaeModel.Packets.Players;

namespace NebulaeWorld.Player;

/// <summary>Measures connection RTT without treating a local player as zero latency.</summary>
internal static class PlayerLatencyTracker
{
    private static readonly Dictionary<ushort, int> Latencies = new();
    private static MultiplayerSession currentSession;
    private static DateTime nextProbeAt;

    public static void Update()
    {
        if (!Multiplayer.IsActive || Multiplayer.Session?.Network == null)
        {
            currentSession = null;
            Latencies.Clear();
            return;
        }

        var session = Multiplayer.Session;
        if (currentSession != session)
        {
            currentSession = session;
            Latencies.Clear();
            nextProbeAt = DateTime.MinValue;
        }

        if (!session.IsGameLoaded) return;

        var now = DateTime.UtcNow;
        if (now < nextProbeAt) return;
        nextProbeAt = now.AddSeconds(5);

        if (session.IsServer)
        {
            var connected = session.Server.Players.Connected.ToArray();
            var ids = new HashSet<ushort>(connected.Select(pair => pair.Value.Id));
            foreach (var id in Latencies.Keys.Where(id => !ids.Contains(id)).ToArray()) Latencies.Remove(id);
            foreach (var pair in connected)
            {
                pair.Key.SendPacket(new PlayerLatencyPacket { Kind = 0, SentTicks = now.Ticks });
            }
            session.Network.SendPacket(new PlayerLatencyPacket
            {
                Kind = 2,
                PlayerIds = Latencies.Keys.ToArray(),
                Milliseconds = Latencies.Values.ToArray()
            });
        }
        else
        {
            session.Network.SendPacket(new PlayerLatencyPacket { Kind = 0, SentTicks = now.Ticks });
        }
    }

    public static void Record(ushort playerId, long sentTicks)
    {
        if (playerId == 0 || sentTicks <= 0) return;
        var elapsed = DateTime.UtcNow.Ticks - sentTicks;
        if (elapsed < 0 || elapsed > TimeSpan.TicksPerSecond * 30) return;
        Latencies[playerId] = (int)Math.Round(elapsed / (double)TimeSpan.TicksPerMillisecond);
    }

    public static void ApplySnapshot(ushort[] ids, int[] milliseconds)
    {
        if (ids == null || milliseconds == null || ids.Length != milliseconds.Length) return;
        for (var i = 0; i < ids.Length; i++)
        {
            if (milliseconds[i] >= 0 && milliseconds[i] <= 30000)
                Latencies[ids[i]] = milliseconds[i];
        }
    }

    public static bool TryGet(ushort playerId, out int milliseconds) => Latencies.TryGetValue(playerId, out milliseconds);
}
