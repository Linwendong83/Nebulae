#region

using System;
using NebulaeModel.Networking;
using NebulaeModel.Logger;
using NebulaeModel.Packets;
using NebulaeModel.Packets.Universe;
using NebulaeWorld;
using NebulaeWorld.GameStates;
using NebulaeWorld.Universe;
using NebulaeModel.Utils;

#endregion

namespace NebulaeNetwork.PacketProcessors.Universe;
internal class DysonSphereDataProcessor : PacketProcessor<DysonSphereData>
{
    protected override void ProcessPacket(DysonSphereData packet, NebulaeConnection conn)
    {
        if (IsHost)
        {
            return;
        }
        var manager = Multiplayer.Session.DysonSpheres;
        var spheres = GameMain.data?.dysonSpheres;
        if (spheres == null || packet.StarIndex < 0 || packet.StarIndex >= spheres.Length)
        {
            if (packet.Event is DysonSphereRespondEvent.Load or DysonSphereRespondEvent.Missing &&
                manager.FailRequest(packet.StarIndex, packet.RequestId))
            {
                if (manager.ClearLoadingNotice(packet.StarIndex)) InGamePopup.FadeOut();
                Log.Debug($"[dyson] discarded response without its world star={packet.StarIndex} request={packet.RequestId}; baseline can be requested again");
            }
            return;
        }

        switch (packet.Event)
        {
            case DysonSphereRespondEvent.List:
                //Overwrite content assigned by UIDETopFunction.SetDysonComboBox()
                var dysonBox = UIRoot.instance.uiGame.dysonEditor.controlPanel.topFunction.dysonBox;
                using (var br = new BinaryUtils.Reader(packet.BinaryData).BinaryReader)
                {
                    dysonBox.Items = [];
                    dysonBox.ItemsData = [];
                    var count = br.ReadInt32();
                    for (var i = 0; i < count; i++)
                    {
                        var starIndex = br.ReadInt32();
                        dysonBox.Items.Add(GameMain.galaxy.stars[starIndex].displayName);
                        dysonBox.ItemsData.Add(starIndex);
                    }
                }
                var index = dysonBox.ItemsData.FindIndex(x =>
                    x == UIRoot.instance.uiGame.dysonEditor.selection.viewStar?.index);
                dysonBox.itemIndex = index >= 0 ? index : 0;
                break;

            case DysonSphereRespondEvent.Load:
                if (!manager.Expects(packet.StarIndex, packet.RequestId)) return;
                // The whole fragment is received
                GameStatesManager.FragmentSize = 0;
                try { ImportSnapshot(packet); }
                catch
                {
                    if (manager.ClearLoadingNotice(packet.StarIndex)) InGamePopup.FadeOut();
                    throw;
                }
                var requestedView = manager.CompleteEditorRequest(packet.StarIndex, out var openEditor);
                var editor = UIRoot.instance?.uiGame?.dysonEditor;
                if (manager.ClearLoadingNotice(packet.StarIndex)) InGamePopup.FadeOut();
                if (openEditor && Multiplayer.Session.IsGameLoaded)
                    editor?._Open();
                else if (editor?.active == true && (requestedView || editor.selection.viewStar?.index == packet.StarIndex))
                {
                    editor.selection.SetViewStar(GameMain.galaxy.stars[packet.StarIndex]);
                    var dysonBox2 = editor.controlPanel.topFunction.dysonBox;
                    var index2 = dysonBox2.ItemsData.FindIndex(x =>
                        x == UIRoot.instance.uiGame.dysonEditor.selection.viewStar?.index);
                    dysonBox2.itemIndex = index2 >= 0 ? index2 : 0;
                }
                break;
            case DysonSphereRespondEvent.Missing:
                if (!manager.Expects(packet.StarIndex, packet.RequestId)) return;
                manager.FailRequest(packet.StarIndex, packet.RequestId);
                if (manager.ClearLoadingNotice(packet.StarIndex)) InGamePopup.FadeOut();
                break;
            case DysonSphereRespondEvent.Desync:
                Multiplayer.Session.DysonSpheres.HandleDesync(packet.StarIndex, conn);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(packet), "Unknown DysonSphereRespondEvent: " + packet.Event);
        }
    }

    internal static void ImportSnapshot(DysonSphereData packet)
    {
        var manager = Multiplayer.Session.DysonSpheres;
        if (!manager.Expects(packet.StarIndex, packet.RequestId)) return;
        var game = GameMain.data;
        var production = game.statistics.production;
        // Existing factory history and register references must survive a sphere refresh.
        if (production.factoryStatPool == null) production.Init(game);
        if (production.factoryStatPool.Length > 0 && production.factoryStatPool[0] == null)
        {
            production.factoryStatPool[0] = new FactoryProductionStat();
            production.factoryStatPool[0].Init();
        }
        var sphere = new DysonSphere();
        var committed = false;
        var previousBlueprint = manager.InBlueprint;
        manager.InBlueprint = true;
        using var incoming = manager.IsIncomingRequest.On();
        using var swarmIncoming = manager.IncomingDysonSwarmPacket.On();
        try
        {
            sphere.Init(game, game.galaxy.stars[packet.StarIndex]);
            using var reader = new BinaryUtils.Reader(packet.BinaryData, 3);
            sphere.Import(reader.BinaryReader);
            reader.RequireEnd();
            ValidateSwarm(sphere.swarm);
            committed = manager.Commit(packet.StarIndex, packet.RequestId, sphere, packet.SnapshotTick);
        }
        catch
        {
            manager.FailRequest(packet.StarIndex, packet.RequestId);
            throw;
        }
        finally
        {
            manager.InBlueprint = previousBlueprint;
            if (!committed && sphere.swarm != null)
            {
                DysonSphereManager.ReleaseSphere(sphere);
            }
        }
    }

    internal static void ValidateSwarm(DysonSwarm swarm)
    {
        SolarSailState.Validate(swarm);
    }
}
