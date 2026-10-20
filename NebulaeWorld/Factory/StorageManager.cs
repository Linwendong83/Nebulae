#region

using System;
using NebulaeModel.DataStructures;
using UnityEngine.UI;

#endregion

namespace NebulaeWorld.Factory;

internal class StorageManager : IDisposable
{
    public readonly ToggleSwitch IsIncomingRequest = new();
    public Slider ActiveBansSlider;
    public Text ActiveBansValueText;
    public StorageComponent ActiveStorageComponent;
    public UIStorageGrid ActiveUIStorageGrid;
    public Text ActiveWindowTitle;
    public bool IsHumanInput = false;
    public bool WindowOpened = false;
    public bool RefreshPending;
    public long RefreshSequence { get; private set; }
    public long BeginRefresh() { RefreshPending = true; return ++RefreshSequence; }
    public bool AcceptRefresh(long sequence) => sequence == RefreshSequence;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
