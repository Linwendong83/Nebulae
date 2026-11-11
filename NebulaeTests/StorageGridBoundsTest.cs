using HarmonyLib;
using UnityEngine;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class StorageGridBoundsTest
{
    [TestMethod]
    public void PartialInventoryRowHasNoHoverOrFilterTargetBeyondTheRealSlots()
    {
        var storage = Uninitialized<StorageComponent>();
        storage.size = 150; storage.grids = new StorageComponent.GRID[150];
        // 12 columns: the thirteenth row has six real cells, followed by six blanks.
        Check.IsTrue(UIStorageGridBounds_Patch.ContainsCell(storage, 12, new Vector2(275, -625)));
        Check.IsFalse(UIStorageGridBounds_Patch.ContainsCell(storage, 12, new Vector2(325, -625)));
        Check.IsFalse(UIStorageGridBounds_Patch.ContainsCell(storage, 12, new Vector2(25, -675)));
        Check.IsFalse(UIStorageGridBounds_Patch.ContainsCell(storage, 12, new Vector2(-1, -25)));
        Check.IsFalse(UIStorageGridBounds_Patch.ContainsCell(storage, 12, new Vector2(600, -25)));
        Check.AreEqual(150, storage.size);
    }

    [TestMethod]
    public void BoundsPatchBindsToTheInstalledGamesHoverAndClickMethods()
    {
        // Inspect the installed game's real IL; Unity supplies the engine ECalls at runtime.
        var screenPoint = AccessTools.Method(typeof(UIRoot), nameof(UIRoot.ScreenPointIntoRect));
        foreach (var name in new[] { "_OnUpdate", "OnContentMouseDown", "OnContentMouseUp" })
        {
            var method = AccessTools.Method(typeof(UIStorageGrid), name);
            Check.IsGreaterThan(20, method.GetMethodBody()!.GetILAsByteArray()!.Length);
            var original = PatchProcessor.GetOriginalInstructions(method).ToArray();
            Check.AreEqual(1, original.Count(code => code.Calls(screenPoint)), name);
            var patched = UIStorageGridBounds_Patch.Transpiler(original).ToArray();
            Check.AreEqual(0, patched.Count(code => code.Calls(screenPoint)), name);
            Check.AreEqual(1, patched.Count(code => code.Calls(AccessTools.Method(typeof(UIStorageGridBounds_Patch), "ScreenPointIntoStorage"))), name);
        }
    }
}
