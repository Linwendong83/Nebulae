#region

using System;
using System.Threading;
using NebulaeModel.Contracts;

#endregion

namespace NebulaeModel.DataStructures;

internal sealed class ToggleSwitch : IToggle
{
    private int onCount;

    public bool Value => onCount > 0;

    public IDisposable On()
    {
        return new Toggle(this, 1);
    }

    public static implicit operator bool(ToggleSwitch toggle)
    {
        return toggle.Value;
    }

    internal readonly struct Toggle : IDisposable
    {
        private readonly ToggleSwitch value;
        private readonly int count;

        public Toggle(ToggleSwitch value, int count)
        {
            this.value = value;
            this.count = count;

            Interlocked.Add(ref value.onCount, count);
        }

        public void Dispose()
        {
            Interlocked.Add(ref value.onCount, -count);
        }
    }
}
