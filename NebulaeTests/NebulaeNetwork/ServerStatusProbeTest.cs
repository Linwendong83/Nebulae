using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HarmonyLib;
using NebulaeModel;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Session;
using NebulaeNetwork.ServerList;
using WebSocketSharp;
using WebSocketSharp.Server;
using ModelConfig = NebulaeModel.Config;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeNetwork;

[TestClass]
public class ServerStatusProbeTest
{
    private static ProbeRun? activeRun;

    [TestMethod]
    [DataRow(0)]
    [DataRow(120)]
    public void ResponseDispatchDoesNotWaitForTheRequestSendToReturn(int serverDelayMs)
    {
        using var run = new ProbeRun();
        var previousOptions = ModelConfig.Options;
        var harmony = new Harmony("nebulae.tests.status-probe-dispatch");
        var portFinder = new TcpListener(IPAddress.Loopback, 0);
        portFinder.Start();
        var port = ((IPEndPoint)portFinder.LocalEndpoint).Port;
        portFinder.Stop();

        var processor = new NebulaeNetPacketProcessor();
        processor.SubscribeReusable<ServerStatusResponse>(_ => { });
        var reply = PacketCompression.WrapForSend(processor.Write(
            new ServerStatusResponse(3, true, "0.9.22-dev.10", "probe test")));
        var server = new WebSocketServer(IPAddress.Loopback, port);
        server.AddWebSocketService<ReplyService>("/socket", service =>
        {
            service.Reply = reply;
            service.DelayMs = serverDelayMs;
        });
        try
        {
            ModelConfig.Options = new MultiplayerOptions();
            activeRun = run;
            harmony.Patch(AccessTools.Method(typeof(WebSocket), nameof(WebSocket.Send), new[] { typeof(byte[]) }),
                prefix: new HarmonyMethod(typeof(ServerStatusProbeTest), nameof(BeforeSend)),
                postfix: new HarmonyMethod(typeof(ServerStatusProbeTest), nameof(AfterSend)));
            server.Start();

            ServerStatusProbe.Probe(run.ServerId, $"127.0.0.1:{port}", "");
            ServerProbeResult? result = null;
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 6000)
            {
                if (ServerStatusProbe.TryDequeueResult(out var next) && next.ServerId == run.ServerId)
                {
                    result = next;
                    break;
                }
                Thread.Sleep(1);
            }

            TestAssert.IsNotNull(result, "The status probe must finish.");
            TestAssert.AreEqual(ServerProbeState.Online, result.State);
            TestAssert.AreEqual((ushort)3, result.NumPlayers);
            TestAssert.IsTrue(run.ResponseBeforeSendReturned,
                "An arrived response must not be held behind the OnOpen callback while Send is returning.");
            TestAssert.IsGreaterThanOrEqualTo(serverDelayMs, result.PingMs,
                "Actual server response delays must still be included in the displayed latency.");
        }
        finally
        {
            server.Stop();
            harmony.UnpatchSelf();
            activeRun = null;
            ModelConfig.Options = previousOptions;
        }
    }

    private static void BeforeSend(WebSocket __instance, out ProbeRun? __state)
    {
        var run = activeRun;
        __state = Thread.CurrentThread.Name == $"Nebulae probe {run?.ServerId}" ? run : null;
        if (__state == null) return;
        __instance.OnMessage += (_, _) => run!.Received.Set();
    }

    private static void AfterSend(ProbeRun? __state)
    {
        if (__state == null) return;
        // Hold Send after its bytes reach the wire; responses must still be dispatched.
        __state.ResponseBeforeSendReturned = __state.Received.WaitOne(1500);
    }

    private sealed class ProbeRun : IDisposable
    {
        public readonly string ServerId = Guid.NewGuid().ToString("N");
        public readonly ManualResetEvent Received = new(false);
        public bool ResponseBeforeSendReturned;
        public void Dispose() => Received.Dispose();
    }

    private sealed class ReplyService : WebSocketBehavior
    {
        public byte[] Reply = null!;
        public int DelayMs;

        protected override void OnMessage(MessageEventArgs e)
        {
            if (DelayMs > 0) Thread.Sleep(DelayMs);
            Send(Reply);
        }
    }
}
