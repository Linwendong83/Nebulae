using HarmonyLib;
using NebulaeModel.Authority;
using NebulaeModel.Packets.Authority;
using NebulaeModel.Packets.Factory.Assembler;
using NebulaeModel.Utils;
using NebulaeWorld;
using NebulaeWorld.Authority;
using NebulaeWorld.Factory;
using NebulaeWorld.Player;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class FighterReceiptNotificationTest
{
    private GameData previousGame = null!;
    private MultiplayerSession previousSession = null!;
    private int[] previousStacks = null!;
    private bool previousLoaded;
    private Harmony harmony = null!;
    private Player player = null!;
    private FactoryProductManager manager = null!;
    private static readonly List<(int Item, int Count)> tips = [];
    private readonly List<(int Item, int Count)> notifications = [];

    [TestInitialize]
    public void Setup()
    {
        previousGame = GameMain.data; previousSession = Multiplayer.Session;
        previousStacks = StorageComponent.itemStackCount; previousLoaded = StorageComponent.staticLoaded;
        StorageComponent.staticLoaded = true;
        StorageComponent.itemStackCount = Enumerable.Repeat(100, 12000).ToArray();
        var game = Uninitialized<GameData>();
        player = Uninitialized<Player>();
        player.SetHiddenProperty(nameof(Player.package), new StorageComponent(2));
        game.SetHiddenProperty(nameof(GameData.mainPlayer), player);
        GameMain.data = game;
        player.onPackageAddItem += (item, count, _) => notifications.Add((item, count));
        var session = Uninitialized<MultiplayerSession>();
        session.Authority = new AuthoritySessionState();
        session.Authority.BeginAuthorityWorld(new AuthorityEpoch(1, 2), false);
        session.Authority.SetConnection(new ConnectionEpoch(3), 2);
        session.InventoryTransfers = new PlayerInventoryTransfers();
        Multiplayer.Session = session;
        manager = new FactoryProductManager();
        manager.TickClient(); // Initialize the actual receipt handler's world/connection gate.
        harmony = new Harmony("nebulae.tests.fighter-receipt-notifications");
        // Record the HUD call in the real receipt handler; Unity UI objects need a game process.
        harmony.Patch(AccessTools.Method(typeof(FactoryProductManager), nameof(FactoryProductManager.TickClient)),
            transpiler: new HarmonyMethod(typeof(FighterReceiptNotificationTest), nameof(CaptureTips)));
        tips.Clear(); notifications.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
        harmony?.UnpatchSelf();
        GameMain.data = previousGame; Multiplayer.Session = previousSession;
        StorageComponent.itemStackCount = previousStacks; StorageComponent.staticLoaded = previousLoaded;
    }

    private static void CaptureTip(int itemId, int upCount) => tips.Add((itemId, upCount));
    private static IEnumerable<CodeInstruction> CaptureTips(IEnumerable<CodeInstruction> instructions)
    {
        var up = AccessTools.Method(typeof(UIItemup), nameof(UIItemup.Up));
        foreach (var code in instructions)
        {
            if (code.Calls(up)) code.operand = AccessTools.Method(typeof(FighterReceiptNotificationTest), nameof(CaptureTip));
            yield return code;
        }
    }

    private void Receive(bool hand, CommandResultCode code, int count)
    {
        AccessTools.Field(typeof(FactoryProductManager), "pending").SetValue(manager, 1L);
        AccessTools.Field(typeof(FactoryProductManager), "activeRequest").SetValue(manager,
            (default(ObjectKey), new FactoryProductIntent(FactoryInteractionKind.FighterPort, -6, 0, 1001, 20, 40)));
        manager.ReceiveResult(new AuthorityCommandResultPacket
        {
            EpochHigh = 1,
            EpochLow = 2,
            ConnectionEpoch = 3,
            Sequence = 1,
            ResultCode = (byte)code,
            ResourceRevision = 1,
            ResultData = FactoryInventoryReceipt.Encode([new PlayerInventoryTransferLedger.Transfer(1001, count, count * 2, hand)])
        });
    }

    [TestMethod]
    [DataRow((int)CommandResultCode.Applied, 19)]
    [DataRow((int)CommandResultCode.RejectedResource, 20)]
    public void HandRemainderAndRefusedGestureDoNotAnnouncePackageGains(int code, int count)
    {
        Receive(true, (CommandResultCode)code, count);
        manager.TickClient(); manager.TickClient();
        TestAssert.AreEqual(1001, player.inhandItemId);
        TestAssert.AreEqual(count, player.inhandItemCount);
        TestAssert.AreEqual(count * 2, player.inhandItemInc);
        TestAssert.AreEqual(0, player.package.GetItemCount(1001));
        TestAssert.IsFalse(manager.InventoryPending);
        TestAssert.IsEmpty(notifications, "Returning an escrowed hand is not a package-add event.");
        TestAssert.IsEmpty(tips, "A fighter gesture must not display the returned hand as newly acquired items.");
    }

    [TestMethod]
    public void ARealPackageCreditStillNotifiesOnce()
    {
        Receive(false, CommandResultCode.Applied, 3);
        manager.TickClient(); manager.TickClient();
        TestAssert.AreEqual(3, player.package.GetItemCount(1001));
        TestAssert.AreEqual(0, player.inhandItemCount);
        TestAssert.HasCount(1, tips); TestAssert.AreEqual((1001, 3), tips[0]);
        TestAssert.HasCount(1, notifications); TestAssert.AreEqual((1001, 3), notifications[0]);
    }

    [TestMethod]
    public void BusyHandRetriesWithoutFalseNotificationsOrDuplicateItems()
    {
        player.SetHandItemId_Unsafe(1002); player.SetHandItemCount_Unsafe(1);
        Receive(true, CommandResultCode.Applied, 19);
        manager.TickClient();
        TestAssert.IsTrue(manager.InventoryPending);
        TestAssert.AreEqual(1002, player.inhandItemId); TestAssert.AreEqual(1, player.inhandItemCount);
        TestAssert.IsEmpty(tips); TestAssert.IsEmpty(notifications);
        player.SetHandItemId_Unsafe(0); player.SetHandItemCount_Unsafe(0);
        manager.TickClient(); manager.TickClient();
        TestAssert.IsFalse(manager.InventoryPending);
        TestAssert.AreEqual(1001, player.inhandItemId); TestAssert.AreEqual(19, player.inhandItemCount);
        TestAssert.IsEmpty(tips); TestAssert.IsEmpty(notifications);
    }

    private static T Uninitialized<T>() => (T)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(T));
}
