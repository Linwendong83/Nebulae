#region

using System;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using NebulaeModel.Contracts;
using NebulaeModel.Logger;
using NebulaeModel.Networking;
using NebulaeNetwork;
using NebulaePatcher.Logger;
using NebulaePatcher.MonoBehaviours;
using NebulaePatcher.Patches.Authority;
using NebulaePatcher.Patches.Dynamic;
using NebulaePatcher.Patches.Misc;
using NebulaeWorld;
using UnityEngine;

#endregion

namespace NebulaePatcher;

[BepInPlugin(PluginInfo.PLUGIN_ID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
public class NebulaePlugin : BaseUnityPlugin
{
    private bool gameBuildChecked;
    private void Awake()
    {
        Log.Init(new BepInExLogger(Logger));

        NebulaeModel.Config.ModInfo = Info;
        NebulaeModel.Config.ModVersion = PluginInfo.PLUGIN_DISPLAY_VERSION;
        if (!NebulaeModel.Config.LoadOptions() || !NebulaeModel.Config.LoadCommandLineOptions())
        {
            Log.Error("Nebulae startup stopped because configuration could not be loaded or migrated.");
            enabled = false;
            return;
        }
        if (!NebulaeModel.Config.CommandLineOptions.VerifyStartupRequirements())
        {
            Application.Quit();
        }
        Multiplayer.IsDedicated = NebulaeModel.Config.CommandLineOptions.IsDedicatedServer;

        try
        {
            Initialize();
        }
        catch (Exception ex)
        {
            Log.Error("Unhandled exception occurred while initializing Nebulae:", ex);
            enabled = false;
            if (Multiplayer.IsDedicated) Application.Quit(1);
        }
    }

    private void Update()
    {
        CheckLoadedGameBuild();
    }

    private void CheckLoadedGameBuild()
    {
        if (gameBuildChecked || GameConfig.gameVersion.Build == 0) return;
        gameBuildChecked = true;
        if (VerifyGameBuild()) return;
        enabled = false;
        if (Multiplayer.IsDedicated) Application.Quit(1);
    }

    private static bool VerifyGameBuild()
    {
        var runningVersion = GameConfig.gameVersion.ToFullString();
        if (AuthorityStartup.CanStartMultiplayer(
                string.Equals(runningVersion, DSPGameVersion.VERSION, StringComparison.Ordinal)))
        {
            Multiplayer.ProtocolReady = true;
            Log.Info($"Verified Dyson Sphere Program {runningVersion}; multiplayer protocol enabled.");
            return true;
        }
        Multiplayer.ProtocolReady = false;
        new Harmony(PluginInfo.PLUGIN_ID).UnpatchSelf();
        Log.Error($"Multiplayer startup refused: game version {runningVersion}, expected {DSPGameVersion.VERSION}; " +
                  $"world-write guards verified={NebulaeModel.Authority.AuthorityRuleGuard.IsLoadVerified}.");
        return false;
    }

    private static void Initialize()
    {
        InitPatches();
        AddNebulaeBootstrapper();
    }

    internal static void StartDedicatedServer(string saveName)
    {
        if (!VerifyGameBuild()) { Application.Quit(1); return; }
        if (!ValidateDedicatedGpu()) return;
        // Mimic UI buttons clicking
        UIMainMenu_Patch.OnMultiplayerButtonClick();
        if (!GameSave.SaveExist(saveName))
        {
            return;
        }
        // Modified from DoLoadSelectedGame
        Log.Info($"Starting dedicated server, loading save : {saveName}");
        DSPGame.StartGame(saveName);
        Log.Info($"Listening server on port {NebulaeModel.Config.Options.HostPort}");
        Multiplayer.HostGame(new Server(NebulaeModel.Config.Options.HostPort, true));
        FPSController.SetFixUPS(NebulaeModel.Config.CommandLineOptions.UpsValue);
    }

    internal static void StartDedicatedServer(GameDesc gameDesc)
    {
        if (!VerifyGameBuild()) { Application.Quit(1); return; }
        if (!ValidateDedicatedGpu()) return;
        // Mimic UI buttons clicking
        UIMainMenu_Patch.OnMultiplayerButtonClick();
        if (gameDesc == null)
        {
            return;
        }
        // Modified from DoLoadSelectedGame
        Log.Info("Starting dedicated server, create new game from parameters:");
        Log.Info(
            $"seed={gameDesc.galaxySeed} starCount={gameDesc.starCount} resourceMultiplier={gameDesc.resourceMultiplier:F1}");
        DSPGame.StartGameSkipPrologue(gameDesc);
        Log.Info($"Listening server on port {NebulaeModel.Config.Options.HostPort}");
        Multiplayer.HostGame(new Server(NebulaeModel.Config.Options.HostPort, false));
        FPSController.SetFixUPS(NebulaeModel.Config.CommandLineOptions.UpsValue);
    }

    private static bool ValidateDedicatedGpu()
    {
        try
        {
            if (!HeadlessShieldPipeline.Ready) throw new InvalidOperationException("Shield backend patches are incomplete; refusing to start.");
            HeadlessShieldBackend.Initialize();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("[headless] Could not initialize the configured shield backend.", ex);
            Application.Quit(1);
            return false;
        }
    }

    private static void InitPatches()
    {
        try
        {
            var runningVersion = GameConfig.gameVersion;
            if (runningVersion.Major != 0 || runningVersion.Minor != 10 || runningVersion.Release != 35)
                throw new InvalidOperationException($"Unsupported game version {runningVersion.ToFullString()}; expected {DSPGameVersion.VERSION}.");
            Log.Info("Patching Dyson Sphere Program...");
            Log.Info($"Applying patches from {PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_DISPLAY_VERSION} made for game version {DSPGameVersion.VERSION}");
            Log.BeginPatchAudit();
            var harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), PluginInfo.PLUGIN_ID);
            harmony.PatchAll(typeof(Fix_Patches));
            if (Multiplayer.IsDedicated)
            {
                Log.Info("Patching for headless mode...");
                harmony.PatchAll(typeof(Dedicated_Server_Patches));
                harmony.PatchAll(typeof(HeadlessShieldPipeline));
                harmony.PatchAll(typeof(Headless_Steam_Patches));
                Headless_Steam_Patches.Apply(harmony);
            }

            NebulaeModel.Authority.AuthorityRuleGuard.RegisterVerifier(
                AuthorityTranspilerGuard.VerifyRequired);
            NebulaeModel.Authority.AuthorityRuleGuard.RegisterVerifier(TurretAuthority_Patch.VerifyCoverage);
            if (Log.PatchDiagnosticCount != 0)
                throw new InvalidOperationException($"{Log.PatchDiagnosticCount} patch compatibility diagnostics were emitted; multiplayer startup is disabled.");

            Log.Info("Patching completed successfully.");
            Multiplayer.ProtocolReady = false; // GlobalObject.ReadVersionList loads the exact build later.

            AuthorityStartup.Validate(NebulaeModel.Authority.AuthorityRuleGuard.VerifyLoadOnce);
        }
        catch (Exception ex)
        {
            Log.Error("Unhandled exception occurred while patching the game:", ex);
            Multiplayer.ProtocolReady = false;
            new Harmony(PluginInfo.PLUGIN_ID).UnpatchSelf();
            // Show error in UIFatalErrorTip to inform normal users
            Harmony.CreateAndPatchAll(typeof(UIFatalErrorTip_Patch));
            Log.Error($"Nebulae Multiplayer Mod is incompatible with game version, expected version {DSPGameVersion.VERSION}\nUnhandled exception occurred while patching the game.");
            throw new InvalidOperationException("Nebulae's protocol patches were not installed completely; multiplayer startup is disabled.", ex);
        }
        finally { Log.EndPatchAudit(); }
    }

    private static void AddNebulaeBootstrapper()
    {
        Log.Info("Applying Nebulae Behaviors..");

        var nebulaeRoot = new GameObject { name = "Nebulae Multiplayer Mod" };
        nebulaeRoot.AddComponent<NebulaeBootstrapper>();

        Log.Info("Behaviors applied.");
    }
}
