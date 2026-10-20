#region

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeModel.Packets.Universe.Editor;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch(typeof(UIDysonBrush_Paint))]
internal class UIDysonBrush_Paint_Transpiler
{
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(UIDysonBrush_Paint._OnUpdate))]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static IEnumerable<CodeInstruction> _OnUpdate_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codeInstructions = instructions as CodeInstruction[] ?? instructions.ToArray();
        try
        {
            var matcher = new CodeMatcher(codeInstructions);

            matcher.MatchForward(false, new CodeMatch(OpCodes.Stfld, AccessTools.Field(typeof(DysonNode), "color")));
            var pos1 = matcher.Pos;
            matcher.MatchBack(false, new CodeMatch(OpCodes.Ldloc_S));
            var pos2 = matcher.Pos;
            matcher.Advance(pos1 - pos2 + 1)
                .Insert(
                    new CodeInstruction(matcher.InstructionAt(pos2 - pos1 - 1)),
                    HarmonyLib.Transpilers.EmitDelegate<Action<DysonNode>>(node =>
                    {
                        if (!Multiplayer.IsActive)
                        {
                            return;
                        }
                        var starIndex = UIRoot.instance.uiGame.dysonEditor.selection.viewStar.index;
                        var color = node.color;
                        Multiplayer.Session.DysonSpheres.RegisterColorChange(starIndex, node.layerId,
                            (byte)DysonSphereComponentType.Node, node.id,
                            (uint)(color.r | (color.g << 8) | (color.b << 16) | (color.a << 24)));
                    })
                );

            matcher.MatchForward(false, new CodeMatch(OpCodes.Stfld, AccessTools.Field(typeof(DysonFrame), "color")));
            pos1 = matcher.Pos;
            matcher.MatchBack(false, new CodeMatch(OpCodes.Ldloc_S));
            pos2 = matcher.Pos;
            matcher.Advance(pos1 - pos2 + 1)
                .Insert(
                    new CodeInstruction(matcher.InstructionAt(pos2 - pos1 - 1)),
                    HarmonyLib.Transpilers.EmitDelegate<Action<DysonFrame>>(frame =>
                    {
                        if (!Multiplayer.IsActive)
                        {
                            return;
                        }
                        var starIndex = UIRoot.instance.uiGame.dysonEditor.selection.viewStar.index;
                        var color = frame.color;
                        Multiplayer.Session.DysonSpheres.RegisterColorChange(starIndex, frame.layerId,
                            (byte)DysonSphereComponentType.Frame, frame.id,
                            (uint)(color.r | (color.g << 8) | (color.b << 16) | (color.a << 24)));
                    })
                );

            matcher.MatchForward(false, new CodeMatch(OpCodes.Stfld, AccessTools.Field(typeof(DysonShell), "color")));
            pos1 = matcher.Pos;
            matcher.MatchBack(false, new CodeMatch(OpCodes.Ldloc_S));
            pos2 = matcher.Pos;
            matcher.Advance(pos1 - pos2 + 1)
                .Insert(
                    new CodeInstruction(matcher.InstructionAt(pos2 - pos1 - 1)),
                    HarmonyLib.Transpilers.EmitDelegate<Action<DysonShell>>(shell =>
                    {
                        if (!Multiplayer.IsActive)
                        {
                            return;
                        }
                        var starIndex = UIRoot.instance.uiGame.dysonEditor.selection.viewStar.index;
                        var color = shell.color;
                        Multiplayer.Session.DysonSpheres.RegisterColorChange(starIndex, shell.layerId,
                            (byte)DysonSphereComponentType.Shell, shell.id,
                            (uint)(color.r | (color.g << 8) | (color.b << 16) | (color.a << 24)));
                    })
                );

            return matcher.InstructionEnumeration();
        }
        catch
        {
            Log.Error("UIDysonBrush_Paint._OnUpdate_Transpiler failed. Mod version not compatible with game version.");
            return codeInstructions;
        }
    }
}
