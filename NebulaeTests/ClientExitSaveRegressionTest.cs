using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeWorld;
using NebulaeWorld.Authority.Adapters;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class ClientExitSaveRegressionTest
{
    private static string peer = "";
    private static string failure = "";
    private static bool reachedEnd;

    [TestMethod]
    [DataRow("client", "none")]
    [DataRow("client", "end")]
    [DataRow("client", "dispose")]
    [DataRow("host", "none")]
    public void ExitKeepsOriginalRoleAfterSessionDisappearsAndClearsGuardOnFailure(string mode, string failAt)
    {
        var previousSession = Multiplayer.Session;
        var previousRoot = UIRoot.instance;
        var previousLeaving = Multiplayer.IsLeavingGame;
        var previousMenu = Multiplayer.IsInMultiplayerMenu;
        var harmony = new Harmony("nebulae.tests.client-exit-save");
        try
        {
            peer = mode; failure = failAt; reachedEnd = false;
            var session = Uninitialized<MultiplayerSession>();
            session.IsGameLoaded = true;
            session.LocalPlayer = new LocalPlayer { IsHost = mode == "host" };
            if (mode == "client") session.Client = Uninitialized<global::NebulaeNetwork.Client>();
            else session.Server = Uninitialized<global::NebulaeNetwork.Server>();
            Multiplayer.Session = session;
            SetStatic(typeof(UIRoot), "_instance", Uninitialized<UIRoot>());
            harmony.Patch(AccessTools.Method(typeof(MultiplayerSession), nameof(MultiplayerSession.Dispose)),
                prefix: new HarmonyMethod(typeof(ClientExitSaveRegressionTest), nameof(DisposeBoundary)));
            harmony.Patch(AccessTools.Method(typeof(DSPGame), nameof(DSPGame.EndGame)),
                prefix: new HarmonyMethod(typeof(ClientExitSaveRegressionTest), nameof(EndBoundary)));
            harmony.CreateClassProcessor(typeof(GameSave_Patch)).Patch();
            if (failAt == "end") Check.ThrowsExactly<InvalidOperationException>(Multiplayer.LeaveGame);
            else Multiplayer.LeaveGame();
            Check.IsTrue(reachedEnd, "Disposal failures must still allow the game to return to the menu.");
            Check.IsNull(Multiplayer.Session);
            Check.IsFalse(Multiplayer.SuppressClientWorldSaves);
            Multiplayer.Session = null;
            Check.IsTrue(GameSave_Patch.SaveCurrentGame_Prefix(), "Subsequent single-player saves must remain enabled.");
            Check.IsTrue(GameSave_Patch.AutoSaveAfterErrored_Prefix());
        }
        finally
        {
            harmony.UnpatchSelf();
            Multiplayer.Session = previousSession;
            Multiplayer.IsLeavingGame = previousLeaving;
            Multiplayer.IsInMultiplayerMenu = previousMenu;
            SetStatic(typeof(UIRoot), "_instance", previousRoot);
        }
    }

    private static bool DisposeBoundary()
    {
        Check.AreEqual(peer == "client", Multiplayer.SuppressClientWorldSaves);
        if (peer == "client")
        {
            Multiplayer.Session.LocalPlayer = null;
            Check.IsFalse(GameSave.SaveCurrentGame("nebulae-regression-must-not-write"));
        }
        if (failure == "dispose") throw new InvalidOperationException("Simulated disposal failure");
        return false;
    }

    private static bool EndBoundary()
    {
        reachedEnd = true;
        Check.IsNull(Multiplayer.Session);
        Check.AreEqual(peer == "client", Multiplayer.SuppressClientWorldSaves);
        Check.AreEqual(peer != "client", GameSave_Patch.SaveCurrentGame_Prefix());
        Check.AreEqual(peer != "client", GameSave_Patch.AutoSaveAfterErrored_Prefix());
        if (peer == "client")
        {
            Check.IsFalse(GameSave.AutoSaveAfterErrored(), "Original emergency save must be skipped without writing files.");
            Check.IsFalse(GameSave.SaveCurrentGame("nebulae-regression-must-not-write"));
        }
        if (failure == "end") throw new InvalidOperationException("Simulated teardown failure");
        return false;
    }

    [TestMethod]
    public void RebuiltGroundSearchBuildersSupportNativeExportAndKeepTheirEnemyIds()
    {
        var factory = Uninitialized<PlanetFactory>();
        var planet = new PlanetData { id = 101, factory = factory };
        SetBackingField(factory, "planet", planet);
        factory.enemyPool = new EnemyData[4]; factory.enemyCursor = 4;
        factory.enemyPool[1] = new EnemyData { id = 1, owner = 1 };
        factory.enemyPool[2] = new EnemyData { id = 2, owner = 1, dynamic = true };
        factory.enemyPool[3] = new EnemyData { id = 3, owner = 1 };
        factory.enemySystem = Uninitialized<EnemyDFGroundSystem>();
        var bases = factory.enemySystem.bases = new ObjectPool<DFGBaseComponent>();
        bases.Reset();
        var component = bases.Add();
        component.InitFormations(); component.InitIncomingSkills();
        var epoch = new AuthorityEpoch(1, 2);
        var replica = new ClientWorldReplica(epoch);
        var scope = new ScopeKey(PoolKind.GroundEnemy, 101);
        replica.Subscribe(scope);
        var members = new List<SnapshotMemberRecord> { new(ObjectKey.Create(epoch, scope.Kind, 101, 1, 1), 1, null) };
        Check.IsTrue(AuthoritySnapshotCodec.TryEncode(new FrozenScopeSnapshot(scope, 1, 1, 0, 0, members), out var image));
        var hash = AuthoritySnapshotCodec.Hash(image);
        Check.IsTrue(replica.BeginSnapshot(scope, 1, 1, 1, image.Length, hash));
        Check.IsTrue(replica.ReceiveSnapshotChunk(scope, 1, 1, 0, image));
        Check.IsTrue(replica.CommitSnapshot(scope, 1, 1, 1, hash));
        DarkFogReplicaBinding.RebuildSearchIndex(factory, replica);
        Check.HasCount(1, component.pbuilders);
        Check.AreEqual(1, component.pbuilders[0].instId);
        Check.IsEmpty(component.pbuilders[0].connections);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        component.Export(writer);
        writer.Flush(); stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var restored = new DFGBaseComponent();
        restored.Import(reader);
        Check.AreEqual(stream.Length, stream.Position);
        Check.AreEqual(1, restored.pbuilders[0].instId);
        Check.IsEmpty(restored.pbuilders[0].connections);
    }
}
