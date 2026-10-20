using System;
using System.Diagnostics;

namespace NebulaeModel.DataStructures;

/// <summary>Cooperative budget: callers finish one atomic operation before testing the deadline.</summary>
internal readonly struct WorkBudget
{
    // Cache the method-group conversions to avoid allocating delegates for each frame budget.
    private static readonly Func<long> TimestampClock = Stopwatch.GetTimestamp;
    private static readonly long TickFrequency = Stopwatch.Frequency;

    private readonly Func<long> clock;
    private readonly long deadline;

    public WorkBudget(double milliseconds) : this(milliseconds, TimestampClock, TickFrequency) { }

    internal WorkBudget(double milliseconds, Func<long> clock, long frequency)
    {
        this.clock = clock;
        deadline = milliseconds <= 0 ? long.MaxValue : clock() + Math.Max(1, (long)(milliseconds * frequency / 1000));
    }

    public bool Expired => clock() >= deadline;
}
