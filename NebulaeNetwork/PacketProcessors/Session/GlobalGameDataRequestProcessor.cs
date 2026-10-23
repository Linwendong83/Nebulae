#region

using System.Collections.Generic;
using NebulaeModel.Networking;
using NebulaeModel.Packets;
using NebulaeModel.Packets.GameStates;
using NebulaeModel.Packets.Session;
using NebulaeWorld.GameStates;

#endregion

namespace NebulaeNetwork.PacketProcessors.Session;
internal class GlobalGameDataRequestProcessor : PacketProcessor<GlobalGameDataRequest>
{
    protected override void ProcessPacket(GlobalGameDataRequest packet, NebulaeConnection conn)
    {
        if (IsClient)
        {
            return;
        }
        var joining = Players.Get(conn, NebulaeModel.Networking.EConnectionStatus.Syncing);
        if (joining == null) return;
        JoinSyncManager.WaitForSimulation();
        NebulaeWorld.Multiplayer.Session.Ships.FlushLifecycle();
        NebulaeWorld.Multiplayer.Session.JoinSync.Begin(conn, new NebulaeModel.DataStructures.PlayerTechBonuses(GameMain.mainPlayer.mecha));
        var messages = new List<byte[]>();
        void AddSnapshot<T>(T value) where T : class, new() => messages.Add(conn.SerializePacket(value));
        AddSnapshot(new GlobalGameDataResponse(GlobalGameDataResponse.EDataType.PlanetBaseline,
            NebulaeWorld.Multiplayer.Session.Planets.ExportFactoryPresence()));

        using (var writer = new BinaryUtils.Writer())
        {
            writer.BinaryWriter.Write(NebulaeWorld.SaveManager.WorldId);
            writer.BinaryWriter.Write(GameMain.galaxy.birthStarId);
            writer.BinaryWriter.Write(GameMain.galaxy.birthPlanetId);
            writer.BinaryWriter.Write((int)GameMain.data.gameDesc.goalLevel);
            writer.BinaryWriter.Write(NebulaeWorld.Multiplayer.Session.Metadata.SourceClusterKey);
            writer.BinaryWriter.Write(GameMain.gameTick);
            AddSnapshot(new GlobalGameDataResponse(GlobalGameDataResponse.EDataType.Session, writer.CloseAndGetBytes()));
        }

        AddSnapshot(new GlobalGameDataResponse(GlobalGameDataResponse.EDataType.Goals,
            NebulaeWorld.Multiplayer.Session.Goals.Export()));
        AddSnapshot(new NebulaeModel.Packets.Combat.CombatGenerationPacket
        { Data = NebulaeWorld.Multiplayer.Session.Generations.Export() });
        AddSnapshot(new GlobalGameDataResponse(GlobalGameDataResponse.EDataType.KillStatistics,
            NebulaeWorld.Multiplayer.Session.Kills.ExportSnapshot(joining.Id)));

        //Export GameHistoryData, SpaceSector, TrashSystem, MilestoneSystem
        //PlanetFactory, Dysonsphere, GalacticTransport will be handle else where        

        using (var writer = new BinaryUtils.Writer())
        {
            GameMain.history.Export(writer.BinaryWriter);

            AddSnapshot(new GlobalGameDataResponse(
                GlobalGameDataResponse.EDataType.History, writer.CloseAndGetBytes()));
        }

        using (var writer = new BinaryUtils.Writer())
        {
            GameMain.data.galacticTransport.Export(writer.BinaryWriter);

            AddSnapshot(new GlobalGameDataResponse(
                GlobalGameDataResponse.EDataType.GalacticTransport, writer.CloseAndGetBytes()));
        }

        using (var writer = new BinaryUtils.Writer())
        {
            SpaceSectorBootstrap.Export(GameMain.data.spaceSector, writer.BinaryWriter);

            AddSnapshot(new GlobalGameDataResponse(
                GlobalGameDataResponse.EDataType.SpaceSector, writer.CloseAndGetBytes()));
        }

        using (var writer = new BinaryUtils.Writer())
        {
            GameMain.data.milestoneSystem.Export(writer.BinaryWriter);

            AddSnapshot(new GlobalGameDataResponse(
                GlobalGameDataResponse.EDataType.MilestoneSystem, writer.CloseAndGetBytes()));
        }

        using (var writer = new BinaryUtils.Writer())
        {
            GameMain.data.trashSystem.Export(writer.BinaryWriter);

            AddSnapshot(new GlobalGameDataResponse(
                GlobalGameDataResponse.EDataType.TrashSystem, writer.CloseAndGetBytes()));
        }

        using (var writer = new BinaryUtils.Writer())
        {
            GameMain.data.galacticDigital.Export(writer.BinaryWriter);

            AddSnapshot(new GlobalGameDataResponse(
                GlobalGameDataResponse.EDataType.GalacticDigital, writer.CloseAndGetBytes()));
        }

        NebulaeWorld.Multiplayer.Session.JoinSync.Checkpoint(conn, 0);
        using (var writer = new BinaryUtils.Writer())
        {
            writer.BinaryWriter.Write(GameMain.sandboxToolsEnabled);

            AddSnapshot(new GlobalGameDataResponse(
                GlobalGameDataResponse.EDataType.Ready, writer.CloseAndGetBytes()));
        }

        conn.SendJoinTransfer(JoinDataStage.World, 0, messages.ToArray());
        conn.SendPacket(new GameStateSaveInfoPacket(GameStatesManager.LastSaveTime));
        conn.SendPacket(new GamePausePacket(NebulaeWorld.Multiplayer.Session.WorldPaused, NebulaeWorld.Multiplayer.Session.WorldPauseStepTick));
        conn.SendPacket(new SimSpeedPacket(NebulaeWorld.Multiplayer.Session.WorldSimSpeed));
    }

}
