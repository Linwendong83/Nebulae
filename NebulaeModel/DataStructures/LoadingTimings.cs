using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace NebulaeModel.DataStructures;

/// <summary>Bounded samples for loading performance statistics.</summary>
internal sealed class LoadingTimings
{
    private sealed class Samples
    {
        public readonly double[] Values = new double[256];
        public int Count;
        public double Total, Max;
        public void Add(double value)
        { Values[Count++ % Values.Length] = value; Total += value; Max = Math.Max(Max, value); }
    }
    private readonly Dictionary<string, Samples> stages = new();
    public static long Now => Stopwatch.GetTimestamp();
    public static double Elapsed(long start) => (Now - start) * 1000.0 / Stopwatch.Frequency;

    public void Record(string stage, double milliseconds)
    {
        lock (stages)
        {
            if (!stages.TryGetValue(stage, out var samples)) stages.Add(stage, samples = new Samples());
            samples.Add(milliseconds);
        }
    }

    public string Describe()
    {
        lock (stages)
            return string.Join("; ", stages.Select(pair =>
            {
                var s = pair.Value;
                var sorted = s.Values.Take(Math.Min(s.Count, s.Values.Length)).OrderBy(x => x).ToArray();
                return string.Format(CultureInfo.InvariantCulture, "{0}: n={1} total={2:F2}ms p95={3:F2}ms max={4:F2}ms",
                    pair.Key, s.Count, s.Total, sorted[(int)((sorted.Length - 1) * .95)], s.Max);
            }));
    }

    public void Clear() { lock (stages) stages.Clear(); }
}
