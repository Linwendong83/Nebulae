using System;
using System.Collections.Generic;

namespace NebulaeModel.DataStructures;

internal sealed class CombatGenerationState
{
    private readonly Dictionary<(int Astro, int Id), long> entries = new();
    private long next = DateTime.UtcNow.Ticks;
    public static int NormalizeAstro(int astro) => astro > 1000000 ? 0 : astro;
    public long Get(int astro, int id) => entries.TryGetValue((NormalizeAstro(astro), id), out var value) ? value : 0;
    public long Create(int astro, int id)
    {
        var value = System.Threading.Interlocked.Increment(ref next);
        Set(astro, id, value);
        return value;
    }
    public void Set(int astro, int id, long generation)
    {
        if (id <= 0 || generation <= 0) return;
        var key = (NormalizeAstro(astro), id);
        if (!entries.TryGetValue(key, out var old) || generation > old) entries[key] = generation;
    }
    public void Clear() => entries.Clear();
}
