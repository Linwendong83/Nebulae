using NebulaeModel.Contracts;
using NebulaeModel.Networking;

namespace NebulaeModel.DataStructures;
internal class PlayerLifeData : INetSerializable
{
    private static long lastRevision;
    public static string CurrentTransactionId { get; set; } = "";
    public static bool CurrentRedeployItemsDropped { get; set; }
    public static long NextRevision(long observed = 0)
    {
        lastRevision = System.Math.Max(System.Math.Max(lastRevision, observed) + 1, System.DateTime.UtcNow.Ticks);
        return lastRevision;
    }
    [NebulaeModel.Networking.WireOrder(0)]
    public bool IsAlive { get; set; } = true;
    [NebulaeModel.Networking.WireOrder(1)]
    public int DeathCount { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int TimeSinceKilled { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int InvincibleTicks { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int RespawnMode { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public int RespawnStage { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public int RespawnTick { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public int SelectedOption { get; set; } = -1;
    [NebulaeModel.Networking.WireOrder(8)]
    public long Revision { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public string TransactionId { get; set; } = "";
    [NebulaeModel.Networking.WireOrder(10)]
    public bool RedeployItemsDropped { get; set; }

    public static PlayerLifeData Capture(Player player, long revision = 0, string transactionId = "")
    {
        var action = player.controller.actionDeath;
        return new PlayerLifeData
        {
            IsAlive = player.isAlive,
            DeathCount = player.deathCount,
            TimeSinceKilled = player.timeSinceKilled,
            InvincibleTicks = player.invincibleTicks,
            RespawnMode = action.respawnMode,
            RespawnStage = action.respawnStage,
            RespawnTick = action.respawnTick,
            SelectedOption = action.selectedRespawnOption,
            Revision = revision == 0 ? NextRevision() : revision,
            TransactionId = string.IsNullOrEmpty(transactionId) ? CurrentTransactionId : transactionId,
            RedeployItemsDropped = CurrentRedeployItemsDropped
        };
    }

    public void Serialize(INetDataWriter writer)
    {
        writer.Put(IsAlive); writer.Put(DeathCount); writer.Put(TimeSinceKilled); writer.Put(InvincibleTicks);
        writer.Put(RespawnMode); writer.Put(RespawnStage); writer.Put(RespawnTick); writer.Put(SelectedOption);
        writer.Put(Revision); writer.Put(TransactionId ?? "");
        writer.Put(RedeployItemsDropped);
    }

    public void Deserialize(INetDataReader reader)
    {
        IsAlive = reader.GetBool(); DeathCount = reader.GetInt(); TimeSinceKilled = reader.GetInt();
        InvincibleTicks = reader.GetInt(); RespawnMode = reader.GetInt(); RespawnStage = reader.GetInt();
        RespawnTick = reader.GetInt(); SelectedOption = reader.GetInt(); Revision = reader.GetLong();
        TransactionId = reader.GetString();
        RedeployItemsDropped = reader.GetBool();
    }
}
