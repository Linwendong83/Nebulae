using System;
using NebulaeModel.Authority;

namespace NebulaePatcher.Patches.Authority;

/// <summary>Validates world-write guards before multiplayer can start.</summary>
internal static class AuthorityStartup
{
    public static void Validate(Func<bool> verifyLoadOnce)
    {
        if (verifyLoadOnce == null) throw new ArgumentNullException(nameof(verifyLoadOnce));
        if (!verifyLoadOnce())
        {
            throw new InvalidOperationException("Multiplayer world-write guards are unavailable: " +
                                                AuthorityRuleGuard.LoadFailure);
        }
    }

    /// <summary>A supported game build cannot override a failed guard verification.</summary>
    public static bool CanStartMultiplayer(bool supportedGameBuild) =>
        supportedGameBuild && AuthorityRuleGuard.IsLoadVerified;
}
