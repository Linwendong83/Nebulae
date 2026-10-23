using System.Runtime.CompilerServices;
using HarmonyLib;

namespace NebulaeWorld.Player;

internal static class VegetationAccess
{
    private static readonly AccessTools.FieldRef<global::Player, VegetableCollection> Raw =
        AccessTools.FieldRefAccess<global::Player, VegetableCollection>("<vegetableCollection>k__BackingField");
    public static VegetableCollection LocalCollection(global::Player player) => player == null ? null : Raw(player);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static VegetableCollection Collection(global::Player player)
    {
        if (player == null) return null;
        var session = Multiplayer.Session;
        var manager = session?.Vegetation;
        if (manager == null || player != GameMain.mainPlayer) return Raw(player);
        if (!session.IsServer && manager.ReplayCollection != null) return manager.ReplayCollection;
        var author = session.Factories.PacketAuthor;
        if (author > 0 && author <= ushort.MaxValue && author != session.LocalPlayer.Id)
            return session.IsServer ? manager.GetRemote((ushort)author) ?? Raw(player) : manager.GetReplicaCollection((ushort)author);
        return Raw(player);
    }
}
