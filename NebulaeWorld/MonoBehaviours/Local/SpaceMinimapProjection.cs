#region

using UnityEngine;

#endregion

namespace NebulaeWorld.MonoBehaviours.Local;

/// <summary>
///     Pure projection math for the remote player markers of the space minimap.
/// </summary>
internal static class SpaceMinimapProjection
{
    // UIMinimap3DControl.scaleRatio while flying in space: minimap scene units per universal unit.
    internal const double SceneScale = 2.5e-6;

    /// <summary>
    ///     Offset of a universal position from the local player in the minimap scene. While in space
    ///     the local player's dot sits at the scene origin, so this is the whole mapping and it stays
    ///     valid in interstellar space, where the game has no local star to measure against.
    /// </summary>
    internal static Vector3 SceneOffset(VectorLF3 universal, VectorLF3 playerUniversal) =>
        (Vector3)((universal - playerUniversal) * SceneScale);
}
