using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Linq;
using System.IO;
using HarmonyLib;
using NebulaeWorld.Player;

namespace NebulaePatcher.Patches.Transpilers;

[HarmonyPatch]
internal static class VegetationAccess_Transpiler
{
    private static IEnumerable<Type> GameTypes()
    {
        try { return typeof(Player).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types.Where(type => type != null); }
    }
    private static IEnumerable<MethodInfo> Methods(Type type, BindingFlags flags)
    {
        try { return type.GetMethods(flags); }
        catch (Exception error) when (error is TypeLoadException || error is IOException) { return Array.Empty<MethodInfo>(); }
    }
    private static byte[] Body(MethodInfo method)
    {
        try { return method.GetMethodBody()?.GetILAsByteArray(); }
        catch (Exception error) when (error is TypeLoadException || error is IOException) { return null; }
    }
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        var getter = AccessTools.PropertyGetter(typeof(Player), nameof(Player.vegetableCollection));
        var token = BitConverter.GetBytes(getter.MetadataToken);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var type in GameTypes())
            foreach (var method in Methods(type, flags))
            {
                var bytes = Body(method);
                if (bytes == null) continue;
                for (var i = 0; i < bytes.Length - 4; i++)
                    if ((bytes[i] == 0x28 || bytes[i] == 0x6f) && bytes[i + 1] == token[0] && bytes[i + 2] == token[1] && bytes[i + 3] == token[2] && bytes[i + 4] == token[3])
                    { yield return method; break; }
            }
    }

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getter = AccessTools.PropertyGetter(typeof(Player), nameof(Player.vegetableCollection));
        var replacement = AccessTools.Method(typeof(VegetationAccess), nameof(VegetationAccess.Collection));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(getter)) { instruction.opcode = OpCodes.Call; instruction.operand = replacement; }
            yield return instruction;
        }
    }
}
