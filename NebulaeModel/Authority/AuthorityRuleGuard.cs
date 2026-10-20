using System;
using System.Collections.Generic;
using System.Reflection;

namespace NebulaeModel.Authority;

/// <summary>The responsibility of a game or mod hook in the multiplayer runtime.</summary>
internal enum AuthorityHookRole : byte
{
    /// <summary>
    /// Unspecified. Only ever the state of a hook the policy table did not classify, which means the
    /// guard lets the vanilla path run.
    /// </summary>
    None = 0,

    /// <summary>
    /// The vanilla method decides shared world facts. On a client it does not run outside a
    /// validated replica apply.
    /// </summary>
    HostRule = 1,

    /// <summary>
    /// The method maintains mirrored state. Legal only inside the replica apply window.
    /// </summary>
    ReplicaApply = 2,

    /// <summary>
    /// Visual or audio only. Runs on every peer, but must not write a protected field.
    /// </summary>
    Presentation = 3,

    /// <summary>Native simulation on loaded client scopes, with shared commits guarded separately.</summary>
    NativeSimulation = 4,

    /// <summary>Lifecycle bookkeeping that changes no shared fact.</summary>
    LifecycleObserve = 5,

    /// <summary>Native drone execution with server-assigned claims and host-only building commits.</summary>
    NativeConstruction = 6
}

/// <summary>
/// The labels the policy table and the call sites share.
/// </summary>
/// <remarks>
/// Names shared by the startup verification and patch coverage checks.
/// </remarks>
internal static class AuthorityHookLabels
{
    public const string CombatStatTickSkillLogic = "CombatStat.TickSkillLogic";
    public const string CombatStatHandleFullHp = "CombatStat.HandleFullHp";
    public const string CombatStatHandleZeroHp = "CombatStat.HandleZeroHp";
    public const string SkillSystemDamageObject = "SkillSystem.DamageObject";
    public const string SkillSystemDamageGroundObjectByLocalCaster = "SkillSystem.DamageGroundObjectByLocalCaster";
    public const string SkillSystemDamageGroundObjectByRemoteCaster = "SkillSystem.DamageGroundObjectByRemoteCaster";
    public const string ConstructionSystemAddConstructStat = "ConstructionSystem.AddConstructStat";
    public const string ConstructionSystemRemoveConstructStat = "ConstructionSystem.RemoveConstructStat";
    public const string ConstructionSystemRepair = "ConstructionSystem.Repair";
    public const string ConstructionSystemDetermineLaunch = "ConstructionSystem.DetermineLaunch";
    public const string ConstructionSystemUpdateModules = "ConstructionSystem.UpdateModules";
    public const string ConstructionSystemUpdateDrones = "ConstructionSystem.UpdateDrones";
    public const string ConstructionSystemAddBuildTargetToModules = "ConstructionSystem.AddBuildTargetToModules";
    /// <summary>The mod's dispatch-claim postfix target on the vanilla drone reset.</summary>
    /// <remarks>The target writes <c>DroneComponent.targetObjectId</c> and is classified as a protected client
    /// write.</remarks>
    public const string ConstructionSystemResetDroneTargets = "ConstructionSystem.ResetDroneTargets";

    public const string ConstructStatGameTick = "ConstructStat.GameTick";
    public const string DroneComponentInternalUpdate = "DroneComponent.InternalUpdate";

    public const string SimulatedWorldOnPlayerJoinedGame = "SimulatedWorld.OnPlayerJoinedGame";

    public const string SimulatedWorldOnPlayerLeftGame = "SimulatedWorld.OnPlayerLeftGame";

    public const string GameLogicLogicFrame = "GameLogic.LogicFrame";

    public const string ThreadManagerProcessFrame = "ThreadManager.ProcessFrame";

    public const string NebulaeNetworkServerUpdate = "NebulaeNetwork.Server.Update";

    /// <summary>
    /// Client-side balance writers outside the authority ledger. The host runs these rules; clients are refused
    /// outside replica apply.
    /// </summary>
    public const string PowerSystemGameTick = "PowerSystem.GameTick";

    public const string UIMechaWindowOnReplaceFuelButtonClick = "UIMechaWindow.OnReplaceFuelButtonClick";

    public const string GameHistoryDataUnlockTechFunction = "GameHistoryData.UnlockTechFunction";

    /// <summary>
    /// Enemy and unit AI entries. The host runs the rules; clients are refused outside replica apply.
    /// </summary>
    public const string EnemyDFGroundSystemGameTickLogicUnit = "EnemyDFGroundSystem.GameTickLogic_Unit";

    /// <summary>
    /// The parallel unit loop lives on <c>GameLogic</c>, not on the ground system: guarding only the
    /// serial entry misses the actual path (README).
    /// </summary>
    public const string GameLogicEnemyGroundUnitParallel = "GameLogic._enemy_ground_unit_parallel";

    public const string EnemyDFHiveSystemGameTickLogic = "EnemyDFHiveSystem.GameTickLogic";
}

/// <summary>World readiness, host identity and the validated replica application window.</summary>
internal readonly struct AuthorityGuardSession
{
    public AuthorityGuardSession(bool isActive, bool isHost, bool replicaApplyActive)
    {
        IsActive = isActive;
        IsHost = isHost;
        ReplicaApplyActive = replicaApplyActive;
    }

    public bool IsActive { get; }

    public bool IsHost { get; }

    /// <summary>True while the frame boundary has a replica apply open.</summary>
    public bool ReplicaApplyActive { get; }

    /// <summary>The state of a peer that is not in an authority room.</summary>
    public static AuthorityGuardSession Inactive => default;
}

/// <summary>
/// One entry of the authority guard policy table.
/// </summary>
internal readonly struct AuthorityHookPolicy
{
    public AuthorityHookPolicy(string label, string typeName, string methodName, AuthorityHookRole role,
        bool required, string owner)
    {
        Label = label;
        TypeName = typeName;
        MethodName = methodName;
        Role = role;
        Required = required;
        Owner = owner;
    }

    /// <summary>Hook identity used by the guard policy.</summary>
    public string Label { get; }

    /// <summary>Declaring type name used to resolve the hook.</summary>
    public string TypeName { get; }

    /// <summary>Method name to resolve when verifying the hook exists.</summary>
    public string MethodName { get; }

    public AuthorityHookRole Role { get; }

    /// <summary>True when multiplayer may not be entered unless this hook resolves.</summary>
    public bool Required { get; }

    /// <summary>Card that replaces the vanilla behaviour with the host-authoritative one.</summary>
    public string Owner { get; }
}

/// <summary>Permits host rule execution and validated client mirror writes.</summary>
internal static class AuthorityRuleGuard
{
    private static readonly object gate = new();
    private static readonly Dictionary<string, bool> required = [];

    private static Func<AuthorityGuardSession> probe;
    private static readonly List<Func<IReadOnlyList<string>>> extraVerifiers = [];
    private static readonly Dictionary<string, Type> resolvedTypes = [];
    private static int loadedAssemblyCount = -1;
    private static bool installed;
    private static bool loadVerified;
    private static string loadFailure;

    /// <summary>
    /// Supplies the live session state. Registered by the world layer; unset means inactive.
    /// </summary>
    public static Func<AuthorityGuardSession> Probe
    {
        get => probe;
        set => probe = value;
    }

    /// <summary>True once the policy table was installed and every required hook resolved.</summary>
    public static bool IsLoadVerified
    {
        get
        {
            lock (gate)
            {
                return loadVerified;
            }
        }
    }

    /// <summary>Why starting multiplayer was refused, or null when it was not.</summary>
    public static string LoadFailure
    {
        get
        {
            lock (gate)
            {
                return loadFailure;
            }
        }
    }

    /// <summary>The session state the guard is currently deciding against.</summary>
    public static AuthorityGuardSession CurrentSession() => probe?.Invoke() ?? AuthorityGuardSession.Inactive;

    /// <summary>The policy table, installed on first use.</summary>
    public static IReadOnlyList<AuthorityHookPolicy> Policy
    {
        get
        {
            EnsureInstalled();
            lock (gate)
            {
                return policyTable.ToArray();
            }
        }
    }

    /// <summary>Hooks whose policy requires a resolvable vanilla target.</summary>
    public static IReadOnlyList<AuthorityHookPolicy> RequiredPolicy
    {
        get
        {
            EnsureInstalled();
            var list = new List<AuthorityHookPolicy>();
            lock (gate)
            {
                foreach (var entry in policyTable)
                {
                    if (entry.Required) list.Add(entry);
                }
            }
            return list;
        }
    }

    /// <summary>True when the hook is recorded as required by the policy table.</summary>
    public static bool IsRequired(string label)
    {
        EnsureInstalled();
        lock (gate)
        {
            return required.TryGetValue(label ?? string.Empty, out var value) && value;
        }
    }

    public static bool AllowHostRule()
    {
        var session = CurrentSession();
        return !session.IsActive || session.IsHost || session.ReplicaApplyActive;
    }

    /// <summary>Forgets the verification result. Used by tests and by leaving a room.</summary>
    public static void ResetVerification()
    {
        lock (gate)
        {
            loadVerified = false;
            loadFailure = null;
        }
    }

    /// <summary>
    /// Resolves every required hook and returns the ones that do not exist.
    /// </summary>
    /// <remarks>
    /// A required hook whose target cannot be resolved means multiplayer would run without its guard, so
    /// the list must be empty before multiplayer is entered. Resolution is by simple type name across
    /// the loaded assemblies, which is enough because the hook inventory names game and mod types that
    /// are always loaded in a running game and in the test process.
    /// </remarks>
    public static IReadOnlyList<string> VerifyRequiredHooks() => VerifyRequiredHooks(ResolveType);

    /// <summary>
    /// <see cref="VerifyRequiredHooks()"/> with an injected resolver, so tests can prove the failure
    /// path without a game assembly.
    /// </summary>
    public static IReadOnlyList<string> VerifyRequiredHooks(Func<string, Type> resolveType)
    {
        if (resolveType == null) throw new ArgumentNullException(nameof(resolveType));
        var failures = new List<string>();
        foreach (var entry in RequiredPolicy)
        {
            var type = resolveType(entry.TypeName);
            if (type == null)
            {
                failures.Add(entry.Label + ": type " + entry.TypeName + " not found");
                continue;
            }
            if (FindMethod(type, entry.MethodName) == null)
            {
                failures.Add(entry.Label + ": method " + entry.MethodName + " not found on " + entry.TypeName);
            }
        }
        return failures;
    }

    /// <summary>
    /// Registers an extra verification step run before multiplayer is entered.
    /// </summary>
    /// <remarks>
    /// Method transformations cannot be verified by resolving a method name: a transpiler that
    /// silently matched nothing leaves a method that exists but runs the wrong rule. The patcher
    /// therefore registers a verifier that reports which of its required transformations actually
    /// applied, and a failure there stops multiplayer just like a missing method does.
    /// </remarks>
    public static void RegisterVerifier(Func<IReadOnlyList<string>> verifier)
    {
        if (verifier == null) throw new ArgumentNullException(nameof(verifier));
        lock (gate)
        {
            if (!extraVerifiers.Contains(verifier)) extraVerifiers.Add(verifier);
        }
    }

    /// <summary>Forgets every registered verifier. Used by tests.</summary>
    public static void ClearVerifiers()
    {
        lock (gate)
        {
            extraVerifiers.Clear();
        }
    }

    /// <summary>Runs every registered verifier and returns their combined failures.</summary>
    public static IReadOnlyList<string> VerifyRegistered()
    {
        Func<IReadOnlyList<string>>[] snapshot;
        lock (gate)
        {
            snapshot = extraVerifiers.ToArray();
        }
        var failures = new List<string>();
        foreach (var verifier in snapshot)
        {
            IReadOnlyList<string> result;
            try
            {
                result = verifier();
            }
            catch (Exception e)
            {
                failures.Add("verifier threw " + e.GetType().Name + ": " + e.Message);
                continue;
            }
            if (result != null) failures.AddRange(result);
        }
        return failures;
    }

    /// <summary>
    /// Verifies the required hooks at most once per process.
    /// </summary>
    /// <remarks>
    /// Called at the point multiplayer would become live. A failure here is reported and multiplayer is not
    /// entered when a required hook is missing.
    /// </remarks>
    /// <returns>True when multiplayer may be entered.</returns>
    public static bool VerifyLoadOnce()
    {
        lock (gate)
        {
            if (loadVerified || loadFailure != null) return loadVerified;
        }

        var failures = new List<string>(VerifyRequiredHooks());
        failures.AddRange(VerifyRegistered());
        lock (gate)
        {
            if (failures.Count == 0)
            {
                loadVerified = true;
                return true;
            }
            loadFailure = string.Join("; ", failures);
            return false;
        }
    }

    /// <summary>Resolves a type by simple or full name across the loaded assemblies.</summary>
    /// <remarks>
    /// <para>
    /// Scanning can itself load an assembly: asking an assembly for its types resolves the types it
    /// references, and the game assembly is only loaded the first time something touches it. A single
    /// pass therefore misses the assembly being loaded by that very pass, which showed up as the
    /// first hook of the list failing to resolve while the second hook on the same type resolved.
    /// The snapshot is retaken whenever the assembly count grows, so the result does not depend on
    /// which test or which game system happened to load the assembly first.
    /// </para>
    /// <para>
    /// A type that cannot be loaded is skipped rather than fatal: the game assembly has types whose
    /// layout the runtime rejects, and one of them must not hide every hook behind it.
    /// </para>
    /// </remarks>
    public static Type ResolveType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;

        // Enumerating every type of every loaded assembly is expensive and throws for the game
        // assembly (its Unity-dependent types cannot all load), so a resolved name is remembered.
        // The cache is dropped when a new assembly appears, because that is exactly what can turn a
        // previous miss into a hit.
        lock (gate)
        {
            if (loadedAssemblyCount == AppDomain.CurrentDomain.GetAssemblies().Length &&
                resolvedTypes.TryGetValue(typeName, out var cached))
            {
                return cached;
            }
        }

        var resolved = ResolveTypeUncached(typeName);
        lock (gate)
        {
            loadedAssemblyCount = AppDomain.CurrentDomain.GetAssemblies().Length;
            resolvedTypes[typeName] = resolved;
        }
        return resolved;
    }

    private static Type ResolveTypeUncached(string typeName)
    {
        var scanned = 0;
        while (true)
        {
            var snapshot = AppDomain.CurrentDomain.GetAssemblies();
            if (snapshot.Length == scanned) return null;

            for (var i = scanned; i < snapshot.Length; i++)
            {
                var direct = TryGetType(snapshot[i], typeName);
                if (direct != null) return direct;

                foreach (var type in SafeTypes(snapshot[i]))
                {
                    if (type.Name == typeName || type.FullName == typeName) return type;
                }
            }
            scanned = snapshot.Length;
        }
    }

    private static Type TryGetType(Assembly assembly, string typeName)
    {
        try
        {
            return assembly.GetType(typeName, false);
        }
        catch (Exception)
        {
            // A type that the runtime refuses to load is not the type being looked for.
            return null;
        }
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            types = error.Types;
        }
        catch (Exception)
        {
            return [];
        }
        if (types == null) return [];
        var usable = new List<Type>(types.Length);
        foreach (var type in types)
        {
            if (type != null) usable.Add(type);
        }
        return usable;
    }

    private static MethodInfo FindMethod(Type type, string methodName) =>
        type.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

    /// <summary>Installs the policy table once.</summary>
    public static void EnsureInstalled()
    {
        lock (gate)
        {
            if (installed) return;
            foreach (var entry in policyTable)
            {
                required[entry.Label] = entry.Required;
            }
            installed = true;
        }
    }

    private static readonly List<AuthorityHookPolicy> policyTable =
    [
        new(AuthorityHookLabels.CombatStatTickSkillLogic, "CombatStat", "TickSkillLogic",
            AuthorityHookRole.HostRule, true, "A19"),
        new(AuthorityHookLabels.CombatStatHandleFullHp, "CombatStat", "HandleFullHp",
            AuthorityHookRole.HostRule, true, "A08/A19"),
        new(AuthorityHookLabels.CombatStatHandleZeroHp, "CombatStat", "HandleZeroHp",
            AuthorityHookRole.HostRule, true, "A19"),
        new(AuthorityHookLabels.SkillSystemDamageObject, "SkillSystem", "DamageObject",
            AuthorityHookRole.HostRule, true, "A19"),
        new(AuthorityHookLabels.SkillSystemDamageGroundObjectByLocalCaster, "SkillSystem",
            "DamageGroundObjectByLocalCaster", AuthorityHookRole.HostRule, true, "A19"),
        new(AuthorityHookLabels.SkillSystemDamageGroundObjectByRemoteCaster, "SkillSystem",
            "DamageGroundObjectByRemoteCaster", AuthorityHookRole.HostRule, true, "A19"),
        new(AuthorityHookLabels.ConstructionSystemAddConstructStat, "ConstructionSystem", "AddConstructStat",
            AuthorityHookRole.HostRule, true, "A15/A18"),
        new(AuthorityHookLabels.ConstructionSystemRemoveConstructStat, "ConstructionSystem", "RemoveConstructStat",
            AuthorityHookRole.HostRule, true, "A15"),
        new(AuthorityHookLabels.ConstructionSystemRepair, "ConstructionSystem", "Repair",
            AuthorityHookRole.HostRule, true, "A17"),
        new(AuthorityHookLabels.ConstructionSystemDetermineLaunch, "ConstructionSystem", "DetermineLaunch",
            AuthorityHookRole.NativeConstruction, true, "BuildDispatch"),
        new(AuthorityHookLabels.ConstructionSystemUpdateModules, "ConstructionSystem", "UpdateModules",
            AuthorityHookRole.NativeConstruction, true, "BuildDispatch"),
        new(AuthorityHookLabels.ConstructionSystemUpdateDrones, "ConstructionSystem", "UpdateDrones",
            AuthorityHookRole.NativeConstruction, true, "BuildDispatch"),
        new(AuthorityHookLabels.ConstructStatGameTick, "ConstructStat", "GameTick",
            AuthorityHookRole.HostRule, true, "A15"),
        // Personal drone energy and motion follow native execution. Shared HP is guarded at Repair.
        new(AuthorityHookLabels.DroneComponentInternalUpdate, "DroneComponent", "InternalUpdate",
            AuthorityHookRole.NativeConstruction, false, "BuildDispatch"),
        // Scheduler and lifecycle entries are classified for coverage;
        // but none of them is a rule writer, so none is required to resolve.
        new(AuthorityHookLabels.SimulatedWorldOnPlayerJoinedGame, "NebulaeWorld.SimulatedWorld",
            "OnPlayerJoinedGame", AuthorityHookRole.LifecycleObserve, false, "A21"),
        new(AuthorityHookLabels.SimulatedWorldOnPlayerLeftGame, "NebulaeWorld.SimulatedWorld",
            "OnPlayerLeftGame", AuthorityHookRole.LifecycleObserve, false, "A21"),
        // The frame boundary is required: if the method that drains the queues
        // disappears, multiplayer would run with no frame boundary at all, so it must fail to load.
        new(AuthorityHookLabels.GameLogicLogicFrame, "GameLogic", "LogicFrame",
            AuthorityHookRole.None, true, "A04"),
        new(AuthorityHookLabels.ThreadManagerProcessFrame, "ThreadManager", "ProcessFrame",
            AuthorityHookRole.None, true, "A04"),
        new(AuthorityHookLabels.NebulaeNetworkServerUpdate, "NebulaeNetwork.Server", "Update",
            AuthorityHookRole.None, false, "A04"),
        // Native construction intake forwards ready sites to the server-owned claim table.
        new(AuthorityHookLabels.ConstructionSystemAddBuildTargetToModules, "ConstructionSystem",
            "AddBuildTargetToModules", AuthorityHookRole.NativeConstruction, false, "BuildDispatch"),
        // Claim release remains part of the live server-owned construction protocol.
        new(AuthorityHookLabels.ConstructionSystemResetDroneTargets, "ConstructionSystem",
            "ResetDroneTargets", AuthorityHookRole.NativeConstruction, false, "BuildDispatch"),
        // the client-side write surface. Host runs, client refused outside a replica apply.
        // Required=false so the frozen contract (15 required hooks) is untouched: the vanilla bodies
        // must still run on the host and in single-player, so a missing target must not stop the
        // multiplayer by itself — the audit test pins the classification instead. The enforcement lives in
        // the patches named by each label's owner card.
        new(AuthorityHookLabels.PowerSystemGameTick, "PowerSystem",
            "GameTick", AuthorityHookRole.NativeSimulation, false, "Factory"),
        new(AuthorityHookLabels.UIMechaWindowOnReplaceFuelButtonClick, "UIMechaWindow",
            "OnReplaceFuelButtonClick", AuthorityHookRole.HostRule, false, "I01/A10"),
        new(AuthorityHookLabels.GameHistoryDataUnlockTechFunction, "GameHistoryData",
            "UnlockTechFunction", AuthorityHookRole.HostRule, false, "I01/A10"),
        new(AuthorityHookLabels.EnemyDFGroundSystemGameTickLogicUnit, "EnemyDFGroundSystem",
            "GameTickLogic_Unit", AuthorityHookRole.NativeSimulation, false, "Enemies"),
        new(AuthorityHookLabels.GameLogicEnemyGroundUnitParallel, "GameLogic",
            "_enemy_ground_unit_parallel", AuthorityHookRole.NativeSimulation, false, "Enemies"),
        new("DefenseSystem.GameTick_Turret", "DefenseSystem", "GameTick_Turret", AuthorityHookRole.NativeSimulation, false, "Turrets"),
        new("GameLogic._defense_system_turret_parallel", "GameLogic", "_defense_system_turret_parallel", AuthorityHookRole.NativeSimulation, false, "Turrets"),
        new("TurretComponent.PostGameTick", "TurretComponent", "PostGameTick", AuthorityHookRole.NativeSimulation, false, "Turrets"),
        new("TurretComponent.Search", "TurretComponent", "Search", AuthorityHookRole.NativeSimulation, false, "Turrets"),
        new(AuthorityHookLabels.EnemyDFHiveSystemGameTickLogic, "EnemyDFHiveSystem",
            "GameTickLogic", AuthorityHookRole.NativeSimulation, false, "Enemies")
    ];
}
