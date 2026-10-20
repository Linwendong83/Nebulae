using System;
using System.Threading;

namespace NebulaeWorld.Combat;

/// <summary>
/// A frame that knows how to undo itself. See <see cref="ScopeStack{T}"/>.
/// </summary>
internal interface IScopeFrame
{
    void Restore();
}

/// <summary>Per-thread stack of saved combat scopes.</summary>
/// <remarks>Patched projectile, laser, sweep and fire ticks save the shared current-player view, redirect it to
/// the authoritative target and restore it on every exit. Each patched call pushes once and its finalizer pops
/// once, preserving strict LIFO nesting without allocation.</remarks>
internal static class ScopeStack<T> where T : struct, IScopeFrame
{
    [ThreadStatic] private static T[] items;
    [ThreadStatic] private static int depth;

    public static void Push(in T frame)
    {
        items ??= new T[16];
        if (depth >= items.Length) Array.Resize(ref items, items.Length * 2);
        items[depth++] = frame;
    }

    public static void Pop()
    {
        if (depth > 0) items[--depth].Restore();
    }
}
