using System;

namespace NebulaeModel.Packets.Factory.Assembler;

internal enum FactoryInteractionKind : byte { Withdraw, Deposit, Recipe, Acceleration, InputWithdraw, FighterPort, FleetConfigure }

internal readonly struct FactoryProductIntent
{
    public const byte Category = 3;
    public FactoryProductIntent(int recipe, int slot, int item, int count)
        : this(FactoryInteractionKind.Withdraw, recipe, slot, item, count) { }
    public FactoryProductIntent(FactoryInteractionKind kind, int recipe, int slot = -1, int item = 0, int count = 0, int inc = 0, int nextRecipe = 0, bool hand = false)
    { Kind = kind; Recipe = recipe; Slot = slot; Item = item; Count = count; Inc = inc; NextRecipe = nextRecipe; Hand = hand; }
    public FactoryInteractionKind Kind { get; }
    public int Recipe { get; }
    public int Slot { get; }
    public int Item { get; }
    public int Count { get; }
    public int Inc { get; }
    public int NextRecipe { get; }
    public bool Hand { get; }
    public byte[] Encode()
    {
        var bytes = new byte[26]; bytes[0] = (byte)Kind; bytes[25] = Hand ? (byte)1 : (byte)0;
        var values = new[] { Recipe, Slot, Item, Count, Inc, NextRecipe };
        for (var i = 0; i < values.Length; i++) Buffer.BlockCopy(BitConverter.GetBytes(values[i]), 0, bytes, 1 + i * 4, 4);
        return bytes;
    }
    public static bool TryDecode(byte[] bytes, out FactoryProductIntent intent)
    {
        intent = default;
        if (bytes?.Length != 26 || bytes[25] > 1) return false;
        intent = new FactoryProductIntent((FactoryInteractionKind)bytes[0], BitConverter.ToInt32(bytes, 1), BitConverter.ToInt32(bytes, 5),
            BitConverter.ToInt32(bytes, 9), BitConverter.ToInt32(bytes, 13), BitConverter.ToInt32(bytes, 17), BitConverter.ToInt32(bytes, 21), bytes[25] == 1);
        if (intent.Kind == FactoryInteractionKind.FleetConfigure)
            return intent.Recipe == -5 && intent.Slot == -1 && intent.Item > 0 && intent.Count == 0 &&
                intent.Inc == 0 && intent.NextRecipe == 0 && !intent.Hand;
        if (intent.Kind == FactoryInteractionKind.FighterPort)
            return (intent.Recipe == -5 || intent.Recipe == -6) && intent.Slot >= 0 && intent.Slot < 8 * 256 &&
                intent.Slot % 256 < 64 && intent.Item >= 0 && intent.Count >= 0 && intent.Inc >= 0 &&
                (long)intent.Inc <= (long)intent.Count * 10 && intent.NextRecipe >= 0 && intent.NextRecipe < 16 &&
                (intent.NextRecipe & 3) <= 2 && !intent.Hand &&
                ((intent.NextRecipe & 3) == 2 ? intent.Count == 0 && intent.Inc == 0 : (intent.Count == 0) == (intent.Item == 0));
        if (intent.Kind > FactoryInteractionKind.InputWithdraw || intent.Recipe < -4 || intent.Inc < 0 || (long)intent.Inc > (long)intent.Count * 10) return false;
        if (intent.Recipe <= -3 && (intent.Kind is not (FactoryInteractionKind.Withdraw or FactoryInteractionKind.Deposit) || intent.Slot != 0)) return false;
        if (intent.Recipe == -2 && (intent.Kind is not (FactoryInteractionKind.Withdraw or FactoryInteractionKind.Deposit) || intent.Kind == FactoryInteractionKind.Deposit && !intent.Hand)) return false;
        return intent.Kind switch
        {
            FactoryInteractionKind.Withdraw => (intent.Recipe > 0 || intent.Recipe <= -2) && intent.Slot >= 0 && intent.Slot < 16 && intent.Item > 0 && intent.Count > 0 && intent.Inc == 0,
            FactoryInteractionKind.Deposit => intent.Recipe != 0 && intent.Slot >= 0 && intent.Slot < 16 && intent.Item > 0 && intent.Count > 0,
            FactoryInteractionKind.Recipe => intent.NextRecipe >= -1 && intent.Count == 0 && intent.Item == 0,
            FactoryInteractionKind.Acceleration => intent.Count is 0 or 1 && intent.Item == 0,
            FactoryInteractionKind.InputWithdraw => intent.Recipe != 0 && intent.Slot >= 0 && intent.Slot < 16 && intent.Item > 0 && intent.Count > 0 && intent.Inc == 0,
            _ => false
        };
    }
}
