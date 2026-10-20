#region

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NebulaeModel.Networking;
using NebulaeModel;
using NebulaeModel.Logger;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Session;
using WebSocketSharp;

#endregion

namespace NebulaeNetwork.ServerList;

internal enum ServerProbeState
{
    Online,
    Unreachable,
    Starting,
    PasswordRequired,
    WrongPassword
}

internal class ServerProbeResult
{
    public string ServerId { get; set; }
    public ServerProbeState State { get; set; }
    public int Generation { get; set; }
    public ushort NumPlayers { get; set; }
    public int PingMs { get; set; }
    public string NebulaeVersion { get; set; }
    public string Description { get; set; }
}

/// <summary>
///     Probes saved servers from the server list without joining them: opens a short lived
///     websocket to /socket, asks for a ServerStatusResponse and closes again. The connection
///     never sends a LobbyRequest, so the server keeps it pending and its player count is
///     not affected.
/// </summary>
internal static class ServerStatusProbe
{
    private const int TimeoutMs = 4000;
    private const string AuthUser = "nebulae-player";

    private static int currentGeneration;
    private static readonly ConcurrentQueue<ServerProbeResult> results = new();
    private static readonly ConcurrentDictionary<string, byte> inFlight = new();

    public static bool TryDequeueResult(out ServerProbeResult result) => results.TryDequeue(out result);

    /// <summary>Generation of the newest RefreshAll call; results of older generations are stale.</summary>
    public static int CurrentGeneration => Volatile.Read(ref currentGeneration);

    /// <summary>
    ///     Invalidate every probe that is still running or still queued, then start a fresh
    ///     probe for each given server.
    /// </summary>
    public static void RefreshAll(IEnumerable<(string id, string address, string password)> servers)
    {
        Interlocked.Increment(ref currentGeneration);
        foreach (var (id, address, password) in servers)
        {
            Probe(id, address, password);
        }
    }

    public static void Probe(string serverId, string address, string password)
    {
        if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        // One probe per server at a time: an auto refresh must not pile up connections while
        // an earlier probe is still waiting on a dead host.
        if (!inFlight.TryAdd(serverId, 0))
        {
            return;
        }

        var generation = Volatile.Read(ref currentGeneration);
        var thread = new Thread(() => RunProbe(serverId, address, password, generation))
        {
            IsBackground = true,
            Name = $"Nebulae probe {serverId}"
        };
        thread.Start();
    }

    private static void RunProbe(string serverId, string address, string password, int generation)
    {
        AutoResetEvent responseEvent = null;
        Timer watchdog = null;
        try
        {
            if (!ServerAddress.TryParse(address, Config.Options.HostPort, out var parsed))
            {
                Emit(serverId, generation, new ServerProbeResult { ServerId = serverId, State = ServerProbeState.Unreachable });
                return;
            }

            var socket = new WebSocket(NetUtils.MakeWebSocketUrl(parsed.Protocol, parsed.Host, parsed.Port));

            var hasPassword = !string.IsNullOrEmpty(password);
            var authFailed = false;
            var emitted = 0;
            responseEvent = new AutoResetEvent(false);
            ServerStatusResponse response = null;
            var stopwatch = new Stopwatch();

            void EmitOnce(ServerProbeResult result)
            {
                if (Interlocked.Exchange(ref emitted, 1) == 0)
                {
                    Emit(serverId, generation, result);
                }
            }

            var packetProcessor = new NebulaeNetPacketProcessor();
            packetProcessor.SubscribeReusable<ServerStatusResponse>(r =>
            {
                stopwatch.Stop();
                response = r;
                responseEvent.Set();
            });

            socket.Log.Level = LogLevel.Fatal;
            socket.Log.Output = (data, _) =>
            {
                // The websocket layer reports a rejected basic auth handshake only through its
                // log, same trick as Client.cs.
                if (data.Level == LogLevel.Fatal && data.Message == "Requires the authentication.")
                {
                    authFailed = true;
                }
            };

            socket.OnMessage += (_, e) =>
            {
                // Single short lived socket: decode inline instead of going through the queue.
                // The probe never shakes hands, so it must frame and unframe on its own, exactly
                // like the live connection paths do.
                byte[] packet;
                try
                {
                    if (!e.IsBinary) throw new InvalidDataException("Packet frames require binary websocket messages");
                    packet = PacketCompression.UnwrapForReceive(e.RawData);
                    packetProcessor.ReadPacket(new NetDataReader(packet), null);
                }
                catch (Exception exception) when (exception is InvalidDataException or ParseException)
                {
                    Log.Warn($"Probe of {address} received an unreadable message: {exception.Message}");
                    return;
                }
            };
            socket.OnClose += (_, e) =>
            {
                if (e.Code == (ushort)DisconnectionReason.HostStillLoading)
                {
                    EmitOnce(new ServerProbeResult { ServerId = serverId, State = ServerProbeState.Starting });
                }
                responseEvent.Set();
            };

            if (hasPassword)
            {
                socket.SetCredentials(AuthUser, password, true);
            }

            // A black hole address can block the connect call far past our deadline, so a
            // watchdog reports Unreachable and shuts the socket down; the first
            // classification wins.
            watchdog = new Timer(_ =>
            {
                EmitOnce(new ServerProbeResult { ServerId = serverId, State = ServerProbeState.Unreachable });
                try
                {
                    socket.Close(CloseStatusCode.Abnormal);
                }
                catch
                {
                    // ignored
                }
            }, null, TimeoutMs + 500, Timeout.Infinite);

            try
            {
                socket.Connect();
                // WebSocketSharp queues incoming messages until OnOpen returns. Send only
                // after Connect returns so an early response can be dispatched immediately.
                if (socket.ReadyState == WebSocketState.Open)
                {
                    stopwatch.Restart();
                    socket.Send(PacketCompression.WrapForSend(packetProcessor.Write(new ServerStatusRequest())));
                }
            }
            catch (Exception e)
            {
                // A refused connect is an ordinary probe outcome: the wait below times out and the
                // watchdog reports Unreachable, so only record why the connect never happened.
                Log.Debug($"Probe of {address} could not connect: {e.GetType()}");
            }

            var remaining = TimeoutMs - (int)stopwatch.ElapsedMilliseconds;
            responseEvent.WaitOne(Math.Max(0, remaining));

            if (authFailed)
            {
                EmitOnce(new ServerProbeResult
                {
                    ServerId = serverId,
                    State = hasPassword ? ServerProbeState.WrongPassword : ServerProbeState.PasswordRequired
                });
            }
            else if (response != null)
            {
                EmitOnce(new ServerProbeResult
                {
                    ServerId = serverId,
                    State = ServerProbeState.Online,
                    NumPlayers = response.NumPlayers,
                    PingMs = (int)stopwatch.ElapsedMilliseconds,
                    NebulaeVersion = response.NebulaeVersion,
                    Description = response.Description
                });
            }
            else
            {
                EmitOnce(new ServerProbeResult { ServerId = serverId, State = ServerProbeState.Unreachable });
            }

            try
            {
                socket.Close((ushort)DisconnectionReason.ClientRequestedDisconnect, "status probe");
            }
            catch
            {
                // The socket may already be gone, we do not care at this point.
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Probe of {address} failed unexpectedly:\n{e}");
            Emit(serverId, generation, new ServerProbeResult { ServerId = serverId, State = ServerProbeState.Unreachable });
        }
        finally
        {
            watchdog?.Dispose();
            responseEvent?.Dispose();
            inFlight.TryRemove(serverId, out _);
        }
    }

    private static void Emit(string serverId, int generation, ServerProbeResult result)
    {
        result.ServerId = serverId;
        result.Generation = generation;
        results.Enqueue(result);
    }
}
