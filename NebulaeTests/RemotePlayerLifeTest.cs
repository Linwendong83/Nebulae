using HarmonyLib;
using System.Reflection.Emit;
using global::NebulaeModel.DataStructures;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Utils;
using NebulaeWorld;
using NebulaeWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class RemotePlayerLifeTest
{
    private static int kills, respawns, prepares;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CombatAndLifeDeliveryOrderCannotSkipModelTransitionsOrReplayADeath(bool combatFirst)
    {
        var previousGame = GameMain.data;
        var previousSession = Multiplayer.Session;
        var previousWreckages = MechaArmorModel.all_wreckages;
        var previousLoaded = StorageComponent.staticLoaded;
        var previousStacks = StorageComponent.itemStackCount;
        var harmony = new Harmony("nebulae.tests.remote-player-life");
        try
        {
            // Only Unity asset/rendering calls are replaced; native combat state and packet application run.
            harmony.Patch(AccessTools.Method(typeof(CombatModuleComponent), "Setup", [typeof(int), typeof(GameData)]),
                prefix: new HarmonyMethod(typeof(RemotePlayerLifeTest), nameof(SkipAssets)));
            harmony.Patch(AccessTools.Method(typeof(PlayerLifeManager), nameof(PlayerLifeManager.ApplyRemote)),
                transpiler: new HarmonyMethod(typeof(RemotePlayerLifeTest), nameof(WithoutRendering)));
            kills = respawns = prepares = 0;
            StorageComponent.staticLoaded = true;
            StorageComponent.itemStackCount = Enumerable.Repeat(100, 12000).ToArray();
            var game = Uninitialized<GameData>();
            GameMain.data = game;
            var player = Uninitialized<Player>();
            player.isAlive = true;
            var mecha = new Mecha
            {
                player = player,
                history = new GameHistoryData(),
                hp = 1000,
                hpMax = 1000,
                coreEnergyCap = 1000000,
                laserEnergyCapacity = 100000,
                ammoStorage = new StorageComponent(3),
                bombStorage = new StorageComponent(1),
                fighterStorage = new StorageComponent(5),
                groundCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] },
                spaceCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] }
            };
            player.SetHiddenProperty(nameof(Player.mecha), mecha);
            player.SetHiddenProperty(nameof(Player.mechaArmorModel), Uninitialized<MechaArmorModel>());
            var model = Uninitialized<RemotePlayerModel>();
            model.PlayerInstance = player;
            model.Life = new PlayerLifeData();
            model.Wreckages = [];
            SetBackingField(model, nameof(RemotePlayerModel.RespawnVisual), new RemoteRespawnVisualState());
            var session = Uninitialized<MultiplayerSession>();
            session.LocalPlayer = new LocalPlayer { Data = new PlayerData(1, 103) };
            session.World = new SimulatedWorld();
            Multiplayer.Session = session;
            using (session.World.GetRemotePlayersModels(out var models)) models[2] = model;
            var authority = new CombatAuthorityManager();
            var manager = new PlayerLifeManager();
            var life = new PlayerLifeData { IsAlive = false, DeathCount = 1, Revision = 1 };
            var dead = Packet(player, false, 1, 0, 0);
            if (combatFirst) TestAssert.IsTrue(authority.ApplyState(dead));
            manager.ApplyRemote(2, life);
            if (!combatFirst) TestAssert.IsTrue(authority.ApplyState(dead));
            TestAssert.AreEqual(1, kills);
            TestAssert.IsFalse(model.RespawnVisual.IsAlive);

            // Host life checkpoints may reuse the exact object held by the model.
            life.RespawnMode = 2; life.RespawnTick = 40; life.Revision = 2;
            manager.ApplyRemote(2, life);
            manager.ApplyRemote(2, life);
            TestAssert.AreEqual(1, prepares);
            TestAssert.AreEqual(40, model.RespawnVisual.Tick);

            var alive = Packet(player, true, 2, 725, 1234);
            if (combatFirst) TestAssert.IsTrue(authority.ApplyState(alive));
            life.IsAlive = true; life.Revision = 3;
            manager.ApplyRemote(2, life);
            manager.ApplyRemote(2, life);
            if (!combatFirst) TestAssert.IsTrue(authority.ApplyState(alive));
            TestAssert.AreEqual(1, respawns);
            TestAssert.IsEmpty(model.Wreckages);
            TestAssert.IsTrue(model.RespawnVisual.IsAlive);
            TestAssert.AreEqual(725, mecha.hp, "Rendering a respawn must preserve newer combat facts.");
            TestAssert.AreEqual(1234L, mecha.laserEnergy);

            dead.CombatRevision = 3;
            TestAssert.IsTrue(authority.ApplyState(dead));
            TestAssert.IsTrue(player.isAlive, "An old dead-life combat packet must not undo an already presented respawn.");
            TestAssert.AreEqual(725, mecha.hp);
            TestAssert.AreEqual(1234L, mecha.laserEnergy);
        }
        finally
        {
            harmony.UnpatchSelf();
            GameMain.data = previousGame;
            Multiplayer.Session = previousSession;
            MechaArmorModel.all_wreckages = previousWreckages;
            StorageComponent.staticLoaded = previousLoaded;
            StorageComponent.itemStackCount = previousStacks;
        }
    }
    private static IEnumerable<CodeInstruction> WithoutRendering(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            foreach (var name in new[] { nameof(MechaArmorModel.Kill), nameof(MechaArmorModel.Respawn),
                         nameof(MechaArmorModel.ClearWreckages), nameof(MechaArmorModel.PrepareRespawn) })
                if (instruction.Calls(AccessTools.Method(typeof(MechaArmorModel), name)))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(RemotePlayerLifeTest), name);
                    break;
                }
            yield return instruction;
        }
    }

    private static AuthorityPlayerCombatStatePacket Packet(Player player, bool alive, long revision, int hp, long laser)
    {
        var writer = new NetDataWriter();
        new MechaFightData(player) { Hp = hp, LaserEnergy = laser, LaserRecharging = false }.Serialize(writer);
        return new AuthorityPlayerCombatStatePacket
        {
            PlayerId = 2,
            ActorConnection = 1,
            CombatRevision = revision,
            DeathCount = 1,
            IsAlive = alive,
            CombatData = writer.CopyData()
        };
    }
    private static bool SkipAssets() => false;
    private static void Kill(MechaArmorModel _) { kills++; MechaArmorModel.all_wreckages.Add(null!); }
    private static void Respawn(MechaArmorModel _) { respawns++; }
    private static void PrepareRespawn(MechaArmorModel _) { prepares++; }
    private static void ClearWreckages(MechaArmorModel _) { MechaArmorModel.all_wreckages.Clear(); }
}
