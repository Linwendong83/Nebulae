using System;
using System.Collections.Generic;
using System.Reflection;

namespace NebulaeModel.Authority;

/// <summary>Hook labels shared with transformation checks and frame-boundary tests.</summary>
internal static class AuthorityHookLabels
{
    public const string SkillSystemDamageObject = "SkillSystem.DamageObject";
    public const string SkillSystemDamageGroundObjectByLocalCaster = "SkillSystem.DamageGroundObjectByLocalCaster";
    public const string SkillSystemDamageGroundObjectByRemoteCaster = "SkillSystem.DamageGroundObjectByRemoteCaster";
    public const string GameLogicLogicFrame = "GameLogic.LogicFrame";
    public const string ThreadManagerProcessFrame = "ThreadManager.ProcessFrame";
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

/// <summary>One native method that must resolve before multiplayer starts.</summary>
internal readonly struct AuthorityHookPolicy
{
    public AuthorityHookPolicy(string typeName, string methodName)
    {
        TypeName = typeName;
        MethodName = methodName;
    }

    public string Label => TypeName + "." + MethodName;
    public string TypeName { get; }
    public string MethodName { get; }
}

/// <summary>Permits host rule execution and validated client mirror writes.</summary>
internal static class AuthorityRuleGuard
{
    private static readonly object gate = new();

    private static Func<AuthorityGuardSession> probe;
    private static readonly List<Func<IReadOnlyList<string>>> extraVerifiers = [];
    private static readonly Dictionary<string, Type> resolvedTypes = [];
    private static int loadedAssemblyCount = -1;
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

    /// <summary>True once every required hook and verifier passed.</summary>
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

    /// <summary>Native hooks required by startup verification.</summary>
    public static IReadOnlyList<AuthorityHookPolicy> RequiredPolicy { get; } = Array.AsReadOnly(new AuthorityHookPolicy[]
    {
        new("CombatStat", "TickSkillLogic"),
        new("CombatStat", "HandleFullHp"),
        new("CombatStat", "HandleZeroHp"),
        new("SkillSystem", "DamageObject"),
        new("SkillSystem", "DamageGroundObjectByLocalCaster"),
        new("SkillSystem", "DamageGroundObjectByRemoteCaster"),
        new("ConstructionSystem", "AddConstructStat"),
        new("ConstructionSystem", "RemoveConstructStat"),
        new("ConstructionSystem", "Repair"),
        new("ConstructionSystem", "DetermineLaunch"),
        new("ConstructionSystem", "UpdateModules"),
        new("ConstructionSystem", "UpdateDrones"),
        new("ConstructStat", "GameTick"),
        new("GameLogic", "LogicFrame"),
        new("ThreadManager", "ProcessFrame")
    });
}
