using System;
using HarmonyLib;
using NebulaeModel.Packets.Authority;
using NebulaeWorld;
using NebulaeWorld.Combat;

namespace NebulaePatcher.Patches.Authority;

[HarmonyPatch]
internal static class PlayerCombatAuthority_Patch
{
    private static bool Client => Multiplayer.IsActive && Multiplayer.Session.IsClient && Multiplayer.Session.Authority.IsActive;

    // Remote bodies are kinematic. The native accessor reads/writes Rigidbody.velocity,
    // which both discards accepted movement and logs a Unity warning on every host frame.
    private static bool RemoteController(PlayerController controller) => Multiplayer.IsActive &&
        Multiplayer.Session.IsServer && Multiplayer.Session.CombatAuthority.Owns(controller.player);

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerController), nameof(PlayerController.velocity), MethodType.Setter)]
    public static bool SetVelocity(PlayerController __instance, UnityEngine.Vector3 __0, ref VectorLF3 ____lVelocity)
    {
        if (!RemoteController(__instance)) return true;
        ____lVelocity = __0;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerController), nameof(PlayerController.velocity), MethodType.Getter)]
    public static bool GetVelocity(PlayerController __instance, VectorLF3 ____lVelocity, ref UnityEngine.Vector3 __result)
    {
        if (!RemoteController(__instance)) return true;
        __result = ____lVelocity;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.factory), MethodType.Getter)]
    public static bool CombatFactory_Prefix(Player __instance, ref PlanetFactory __result)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsServer || !Multiplayer.Session.CombatAuthority.Owns(__instance)) return true;
        __result = NebulaeWorld.Combat.CombatAuthorityManager.FactoryForCombat(__instance.planetData);
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StorageComponent), nameof(StorageComponent.NotifyStorageChange))]
    public static void PersonalStockChanged_Postfix(StorageComponent __instance)
    {
        if (Client) Multiplayer.Session.CombatAuthority.MarkPersonalStockChange(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StorageComponent), nameof(StorageComponent.SetFilter))]
    public static void PersonalStockFilterChanged_Postfix(StorageComponent __instance) => PersonalStockChanged_Postfix(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerAction_Combat), nameof(PlayerAction_Combat.ShootTarget))]
    public static void ShootTarget_Prefix(PlayerAction_Combat __instance, EAmmoType ammoType, SkillTarget target,
        SkillTarget ___ammoTargetManual, SkillTarget ___laserTargetManual, out PlayerWeaponManager.FireCapture __state)
    {
        __state = null;
        if (!Multiplayer.IsActive || !Multiplayer.Session.Authority.IsActive) return;
        var manual = ammoType == EAmmoType.Laser ? ___laserTargetManual : ___ammoTargetManual;
        var kind = __instance.manualAttacking && manual.id == target.id && manual.astroId == target.astroId
            ? CombatIntentKind.ManualShoot : CombatIntentKind.Shoot;
        __state = Multiplayer.Session.PlayerWeapons.BeginFire(new CombatIntent(kind, (int)ammoType, ammoType == EAmmoType.Laser ? 0 : __instance.mecha.ammoItemId,
            ammoType == EAmmoType.Laser ? 0 : __instance.mecha.ammoSelectSlot), target);
    }

    [HarmonyFinalizer, HarmonyPatch(typeof(PlayerAction_Combat), nameof(PlayerAction_Combat.ShootTarget))]
    public static Exception ShootTarget_Finalizer(PlayerWeaponManager.FireCapture __state, bool __result, Exception __exception)
    {
        Multiplayer.Session?.PlayerWeapons.FinishFire(__state, __exception == null && __result);
        return __exception;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerAction_Combat), nameof(PlayerAction_Combat.Bombing))]
    public static void Bombing_Prefix(ref VectorLF3 initialVel, out PlayerWeaponManager.FireCapture __state)
    {
        __state = Multiplayer.IsActive && Multiplayer.Session.Authority.IsActive
            ? Multiplayer.Session.PlayerWeapons.BeginFire(new CombatIntent(CombatIntentKind.Bomb, x: initialVel.x, y: initialVel.y, z: initialVel.z), default) : null;
    }

    [HarmonyFinalizer, HarmonyPatch(typeof(PlayerAction_Combat), nameof(PlayerAction_Combat.Bombing))]
    public static Exception Bombing_Finalizer(PlayerWeaponManager.FireCapture __state, Exception __exception)
    {
        Multiplayer.Session?.PlayerWeapons.FinishFire(__state, __exception == null);
        return __exception;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerAction_Combat), nameof(PlayerAction_Combat.ShieldBurst))]
    public static void ShieldBurst_Prefix(PlayerAction_Combat __instance, out PlayerWeaponManager.FireCapture __state)
    {
        __state = Multiplayer.IsActive && Multiplayer.Session.Authority.IsActive ? Multiplayer.Session.PlayerWeapons.BeginFire(
            new CombatIntent(CombatIntentKind.ShieldBurst, x: __instance.mecha.energyShieldBurstProgress), default) : null;
    }

    [HarmonyFinalizer, HarmonyPatch(typeof(PlayerAction_Combat), nameof(PlayerAction_Combat.ShieldBurst))]
    public static Exception ShieldBurst_Finalizer(PlayerWeaponManager.FireCapture __state, Exception __exception)
    { Multiplayer.Session?.PlayerWeapons.FinishFire(__state, __exception == null); return __exception; }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Mecha), nameof(Mecha.UpdateCombatStats))]
    public static bool UpdateCombatStats_Prefix(Mecha __instance, double dt)
    {
        if (!Client) return true;
        Multiplayer.Session.PlayerWeapons.Resources.Recovery.Tick(__instance, dt);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Mecha), nameof(Mecha.TakeDamage))]
    public static bool TakeDamage_Prefix() => !Client;

    [HarmonyPrefix, HarmonyPatch(typeof(Mecha), nameof(Mecha.CheckLaunchFleetCondition))]
    public static bool CheckLaunchFleetCondition(Mecha __instance, bool isSpace, ref bool __result)
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.IsServer || !Multiplayer.Session.CombatAuthority.Owns(__instance.player)) return true;
        // Remote planets have combat pools without the host loading their terrain/renderer.
        // Using factoryLoaded here makes the next module tick immediately recycle a valid launch.
        __result = isSpace || __instance.player.movementState < EMovementState.Sail &&
            CombatAuthorityManager.FactoryForCombat(__instance.player.planetData)?.combatGroundSystem != null;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.GameTick))]
    public static bool CombatModuleTick_Prefix() => !Client;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.RemoveFleetDirectly))]
    public static bool RemoveFleetDirectly_Prefix() => !Client;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Mecha), nameof(Mecha.TickAmmoFireCondition))]
    [HarmonyPatch(typeof(Mecha), nameof(Mecha.TickLaserFireCondition))]
    public static void FireClock_Prefix(Mecha __instance, out PlayerWeaponResources.Scope __state)
    {
        __state = null;
        if (!Client || PlayerWeaponResources.Active) return;
        Multiplayer.Session.CombatAuthority.SendSettings(GameMain.gameTick, immediate: true);
        __state = Multiplayer.Session.PlayerWeapons.Resources.Enter(__instance);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerAction_Combat), "GameTickBombing")]
    [HarmonyPatch(typeof(PlayerAction_Combat), "GameTickShieldBurst")]
    public static void BombClock_Prefix(PlayerAction_Combat __instance, out PlayerWeaponResources.Scope __state) =>
        FireClock_Prefix(__instance.mecha, out __state);

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Mecha), nameof(Mecha.TickAmmoFireCondition))]
    [HarmonyPatch(typeof(Mecha), nameof(Mecha.TickLaserFireCondition))]
    [HarmonyPatch(typeof(PlayerAction_Combat), "GameTickBombing")]
    [HarmonyPatch(typeof(PlayerAction_Combat), "GameTickShieldBurst")]
    public static Exception FireClock_Finalizer(PlayerWeaponResources.Scope __state, Exception __exception)
    {
        __state?.Dispose();
        return __exception;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(Mecha), nameof(Mecha.LoadAmmo))]
    public static bool LoadAmmo_Prefix() => !Client || PlayerWeaponResources.Active;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.LaunchFleet))]
    public static bool LaunchFleet_Prefix(CombatModuleComponent __instance, int fleetIndex, Player player)
    {
        if (!Client) return true;
        if (player != GameMain.mainPlayer) return false;
        var argument = fleetIndex + (__instance == player.mecha.spaceCombatModule ? 100 : 0);
        if (Multiplayer.Session.CombatAuthority.Send(new CombatIntent(CombatIntentKind.FleetLaunch, argument), default, out var sequence))
            Multiplayer.Session.FleetCommands.Begin(__instance, fleetIndex, false, sequence);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.RecycleFleet))]
    public static bool RecallFleet_Prefix(CombatModuleComponent __instance, int fleetIndex)
    {
        if (!Client) return true;
        var mecha = GameMain.mainPlayer.mecha;
        if (__instance != mecha.groundCombatModule && __instance != mecha.spaceCombatModule) return false;
        if (Multiplayer.Session.CombatAuthority.Send(new CombatIntent(CombatIntentKind.FleetRecall,
            fleetIndex + (__instance == mecha.spaceCombatModule ? 100 : 0)), default, out var sequence))
            Multiplayer.Session.FleetCommands.Begin(__instance, fleetIndex, true, sequence);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CombatModuleComponent), nameof(CombatModuleComponent.ChangeFleetConfig))]
    public static bool FleetConfig_Prefix(CombatModuleComponent __instance, int fleetIndex, int newConfigId)
    {
        if (!Client) return true;
        var mecha = GameMain.mainPlayer.mecha;
        if (__instance != mecha.groundCombatModule && __instance != mecha.spaceCombatModule) return false;
        Multiplayer.Session.CombatAuthority.Send(new CombatIntent(CombatIntentKind.FleetConfigure,
            fleetIndex + (__instance == mecha.spaceCombatModule ? 100 : 0), newConfigId));
        return false;
    }
}
