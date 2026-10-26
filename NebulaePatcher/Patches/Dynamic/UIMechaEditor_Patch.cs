#region

using HarmonyLib;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Players;
using NebulaeWorld;

#endregion

namespace NebulaePatcher.Patches.Dynamic;

[HarmonyPatch(typeof(UIMechaEditor))]
internal class UIMechaEditor_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMechaEditor.ApplyMechaAppearance))]
    public static void ApplyMechaAppearance_Postfix()
    {
        if (!Multiplayer.IsActive)
        {
            return;
        }
        using var writer = new BinaryUtils.Writer();
        GameMain.mainPlayer.mecha.appearance.Export(writer.BinaryWriter);
        Multiplayer.Session.Network.SendPacket(new PlayerMechaArmor(Multiplayer.Session.LocalPlayer.Id,
            writer.CloseAndGetBytes()));
        Multiplayer.Session.Life.Publish();
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMechaEditor._OnClose))]
    public static void OnClose_Postfix()
    {
        if (!Multiplayer.IsActive || !Multiplayer.Session.LocalPlayer.IsClient)
        {
            return;
        }
        // Persist the draft and its materials together with the debited package.
        Multiplayer.Session.Life.Publish();
    }
}

[HarmonyPatch(typeof(UIMechaMatsGroup))]
internal static class UIMechaMatsGroup_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UIMechaMatsGroup.OnTransferItemClick))]
    [HarmonyPatch(nameof(UIMechaMatsGroup.OnTransferHalfClick))]
    [HarmonyPatch(nameof(UIMechaMatsGroup.OnTransferAllClick))]
    public static void Transfer_Postfix()
    {
        if (Multiplayer.IsActive) Multiplayer.Session.Life.Publish();
    }
}
