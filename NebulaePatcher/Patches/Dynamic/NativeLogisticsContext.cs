using System;
using System.Threading;
using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Dynamic;

// Limits AddItem/TakeItem capture to native remote shipping. Local production keeps its native path.
[HarmonyPatch(typeof(StationComponent))]
internal static class NativeLogisticsContext
{
    [ThreadStatic] private static int depth;
    public static void Enter() => depth++;
    public static void Exit() => depth--;

    private readonly struct TransferScope
    {
        public readonly StationStore[] Storage;
        public readonly int Index;
        public TransferScope(StationStore[] storage, int index) { Storage = storage; Index = index; }
    }

    // Hold the native inventory lock across selection and capture. A parallel pickup must not
    // change which slot was selected between a prefix and the native method's own lock.
    private static TransferScope BeginTransfer(StationComponent station, int item, bool take)
    {
        if (depth == 0 || !Multiplayer.IsActive || !Multiplayer.Session.IsServer) return default;
        var storage = station.storage;
        Monitor.Enter(storage);
        var index = -1;
        for (var i = 0; i < storage.Length; i++)
            if (storage[i].itemId == item && (!take || storage[i].count > 0)) { index = i; break; }
        return new TransferScope(storage, index);
    }

    private static void EndTransfer(TransferScope scope)
    {
        if (scope.Storage != null) Monitor.Exit(scope.Storage);
    }

    [HarmonyPrefix]
    [HarmonyPatch("DispatchSupplyShip")]
    private static void BeforeDispatch(StationComponent __instance)
    {
        if (Multiplayer.IsActive && Multiplayer.Session.IsServer) Multiplayer.Session.Ships.RegisterStorage(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(StationComponent.AddItem))]
    private static void Added(StationComponent __instance, int itemId, int inc, int __result, TransferScope __state)
    {
        if (__state.Storage != null && __state.Index >= 0 && __result > 0)
            Multiplayer.Session.Ships.Inventory.Record(__instance, __state.Index, itemId, __result, inc);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(StationComponent.AddItem))]
    private static void BeforeAdd(StationComponent __instance, int itemId, out TransferScope __state)
        => __state = BeginTransfer(__instance, itemId, false);

    [HarmonyFinalizer]
    [HarmonyPatch(nameof(StationComponent.AddItem))]
    private static void AddedFinalizer(TransferScope __state) => EndTransfer(__state);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(StationComponent.TakeItem), new[] { typeof(int), typeof(int), typeof(int) },
        new[] { ArgumentType.Ref, ArgumentType.Ref, ArgumentType.Out })]
    private static void Taken(StationComponent __instance, int itemId, int count, int inc, TransferScope __state)
    {
        if (__state.Storage != null && __state.Index >= 0 && count > 0)
            Multiplayer.Session.Ships.Inventory.Record(__instance, __state.Index, itemId, -count, -inc);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(StationComponent.TakeItem), new[] { typeof(int), typeof(int), typeof(int) },
        new[] { ArgumentType.Ref, ArgumentType.Ref, ArgumentType.Out })]
    private static void BeforeTake(StationComponent __instance, int itemId, out TransferScope __state)
        => __state = BeginTransfer(__instance, itemId, true);

    [HarmonyFinalizer]
    [HarmonyPatch(nameof(StationComponent.TakeItem), new[] { typeof(int), typeof(int), typeof(int) },
        new[] { ArgumentType.Ref, ArgumentType.Ref, ArgumentType.Out })]
    private static void TakenFinalizer(TransferScope __state) => EndTransfer(__state);
}
