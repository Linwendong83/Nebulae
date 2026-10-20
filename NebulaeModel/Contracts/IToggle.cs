#region

using System;

#endregion

namespace NebulaeModel.Contracts;

internal interface IToggle
{
    bool Value { get; }

    IDisposable On();
}
