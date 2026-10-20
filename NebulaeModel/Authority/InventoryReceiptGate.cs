using System.Collections.Generic;

namespace NebulaeModel.Authority;

/// <summary>One receipt per command, including while an inventory credit waits for room.</summary>
internal sealed class InventoryReceiptGate
{
    private readonly HashSet<CommandKey> received = new();
    public bool TryAccept(CommandKey key) => key.IsValid && received.Add(key);
    public void Finish(CommandKey key) => received.Remove(key);
    public void Clear() => received.Clear();
}
