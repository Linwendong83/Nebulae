#region

using System;
using System.Collections;
using System.Threading;

#endregion

namespace NebulaeModel.DataStructures;

internal static class CollectionExtensions
{
    public static Locker GetLocked<T>(this T collection, out T result) where T : ICollection
    {
        result = collection;
        return new Locker(collection.SyncRoot);
    }
}

internal readonly struct Locker : IDisposable
{
    private readonly object lockObject;

    public Locker(object lockObject)
    {
        this.lockObject = lockObject;

        Monitor.Enter(lockObject);
    }

    public void Dispose()
    {
        Monitor.Exit(lockObject);
    }
}
