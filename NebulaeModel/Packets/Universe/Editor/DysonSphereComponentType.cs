#region

#endregion

namespace NebulaeModel.Packets.Universe.Editor;

/// <summary>
///     Which kind of Dyson sphere component a colour or geometry change applies to.
/// </summary>
internal enum DysonSphereComponentType : byte
{
    Node,
    Frame,
    Shell
}
