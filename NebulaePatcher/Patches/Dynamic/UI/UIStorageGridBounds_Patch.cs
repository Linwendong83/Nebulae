using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeWorld;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic.UI;

[HarmonyPatch]
internal static class UIStorageGridBounds_Patch
{
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[] { "_OnUpdate", "OnContentMouseDown", "OnContentMouseUp" })
            yield return AccessTools.Method(typeof(UIStorageGrid), name);
    }

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var screenPoint = AccessTools.Method(typeof(UIRoot), nameof(UIRoot.ScreenPointIntoRect));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(screenPoint))
            {
                // Keep all the native hover, filter and tooltip paths outside the unused
                // end of a partial row. Padding the storage would grant extra inventory slots.
                var grid = new CodeInstruction(OpCodes.Ldarg_0);
                grid.MoveLabelsFrom(instruction);
                yield return grid;
                yield return new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(UIStorageGridBounds_Patch), nameof(ScreenPointIntoStorage)));
            }
            else yield return instruction;
        }
    }

    private static bool ScreenPointIntoStorage(Vector3 screenPoint, RectTransform rect, out Vector2 point, UIStorageGrid grid) =>
        UIRoot.ScreenPointIntoRect(screenPoint, rect, out point) &&
        (!Multiplayer.IsActive || ContainsCell(grid.storage, grid.colCount, point));

    internal static bool ContainsCell(StorageComponent storage, int columns, Vector2 point)
    {
        if (storage?.grids == null || columns <= 0) return false;
        var x = Mathf.FloorToInt(point.x / 50f);
        var y = Mathf.FloorToInt(-point.y / 50f);
        return x >= 0 && x < columns && y >= 0 &&
            (long)y * columns + x < System.Math.Min(storage.size, storage.grids.Length);
    }
}
