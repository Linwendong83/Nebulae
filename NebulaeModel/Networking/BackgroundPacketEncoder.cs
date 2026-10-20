using System;
using System.Threading;
using System.Threading.Tasks;

namespace NebulaeModel.Networking;

// Encoding must run independently of the socket/compression pump. Otherwise a
// deferred factory puts CPU work back on the single transport lane and builds latency.
internal sealed class BackgroundPacketEncoder
{
    private readonly int limit;
    private readonly Action<Action> schedule;
    private int active;
    internal int Active => Volatile.Read(ref active);
    internal BackgroundPacketEncoder(int limit = 4, Action<Action> schedule = null)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        this.limit = limit;
        this.schedule = schedule ?? (work => ThreadPool.QueueUserWorkItem(_ => work()));
    }

    internal Func<byte[]> Prepare(Func<byte[]> encode)
    {
        if (Interlocked.Increment(ref active) > limit)
        {
            Interlocked.Decrement(ref active);
            var immediate = encode(); // Bounded CPU work, without queuing blocked pool workers.
            return () => immediate;
        }
        var result = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            schedule(() =>
            {
                try { result.SetResult(encode()); }
                catch (Exception error) { result.SetException(error); _ = result.Task.Exception; }
                finally { Interlocked.Decrement(ref active); }
            });
        }
        catch (Exception error)
        {
            Interlocked.Decrement(ref active);
            result.SetException(error); _ = result.Task.Exception;
        }
        return () => result.Task.GetAwaiter().GetResult();
    }
}
