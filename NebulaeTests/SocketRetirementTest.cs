using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.GameStates;
using NebulaeModel.Utils;
using NebulaeNetwork.Messaging;
using WebSocketSharp;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class SocketRetirementTest
{
    private sealed class RecordingLogger : ILogger
    {
        public readonly ConcurrentQueue<string> Warnings = new(), Errors = new();
        public void LogDebug(object data) { }
        public void LogInfo(object data) { }
        public void LogWarning(object data) => Warnings.Enqueue(data.ToString()!);
        public void LogError(object data) => Errors.Enqueue(data.ToString()!);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SocketCallbacksRetireTransportBeforeMainThreadCleanup(bool error)
    {
        var processor = new NebulaeNetPacketProcessor();
        var socket = Socket(new MemoryStream());
        var connection = new NebulaeConnection(socket, new IPEndPoint(IPAddress.Loopback, 8469), processor);
        var jobs = new Queue<Action>(); var sends = 0; var failures = 0;
        var queue = new OrderedSendQueue(b => b, (_, done) => { sends++; done(true); }, jobs.Enqueue, _ => failures++);
        SetField(connection, "sender", queue);
        var service = new WebSocketService(); SetField(service, "connection", connection);
        var bytes = processor.Write(new GameStateRequest());
        connection.SendRawPacket(bytes);
        if (error)
        {
            var args = Activator.CreateInstance(typeof(WebSocketSharp.ErrorEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { "fixture", new IOException("fixture") }, null);
            AccessTools.Method(typeof(WebSocketService), "OnError").Invoke(service, new[] { args });
        }
        else
        {
            var args = Activator.CreateInstance(typeof(CloseEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { (ushort)2001, "Player left the game", true }, null);
            AccessTools.Method(typeof(WebSocketService), "OnClose").Invoke(service, new[] { args });
        }
        Check.IsTrue(connection.IsStopped);
        Check.IsFalse(connection.IsAlive);
        Check.IsNull(connection.ReceivePacket(bytes));
        connection.SendRawPacket(bytes);
        while (jobs.Count != 0) jobs.Dequeue()();
        Check.AreEqual(0, sends); Check.AreEqual(0, failures);
        connection.StopSending(); // Repeated socket/main-thread retirement stays harmless.
        ((MainThreadActionQueue)AccessTools.Field(typeof(UnityDispatchQueue), "actionsQueue").GetValue(null)).Drain();
    }

    [TestMethod]
    public void AClosingSocketStopsQueuedSendsWithoutInventingAnIoFailure()
    {
        var processor = new NebulaeNetPacketProcessor();
        var socket = Socket(new MemoryStream());
        SetField(socket, "_readyState", WebSocketState.Closing);
        var connection = new NebulaeConnection(socket, new IPEndPoint(IPAddress.Loopback, 8469), processor);
        connection.SendPacket(new GameStateRequest());
        Check.IsFalse(connection.FlushSendQueue(3000));
        Check.IsTrue(connection.IsStopped);
        var queue = (OrderedSendQueue)AccessTools.Field(typeof(NebulaeConnection), "sender").GetValue(connection);
        Check.IsNull(queue.FailureDiagnostics);
    }

    [TestMethod]
    public void AnOpenSocketWriteFailureStillRecordsTheFailureAndRetiresTheConnection()
    {
        var previous = (ILogger)AccessTools.Field(typeof(Log), "logger").GetValue(null);
        var logger = new RecordingLogger();
        try
        {
            Log.Init(logger);
            var processor = new NebulaeNetPacketProcessor();
            var connection = new NebulaeConnection(Socket(new BrokenStream()), new IPEndPoint(IPAddress.Loopback, 8469), processor);
            connection.SendPacket(new GameStateRequest());
            Check.IsFalse(connection.FlushSendQueue(3000));
            Check.IsTrue(SpinWait.SpinUntil(() => connection.IsStopped, 3000));
            var queue = (OrderedSendQueue)AccessTools.Field(typeof(NebulaeConnection), "sender").GetValue(connection);
            Check.IsNotNull(queue.FailureDiagnostics);
            Check.HasCount(1, logger.Warnings);
            Check.IsEmpty(logger.Errors);
            StringAssert.Contains(logger.Warnings.Single(), "stage=socket-send");
            ((MainThreadActionQueue)AccessTools.Field(typeof(UnityDispatchQueue), "actionsQueue").GetValue(null)).Drain();
        }
        finally { Log.Init(previous); }
    }

    [TestMethod]
    [DataRow("socket-send", "io", true)]
    [DataRow("socket-send", "socket", true)]
    [DataRow("socket-send", "disposed", true)]
    [DataRow("socket-send", "wrapped-io", true)]
    [DataRow("socket-send", "wrapped-bug", false)]
    [DataRow("socket-send", "invalid-data", false)]
    [DataRow("socket-send", "bug", false)]
    [DataRow("encode", "io", false)]
    [DataRow("snapshot-batch", "io", false)]
    [DataRow("delta-compression", "io", false)]
    [DataRow("", "io", false)]
    public void OnlyTransportExceptionsDuringSocketSendAreWarnings(string stage, string kind, bool warning)
    {
        Exception error = kind switch
        {
            "socket" => new SocketException((int)SocketError.ConnectionReset),
            "disposed" => new ObjectDisposedException("socket"),
            "wrapped-io" => new TargetInvocationException(new TargetInvocationException(new IOException("reset"))),
            "wrapped-bug" => new TargetInvocationException(new InvalidOperationException("bug")),
            "invalid-data" => new InvalidDataException("bad packet"),
            "bug" => new NullReferenceException("bug"),
            _ => new IOException("io")
        };
        error.Data["Nebulae.SendStage"] = stage;
        Check.AreEqual(warning, NebulaeConnection.IsTransportSendFailure(error));
    }

    [TestMethod]
    [DataRow("encode")]
    [DataRow("delta-compression")]
    [DataRow("snapshot-batch")]
    [DataRow("schedule")]
    public void PreparationAndSchedulingIoFailuresAreNotTreatedAsDisconnects(string stage)
    {
        Exception? failure = null;
        var queue = new OrderedSendQueue(bytes => stage == "delta-compression" ? throw new IOException("compression") : bytes,
            (_, done) => done(true), work => { if (stage == "schedule") throw new IOException("schedule"); work(); },
            error => failure = error);
        if (stage == "encode") queue.Enqueue(() => throw new IOException("encoding"));
        else if (stage == "snapshot-batch") queue.EnqueueBatch(new[] { new byte[] { 1 } }, _ => throw new IOException("snapshot"));
        else queue.Enqueue(new byte[] { 1 });
        Check.IsNotNull(failure);
        if (stage != "schedule") Check.AreEqual(stage, failure.Data["Nebulae.SendStage"]);
        Check.IsFalse(NebulaeConnection.IsTransportSendFailure(failure));
        Check.IsFalse(queue.Flush(0));
    }

    private static WebSocket Socket(Stream output)
    {
        var socket = new WebSocket("ws://127.0.0.1:1/socket");
        socket.Log.Output = (_, _) => { };
        SetField(socket, "_readyState", WebSocketState.Open);
        SetField(socket, "_stream", output);
        return socket;
    }


}
