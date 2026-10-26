#region

using NebulaeModel.Authority;

#endregion

namespace NebulaeWorld.Authority;

/// <summary>Connects world-write permissions to the live session and replica application window.</summary>
internal static class AuthorityGuardWiring
{
    private static bool installed;

    /// <summary>
    /// Registers the session probe once.
    /// </summary>
    public static void Install()
    {
        if (installed) return;
        installed = true;
        AuthorityRuleGuard.Probe = ReadSession;
    }

    /// <summary>
    /// Reads the guard's view of the session: whether a world epoch exists, which
    /// side this is, and whether the frame boundary currently has a replica apply open.
    /// </summary>
    /// <remarks>
    /// A missing or disposed session is inactive, so single-player runs the vanilla path.
    /// </remarks>
    private static AuthorityGuardSession ReadSession()
    {
        var session = Multiplayer.Session;
        var identity = session?.Authority;
        if (identity is null)
        {
            return AuthorityGuardSession.Inactive;
        }

        var applyActive = !identity.IsHost &&
                          (session.AuthorityRuntime?.ApplyContext?.IsActiveOnCurrentThread ?? false);
        return new AuthorityGuardSession(identity.IsActive, identity.IsHost, applyActive);
    }
}
