#region

using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.MonoBehaviours.Local.Chat;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(VFInput))]
internal class VFInput_Patch
{
    [HarmonyPatch(nameof(VFInput._buildConfirm), MethodType.Getter)]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Original Function Name")]
    public static bool _buildConfirm_Prefix(ref VFInput.InputValue __result)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.Factories.IsIncomingRequest.Value)
        {
            return true;
        }
        __result = default;
        __result.onDown = true;
        return false;
    }

    // The selected object changes rarely, and GetComponent is a native lookup, so cache
    // the verdict per selected object instead of paying two lookups every frame while
    // some non-input UI element stays selected.
    private static GameObject lastSelectedObject;
    private static bool lastSelectedIsInput;

    [HarmonyPatch(nameof(VFInput.UpdateGameStates))]
    [HarmonyPostfix]
    public static void UpdateGameStates_Postfix()
    {
        if (Multiplayer.Session?.JoinSync?.CanInteract == false)
        {
            VFInput.inputing = VFInput.inFullscreenGUI = VFInput.onGUI = VFInput.onGUIOperate = true;
            VFInput.readyToBuild = VFInput.readyToEnterBlueprintMode = VFInput.readyToPlant = false;
            return;
        }
        if (!VFInput.inputing)
        {
            var currentSelectedGameObject = EventSystem.current?.currentSelectedGameObject;
            if (currentSelectedGameObject != lastSelectedObject)
            {
                lastSelectedObject = currentSelectedGameObject;
                lastSelectedIsInput = currentSelectedGameObject != null &&
                                   (currentSelectedGameObject.GetComponent<TMP_InputField>() != null ||
                                    currentSelectedGameObject.GetComponent<InputField>() != null);
            }
            VFInput.inputing = lastSelectedIsInput;
        }

        if (!VFInput.inScrollView && ChatManager.Instance != null)
        {
            VFInput.inScrollView = ChatManager.Instance.IsPointerIn();
        }
    }
}
