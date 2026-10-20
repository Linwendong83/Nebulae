#region

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using NebulaeModel.Logger;
using NebulaeWorld;
using UnityEngine;
using Object = UnityEngine.Object;

#endregion

namespace NebulaePatcher.Patches.Misc;

// DSP 0.10.35 ships a Steamworks SDK that no longer initializes offline from steam_appid.txt, so on a
// host without a running Steam client SteamManager.Awake and VFPreload.PreloadThread both call
// Application.Quit during boot. A dedicated server does not need Steam: every consumer of the Steam
// session guards on PARTNER.STEAMWORKS (achievements, cloud saves, leaderboards, persona name), so the
// safe headless state is SteamManager.Initialized == false with the two boot-time quits neutralized.
// This part only gets patched when Multiplayer.IsDedicated is true.
internal static class Headless_Steam_Patches
{
    // Replaces SteamManager.Awake on dedicated servers: same duplicate-instance bootstrap, but an
    // unavailable Steam session is logged and survived instead of quitting the process.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamManager), nameof(SteamManager.Awake))]
    public static bool SteamManagerAwake_Prefix(SteamManager __instance)
    {
        if (SteamManager.s_instance != null)
        {
            Object.Destroy(__instance.gameObject);
            return false;
        }

        SteamManager.s_instance = __instance;
        bool initialized = false;
        try
        {
            initialized = TrySteamInit();
        }
        catch (Exception ex)
        {
            Log.Error("[headless] SteamAPI initialization threw; continuing without Steam.", ex);
        }

        __instance.m_bInitialized = initialized;
        if (initialized)
        {
            SteamManager.s_EverInitialized = true;
        }
        else
        {
            Log.Info("[headless] SteamAPI_Init() failed; running the dedicated server without Steam.");
        }

        return false;
    }

    private static bool TrySteamInit()
    {
        // Steamworks.NET is a game-side assembly the mod does not reference, so reach SteamAPI.Init by name.
        var init = AccessTools.Method(AccessTools.TypeByName("Steamworks.SteamAPI"), "Init", Type.EmptyTypes);
        return init != null && init.Invoke(null, null) is bool ok && ok;
    }

    // The steamless quit sits inside the compiler-generated VFPreload/<PreloadThread>d__*.MoveNext, so
    // it is reached with a transpiler that redirects Application.Quit() to a dedicated-aware stub.
    public static void Apply(Harmony harmony)
    {
        try
        {
            var stateMachine = typeof(VFPreload).GetNestedTypes(AccessTools.all)
                .FirstOrDefault(t => t.Name.StartsWith("<PreloadThread>"));
            var moveNext = AccessTools.DeclaredMethod(stateMachine, "MoveNext");
            if (moveNext == null)
            {
                Log.Warn("[headless] VFPreload.PreloadThread state machine not found; the steamless quit stays vanilla.");
                return;
            }

            harmony.Patch(moveNext,
                transpiler: new HarmonyMethod(typeof(Headless_Steam_Patches), nameof(PreloadThreadQuitTranspiler)));
        }
        catch (Exception ex)
        {
            Log.Error("[headless] Could not neutralize the VFPreload steamless quit.", ex);
        }
    }

    public static IEnumerable<CodeInstruction> PreloadThreadQuitTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var quit = AccessTools.Method(typeof(Application), nameof(Application.Quit), Type.EmptyTypes);
        var replacement = AccessTools.Method(typeof(Headless_Steam_Patches), nameof(SteamlessQuit));
        foreach (var instruction in instructions)
        {
            yield return instruction.Calls(quit) ? new CodeInstruction(OpCodes.Call, replacement) : instruction;
        }
    }

    public static void SteamlessQuit()
    {
        if (Multiplayer.IsDedicated)
        {
            Log.Warn("[headless] VFPreload tried to quit over a missing Steam session; request ignored.");
            return;
        }

        Application.Quit();
    }
}
