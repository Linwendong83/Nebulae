#region

using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using NebulaeModel;
using NebulaeModel.Logger;
using NebulaeWorld;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

#endregion

namespace NebulaePatcher.Patches.Misc;

// Dedicated server patches: suppress rendering and Unity compute; shields use native D3D11 or CPU.
// This part only get patch when Multiplayer.IsDedicated is true
internal class Dedicated_Server_Patches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameMain), nameof(GameMain.Begin))]
    public static void GameMainBegin_Postfix()
    {
        // Repeated Begin calls indicate overlapping menu-demo and server loaders.
        if (Interlocked.Increment(ref gameMainBeginCount) > 1)
            Log.Warn("[headless] GameMain.Begin was called more than once; loaders are overlapping");
        // Server.Start() could not restore the saved player data because the world did not exist yet.
        SaveManager.EnsureServerDataLoaded();
        if (!Multiplayer.IsActive)
        {
            return;
        }
        if (Config.Options.AutoPauseEnabled)
        {
            GameMain.Pause();
        }

        if (GameMain.mainPlayer != null)
        {
            // Don't let the player of dedicated server to interact with enemies
            GameMain.mainPlayer.isAlive = false;
            // Don't let the player of dedicated server send out construction drones 
            GameMain.mainPlayer.mecha.constructionModule.droneEnabled = false;
        }
    }

    // A dedicated server needs no menu demo. This must stay paired with VFPreload's
    // IsMenuDemoLoaded override: otherwise the demo and server loaders can overlap and
    // call GameMain.Begin twice, causing duplicate-key errors in the achievement UI.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DSPGame), nameof(DSPGame.StartDemoGame))]
    public static bool StartDemoGame_Prefix()
    {
        return false;
    }

    // Stop game rendering
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameLogic), nameof(GameLogic.Draw))]
    [HarmonyPatch(typeof(GameLogic), nameof(GameLogic.DrawPost))]
    [HarmonyPatch(typeof(FactoryModel), nameof(FactoryModel.LateUpdate))]
    [HarmonyPatch(typeof(SectorModel), nameof(SectorModel.LateUpdate))]
    public static bool OnDraw_Prefix()
    {
        return false;
    }

    private static int gameMainBeginCount;

    // The dedicated host's dead mecha must not slow or stop the world. Use the vanilla
    // living-player timing rules, including fullscreen pause and one-frame unlock.
    // GameMain_Patch's postfix still applies the multiplayer CanPause policy.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameMain), nameof(GameMain.DetermineGameTickRate))]
    public static bool DetermineGameTickRate_Prefix(bool ____fullscreenPaused,
        ref bool ____fullscreenPausedUnlockOneFrame, ref int __result)
    {
        if (____fullscreenPaused && !____fullscreenPausedUnlockOneFrame)
        {
            __result = 0;
            return false;
        }

        ____fullscreenPausedUnlockOneFrame = false;
        __result = 1;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameMain), nameof(GameMain.LateUpdate))]
    public static bool OnLateUpdate()
    {
        // Because UIRoot._LateUpdate() doesn't run in headless mode, we need this to enable autosave
        UIRoot.instance.uiGame.autoSave._LateUpdate();
        return false;
    }

    // Destroy gameObject so Update() won't execute
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlanetAtmoBlur), nameof(PlanetAtmoBlur.Start))]
    public static bool PlanetAtmoBlur_Start(PlanetAtmoBlur __instance)
    {
        Object.Destroy(__instance.gameObject);
        return false;
    }

    // RenderTexture is not support, so disable all functions using the constructor
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DysonMapCamera), nameof(DysonMapCamera.CheckOrCreateRTex))]
    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.CaptureScreenshot))] // Save won't have a img preview when disable
    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.CaptureScreenShot))]
    [HarmonyPatch(typeof(MechaEditorCamera), nameof(MechaEditorCamera.CheckOrCreateRTex))]
    [HarmonyPatch(typeof(UIDysonOrbitPreview), nameof(UIDysonOrbitPreview.CheckOrCreateRTex))]
    [HarmonyPatch(typeof(UIMechaMaterialBall), nameof(UIMechaMaterialBall._OnCreate))]
    [HarmonyPatch(typeof(UIMechaSaveGroup), nameof(UIMechaSaveGroup._OnCreate))]
    [HarmonyPatch(typeof(UIMilkyWay), nameof(UIMilkyWay.CheckOrCreateRTex))]
    [HarmonyPatch(typeof(UIMinimap3DControl), nameof(UIMinimap3DControl._OnCreate))]
    [HarmonyPatch(typeof(UIMinimap3DControl), nameof(UIMinimap3DControl._OnDestroy))] // No texture was created on the dedicated host.
    [HarmonyPatch(typeof(UISplitterWindow), nameof(UISplitterWindow._OnCreate))]
    [HarmonyPatch(typeof(UIStarmap), nameof(UIMilkyWay.CheckOrCreateRTex))]
    [HarmonyPatch(typeof(TranslucentImageSource), nameof(TranslucentImageSource.CreateNewBlurredScreen))]
    private static bool RenderTexture_Prefix()
    {
        return false;
    }

    // Keep the managed lifecycle state used by _OnInit/_OnFree without allocating
    // the advisor's audio visualization buffer on a graphics-less server.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIAdvisorTip), nameof(UIAdvisorTip._OnCreate))]
    public static bool UIAdvisorTipCreate_Prefix(UIAdvisorTip __instance)
    {
        __instance.requests = new List<int>();
        return false;
    }

    // An invisible host must not open advisor UI that depends on the skipped buffer.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIAdvisorTip), nameof(UIAdvisorTip.RequestAdvisorTip))]
    [HarmonyPatch(typeof(UIAdvisorTip), nameof(UIAdvisorTip.RunAdvisorTip))]
    public static bool UIAdvisorTipPlay_Prefix()
    {
        return false;
    }

    // Dedicated servers never use Unity compute. The shield's independent native D3D11 device
    // and CPU backend do not need Unity kernels, dispatches or buffer transfers.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ComputeShader), nameof(ComputeShader.FindKernel))]
    [HarmonyPatch(typeof(ComputeShader), nameof(ComputeShader.GetKernelThreadGroupSizes))]
    public static bool ComputeShaderKernel_Prefix()
    {
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ComputeShader), nameof(ComputeShader.Dispatch))]
    public static bool ComputeShaderDispatch_Prefix()
    {
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ComputeBuffer), nameof(ComputeBuffer.SetData), typeof(Array))]
    [HarmonyPatch(typeof(ComputeBuffer), nameof(ComputeBuffer.GetData), typeof(Array), typeof(int), typeof(int), typeof(int))]
    public static bool ComputeBuffer_Prefix()
    {
        return false;
    }


    // From user report
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Graphic), nameof(Graphic.DoMeshGeneration))]
    [HarmonyPatch(typeof(Graphic), nameof(Graphic.DoLegacyMeshGeneration))]
    public static bool DoMeshGeneration_Prefix()
    {
        return false;
    }

    // Fixes a UI Object reference not set error during headless load due to there being no UI enabled.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UICommunicatorIndicator), nameof(UICommunicatorIndicator._OnLateUpdate))]
    public static bool UICommunicatorIndicatorOnLateUpdate_Prefix()
    {
        return false;
    }

    // HeadlessShieldPipeline replaces the compute transport while the original method still
    // applies coverage, capacity and collision state. A finalizer handles backend failures even
    // when the recalculation throws, and validates the physics output on successful returns.
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(PlanetATField), nameof(PlanetATField.RecalculatePhysicsShape))]
    public static void RecalculatePhysicsShape_Finalizer(PlanetATField __instance, Exception __exception)
    {
        if (__exception != null)
        {
            Log.Error("[headless] Shield computation failed after configured fallback; stopping.", __exception);
            Application.Quit(1);
            return; // Harmony still propagates the original exception.
        }
        if (__instance == null || __instance.generatorCount <= 0)
        {
            return;
        }

        var args = __instance.physicsArgs;
        var vertexCount = __instance.physicsMeshVertsOriginal?.Length ?? 0;
        var planetId = __instance.planet?.id ?? -1;
        if (args == null || args.Length == 0 || vertexCount == 0)
        {
            Log.Error($"[headless] shield planet={planetId} has no valid physics output; stopping.");
            Application.Quit(1);
            return;
        }
    }
}
