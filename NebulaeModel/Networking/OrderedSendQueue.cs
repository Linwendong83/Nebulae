using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace NebulaeModel.Networking;

/// <summary>One ordered preparer overlaps at most two ready packets with one in-flight send.</summary>
internal sealed class OrderedSendQueue
{
    private const int PrefetchLimit = 2;
    private readonly Queue<(byte[] Bytes, Func<byte[]> Factory, Func<IEnumerable<byte[]>> Batch, bool Groupable)> packets = new();
    private readonly Queue<byte[]> ready = new();
    private IEnumerator<byte[]> batch;
    private readonly Func<byte[], byte[]> prepare;
    private readonly Action<byte[], Action<bool>> send;
    private readonly Action<Action> schedule;
    private readonly Action<Exception> onFailure;
    private readonly Func<byte[][], byte[]> combine;
    private readonly Func<byte[], bool> canCombine;
    private bool preparing, sending, stopped, failed;
    internal bool PreparationBacklogged
    {
        get { lock (packets) return packets.Count >= 8 && ready.Count == 0; }
    }

    public OrderedSendQueue(Func<byte[], byte[]> prepare, Action<byte[], Action<bool>> send,
        Action<Action> schedule = null, Action<Exception> onFailure = null,
        Func<byte[][], byte[]> combine = null, Func<byte[], bool> canCombine = null)
    {
        this.prepare = prepare; this.send = send;
        this.schedule = schedule ?? (work => ThreadPool.QueueUserWorkItem(_ => work()));
        this.onFailure = onFailure;
        this.combine = combine; this.canCombine = canCombine;
    }

    public void Enqueue(byte[] packet) => EnqueueCore((packet, null, null, canCombine?.Invoke(packet) == true));
    public void Enqueue(Func<byte[]> factory, bool groupable = false) => EnqueueCore((null, factory, null, groupable));
    public void EnqueueBatch(byte[][] originals, Func<byte[][], IEnumerable<byte[]>> wrap) =>
        EnqueueCore((null, null, () => PrepareBatch(originals, wrap), false));

    private IEnumerable<byte[]> PrepareBatch(byte[][] originals, Func<byte[][], IEnumerable<byte[]>> wrap)
    {
        var prepared = new byte[originals.Length][];
        for (var i = 0; i < originals.Length; i++)
        {
            prepared[i] = Prepare(originals[i]) ?? throw new InvalidOperationException("Snapshot message was dropped during preparation.");
            originals[i] = null;
        }
        foreach (var framed in wrap(prepared)) yield return framed;
    }

    private void EnqueueCore((byte[] Bytes, Func<byte[]> Factory, Func<IEnumerable<byte[]>> Batch, bool Groupable) packet)
    {
        bool start;
        lock (packets)
        {
            if (stopped) return;
            packets.Enqueue(packet); start = StartPreparation();
        }
        if (start) Schedule();
    }

    // Called under the queue lock. A socket completion never starts a second preparer.
    private bool StartPreparation()
    {
        if (stopped || preparing || ready.Count >= PrefetchLimit || batch == null && packets.Count == 0) return false;
        preparing = true; return true;
    }

    private void Schedule()
    {
        try { schedule(SendNext); }
        catch (Exception error) { Fail(error); }
    }

    // This worker alone evaluates factories, advances batch iterators and updates compression
    // baselines. It can prepare the next packet while the socket sends the previous one.
    private void SendNext()
    {
        try
        {
            while (true)
            {
                (byte[] Bytes, Func<byte[]> Factory, Func<IEnumerable<byte[]>> Batch, bool Groupable) pending = default;
                IEnumerator<byte[]> current;
                lock (packets)
                {
                    if (stopped || ready.Count >= PrefetchLimit || batch == null && packets.Count == 0)
                    { preparing = false; Monitor.PulseAll(packets); return; }
                    current = batch;
                    if (current == null) pending = packets.Dequeue();
                }
                byte[] framed;
                if (current != null)
                {
                    if (!current.MoveNext())
                    {
                        current.Dispose();
                        lock (packets) { if (ReferenceEquals(batch, current)) batch = null; }
                        continue;
                    }
                    framed = current.Current;
                }
                else if (pending.Batch != null)
                {
                    var next = pending.Batch().GetEnumerator();
                    lock (packets)
                    {
                        if (stopped) { next.Dispose(); preparing = false; return; }
                        batch = next;
                    }
                    continue;
                }
                else
                {
                    var packet = pending.Factory == null ? pending.Bytes : pending.Factory();
                    if (packet != null && combine != null && pending.Groupable && packet.Length < RealtimePacketBatch.MaxBytes)
                    {
                        var group = new List<byte[]> { packet }; var bytes = packet.Length;
                        while (group.Count < RealtimePacketBatch.MaxPackets && bytes < RealtimePacketBatch.MaxBytes)
                        {
                            (byte[] Bytes, Func<byte[]> Factory, Func<IEnumerable<byte[]>> Batch, bool Groupable) next;
                            lock (packets)
                            {
                                if (stopped || packets.Count == 0 || !packets.Peek().Groupable) break;
                                next = packets.Dequeue();
                            }
                            var value = next.Factory == null ? next.Bytes : next.Factory();
                            if (value != null) { group.Add(value); bytes += value.Length; }
                        }
                        if (group.Count > 1) packet = combine(group.ToArray());
                    }
                    framed = packet == null ? null : Prepare(packet);
                }
                if (framed == null) continue;
                byte[] outgoing;
                lock (packets)
                {
                    if (stopped) { preparing = false; return; }
                    ready.Enqueue(framed); outgoing = TakeReady();
                }
                if (outgoing != null) SendReady(outgoing);
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private byte[] TakeReady()
    {
        if (stopped || sending || ready.Count == 0) return null;
        sending = true; return ready.Dequeue();
    }

    private void SendReady(byte[] bytes)
    {
        try
        {
            lock (packets) { if (stopped) return; }
            send(bytes, Complete);
        }
        catch (Exception error) { Fail(error); }
    }

    private byte[] Prepare(byte[] packet)
    {
        return prepare(packet);
    }

    private void Complete(bool success)
    {
        if (!success) { Fail(new System.IO.IOException("Websocket send failed")); return; }
        bool start; byte[] next;
        lock (packets)
        {
            if (stopped) return;
            sending = false; next = TakeReady(); start = StartPreparation();
            Monitor.PulseAll(packets);
        }
        if (next != null) SendReady(next);
        if (start) Schedule();
    }

    public void Stop(bool failure = false)
    {
        lock (packets)
        {
            stopped = true; failed |= failure; preparing = false; sending = false;
            packets.Clear(); ready.Clear();
            // Iterators own managed arrays only. The active preparer owns any current MoveNext.
            batch = null; Monitor.PulseAll(packets);
        }
    }

    private void Fail(Exception error)
    {
        lock (packets) { if (stopped) return; Stop(true); }
        onFailure?.Invoke(error);
    }

    public bool Flush(int timeoutMilliseconds)
    {
        var start = Stopwatch.GetTimestamp();
        lock (packets)
        {
            while (preparing || sending || packets.Count > 0 || ready.Count > 0 || batch != null)
            {
                var remaining = timeoutMilliseconds - (int)((Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency);
                if (remaining <= 0 || !Monitor.Wait(packets, remaining)) return false;
            }
            return !failed && !stopped;
        }
    }
}
