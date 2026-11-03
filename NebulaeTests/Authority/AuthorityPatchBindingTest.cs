
using System.Reflection;
using HarmonyLib;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.Authority;

/// <summary>
/// Verify prefix and postfix argument names against the installed game. Resolving a Harmony
/// target alone does not prove that its injected parameters can bind when patches are installed.
/// </summary>
[TestClass]
public class AuthorityPatchBindingTest
{
    private static readonly HashSet<string> MagicNames = new(StringComparer.Ordinal)
    {
        "__instance", "__result", "__state", "__args", "__originalMethod", "__runOriginal", "__exception",
    };

    private static bool IsMagic(string name)
    {
        if (MagicNames.Contains(name)) return true;
        if (name.StartsWith("___", StringComparison.Ordinal)) return true;
        // Harmony positional original-argument injection: __0, __1, ...
        if (name.Length > 2 && name[0] == '_' && name[1] == '_')
        {
            for (var i = 2; i < name.Length; i++)
                if (name[i] < '0' || name[i] > '9')
                    return false;
            return name.Length > 2;
        }
        return false;
    }

    [TestMethod]
    public void EveryPrefixAndPostfixBindsItsParametersByName()
    {
        var failures = new List<string>();
        var checkedPatches = 0;
        var patcher = typeof(NebulaePatcher.NebulaePlugin).Assembly;
        foreach (var patchClass in patcher.GetTypes().Where(type =>
                     type.Namespace?.StartsWith("NebulaePatcher.Patches.", StringComparison.Ordinal) == true &&
                     type.GetCustomAttributesData().Any(attribute => attribute.AttributeType == typeof(HarmonyPatch))))
        {
            try
            {
                foreach (var (target, patchMethod) in DeclaredPatches(patchClass))
                {
                    if (!patchMethod.IsDefined(typeof(HarmonyPrefix)) && !patchMethod.IsDefined(typeof(HarmonyPostfix))) continue;
                    if (target == null)
                    {
                        failures.Add(patchClass.FullName + "." + patchMethod.Name + ": target missing");
                        continue;
                    }
                    checkedPatches++;
                    var parameters = target.GetParameters().ToDictionary(parameter => parameter.Name!);
                    foreach (var parameter in patchMethod.GetParameters())
                    {
                        if (parameter.Name!.StartsWith("___", StringComparison.Ordinal))
                        {
                            var fieldName = parameter.Name.Substring(3);
                            var field = int.TryParse(fieldName, out var index)
                                ? AccessTools.DeclaredField(target.DeclaringType, index)
                                : AccessTools.Field(target.DeclaringType, fieldName);
                            var fieldType = parameter.ParameterType.IsByRef
                                ? parameter.ParameterType.GetElementType()!
                                : parameter.ParameterType;
                            if (field == null || !fieldType.IsAssignableFrom(field.FieldType))
                                failures.Add(patchClass.FullName + "." + patchMethod.Name + ": " + parameter.Name +
                                             " does not bind a compatible field on " + target.DeclaringType);
                            continue;
                        }
                        if (IsMagic(parameter.Name!)) continue;
                        var label = patchClass.FullName + "." + patchMethod.Name + ": " + parameter.Name;
                        if (!parameters.TryGetValue(parameter.Name!, out var original))
                        {
                            failures.Add(label + " not found in " + target);
                            continue;
                        }
                        var originalType = original.ParameterType;
                        var patchType = parameter.ParameterType;
                        if (originalType.IsByRef) originalType = originalType.GetElementType()!;
                        if (patchType.IsByRef) patchType = patchType.GetElementType()!;
                        if (!patchType.IsAssignableFrom(originalType))
                            failures.Add(label + " has incompatible type " + patchType + "; original is " + originalType);
                    }
                }
            }
            catch (Exception error) { failures.Add(patchClass.FullName + ": " + error.Message); }
        }
        TestAssert.IsGreaterThan(50,
            checkedPatches, "Expected to check dozens of patches, saw " + checkedPatches + "; the scan is broken.");
        TestAssert.IsEmpty(failures, "Patch parameter binding failures:\n" + string.Join("\n", failures));
    }
}
