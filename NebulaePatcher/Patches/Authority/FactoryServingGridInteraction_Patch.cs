using System;
using HarmonyLib;
using NebulaeWorld;

namespace NebulaePatcher.Patches.Authority;

/// <summary>The temporary UI grid presents native stock; commits update the real assembler.</summary>
[HarmonyPatch]
internal static class FactoryServingGridInteraction_Patch
{
    private static readonly AccessTools.FieldRef<UIAssemblerWindow, StorageComponent> ServingStorage = AccessTools.FieldRefAccess<UIAssemblerWindow, StorageComponent>("servingStorage");
    private static readonly AccessTools.FieldRef<UIStorageGrid, int> SplitGrid = AccessTools.FieldRefAccess<UIStorageGrid, int>("splitGrid");
    private static readonly AccessTools.FieldRef<UIEjectorWindow, StorageComponent> EjectorStorage = AccessTools.FieldRefAccess<UIEjectorWindow, StorageComponent>("servingStorage");
    private static readonly AccessTools.FieldRef<UISiloWindow, StorageComponent> SiloStorage = AccessTools.FieldRefAccess<UISiloWindow, StorageComponent>("servingStorage");
    private readonly struct Target
    {
        public Target(PlanetFactory factory, int id, bool launcher) { Factory = factory; Id = id; Launcher = launcher; }
        public PlanetFactory Factory { get; }
        public int Id { get; }
        private bool Launcher { get; }
        public int[] Items => Launcher ? new[] { Factory.entityPool[Id].ejectorId > 0 ? Factory.factorySystem.ejectorPool[Factory.entityPool[Id].ejectorId].bulletId : Factory.factorySystem.siloPool[Factory.entityPool[Id].siloId].bulletId } : Factory.factorySystem.assemblerPool[Id].recipeExecuteData?.requires;
        public int[] Counts => Launcher ? new[] { Factory.entityPool[Id].ejectorId > 0 ? Factory.factorySystem.ejectorPool[Factory.entityPool[Id].ejectorId].bulletCount : Factory.factorySystem.siloPool[Factory.entityPool[Id].siloId].bulletCount } : Factory.factorySystem.assemblerPool[Id].served;
        public int Take(int slot, int count, bool hand, Player player)
        {
            if (Multiplayer.Session.IsClient)
            {
                var queued = Launcher ? Multiplayer.Session.FactoryProducts.RequestLauncher(Factory, Id, false, hand, count) : Multiplayer.Session.FactoryProducts.RequestInputWithdrawal(Factory, Id, slot, count, hand);
                return queued ? Math.Min(count <= 0 ? Counts[slot] : count, Counts[slot]) : 0;
            }
            return Launcher ? Multiplayer.Session.FactoryProducts.LocalLauncher(Factory, Id, false, hand, player, count) : Multiplayer.Session.FactoryProducts.LocalInputWithdrawal(Factory, Id, slot, count, hand, player);
        }
        public void Put(int slot, int count, bool hand, Player player)
        {
            if (Multiplayer.Session.IsClient)
            {
                if (Launcher) Multiplayer.Session.FactoryProducts.RequestLauncher(Factory, Id, true, hand, count);
                else if (hand) Multiplayer.Session.FactoryProducts.RequestHandInput(Factory, Id, slot, count);
                else Multiplayer.Session.FactoryProducts.RequestInput(Factory, Id, slot);
            }
            else if (Launcher) Multiplayer.Session.FactoryProducts.LocalLauncher(Factory, Id, true, hand, player, count);
            else if (hand) Multiplayer.Session.FactoryProducts.LocalHandInput(Factory, Id, slot, count, player);
            else Multiplayer.Session.FactoryProducts.LocalInput(Factory, Id, slot, player.package);
        }
    }
    private static bool Serving(UIStorageGrid grid, out Target target)
    {
        target = default;
        if (!Multiplayer.IsActive) return false;
        var ui = UIRoot.instance?.uiGame;
        var window = ui?.assemblerWindow;
        if (window?.factory != null && window.assemblerId > 0 && ReferenceEquals(grid.storage, ServingStorage(window))) { target = new Target(window.factory, window.assemblerId, false); return true; }
        var ejector = ui?.ejectorWindow;
        if (ejector?.factory != null && ejector.ejectorId > 0 && ReferenceEquals(grid.storage, EjectorStorage(ejector))) { target = new Target(ejector.factory, ejector.factory.factorySystem.ejectorPool[ejector.ejectorId].entityId, true); return true; }
        var silo = ui?.siloWindow;
        if (silo?.factory != null && silo.siloId > 0 && ReferenceEquals(grid.storage, SiloStorage(silo))) { target = new Target(silo.factory, silo.factory.factorySystem.siloPool[silo.siloId].entityId, true); return true; }
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(UIStorageGrid), "HandTake")]
    private static bool Take(UIStorageGrid __instance, Player player, int grid, int count, ref int __result)
    {
        if (!Serving(__instance, out var window)) return true;
        __result = 0;
        var requires = window.Items;
        if (requires == null || grid < 0 || grid >= requires.Length || player.inhandItemCount > 0 && player.inhandItemId != requires[grid]) return false;
        __result = window.Take(grid, count, true, player);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(UIStorageGrid), "HandPut")]
    private static bool Put(UIStorageGrid __instance, Player player, int grid, int count)
    {
        if (!Serving(__instance, out var window)) return true;
        if (window.Items == null || grid < 0 || grid >= window.Items.Length) return false;
        window.Put(grid, count, true, player);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(UIStorageGrid), "OnGridMouseDown")]
    private static bool ModifiedClick(UIStorageGrid __instance, int grid, int button, bool shift, bool control, Player player)
    {
        if (!Serving(__instance, out var window) || button != 0 || (!shift && !control) || player.inhandItemCount > 0) return true;
        if (SplitGrid(__instance) >= 0) return false;
        var requires = window.Items;
        if (requires == null || grid < 0 || grid >= requires.Length) return false;
        for (var slot = 0; slot < requires.Length; slot++)
        {
            if (control ? requires[slot] != requires[grid] : slot != grid) continue;
            window.Take(slot, 0, false, player);
        }
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(UIStorageGrid), nameof(UIStorageGrid.TransferToOther))]
    private static bool Transfer(UIStorageGrid __instance, UIStorageGrid other, Player player)
    {
        if (Serving(__instance, out var window))
        {
            var requires = window.Items;
            if (requires != null) for (var slot = 0; slot < requires.Length; slot++)
                    window.Take(slot, 0, false, player);
            return false;
        }
        if (other != null && Serving(other, out window) && ReferenceEquals(__instance.storage, player.package))
        {
            var requires = window.Items;
            if (requires != null) for (var slot = 0; slot < requires.Length; slot++)
                    window.Put(slot, 0, false, player);
            return false;
        }
        return true;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(UIAssemblerWindow), nameof(UIAssemblerWindow.OnManualServingContentChange))]
    private static bool TemporaryContent() => !Multiplayer.IsActive;
    [HarmonyPrefix, HarmonyPatch(typeof(UIEjectorWindow), nameof(UIEjectorWindow.OnManualServingContentChange))]
    private static bool EjectorTemporaryContent() => !Multiplayer.IsActive;
    [HarmonyPrefix, HarmonyPatch(typeof(UISiloWindow), nameof(UISiloWindow.OnManualServingContentChange))]
    private static bool SiloTemporaryContent() => !Multiplayer.IsActive;
}
