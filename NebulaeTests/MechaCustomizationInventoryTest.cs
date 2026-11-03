using HarmonyLib;
using System.Reflection.Emit;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Combat.Mecha;
using NebulaeModel.Packets.Players;
using NebulaeNetwork.PacketProcessors.Players;
using NebulaeWorld;
using NebulaeWorld.Combat;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class MechaCustomizationInventoryTest
{
    private GameData previousGame = null!;
    private MultiplayerSession previousSession = null!;
    private UIRoot previousRoot = null!;
    private bool previousLoaded;
    private int[] previousStacks = null!, previousMaterials = null!;
    private Harmony harmony = null!;
    private Player player = null!;

    [TestInitialize]
    public void Initialize()
    {
        previousGame = GameMain.data; previousSession = Multiplayer.Session;
        previousRoot = UIRoot.instance;
        previousLoaded = StorageComponent.staticLoaded; previousStacks = StorageComponent.itemStackCount;
        previousMaterials = ItemProto.mechaMaterials;
        StorageComponent.staticLoaded = true;
        StorageComponent.itemStackCount = Enumerable.Repeat(100, 12000).ToArray();
        ItemProto.mechaMaterials = [1001, 1201];
        harmony = new Harmony("nebulae.tests.mecha-customization-inventory");
        harmony.Patch(AccessTools.Method(typeof(MechaMaterial), nameof(MechaMaterial.SetProto)),
            prefix: new HarmonyMethod(typeof(MechaCustomizationInventoryTest), nameof(MaterialAssets)));
        harmony.Patch(AccessTools.Method(typeof(CombatModuleComponent), "Setup", [typeof(int), typeof(GameData)]),
            prefix: new HarmonyMethod(typeof(MechaCustomizationInventoryTest), nameof(SkipUI)));
        harmony.Patch(AccessTools.Method(typeof(UIMechaMatsGroup), nameof(UIMechaMatsGroup.UpdateStorageItems)),
            prefix: new HarmonyMethod(typeof(MechaCustomizationInventoryTest), nameof(SkipUI)));
        foreach (var method in new[] { nameof(UIMechaMatsGroup.OnTransferHalfClick), nameof(UIMechaMatsGroup.OnTransferAllClick) })
            harmony.Patch(AccessTools.Method(typeof(UIMechaMatsGroup), method),
                transpiler: new HarmonyMethod(typeof(MechaCustomizationInventoryTest), nameof(WithoutAudio)),
                postfix: new HarmonyMethod(typeof(UIMechaMatsGroup_Patch), nameof(UIMechaMatsGroup_Patch.Transfer_Postfix)));
        GameMain.data = Uninitialized<GameData>();
        player = Uninitialized<Player>();
        SetBackingField(GameMain.data, nameof(GameData.mainPlayer), player);
        var delivery = new DeliveryPackage(); delivery.Init();
        SetBackingField(player, nameof(Player.deliveryPackage), delivery);
        SetBackingField(player, nameof(Player.package), new StorageComponent(3));
        var mecha = new Mecha
        {
            player = player,
            reactorStorage = new StorageComponent(4),
            warpStorage = new StorageComponent(1),
            ammoStorage = new StorageComponent(3),
            bombStorage = new StorageComponent(1),
            fighterStorage = new StorageComponent(5),
            forge = new MechaForge { tasks = [], extraItems = new ItemBundle() },
            constructionModule = new ConstructionModuleComponent(),
            groundCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] },
            spaceCombatModule = new CombatModuleComponent { moduleFleets = [], moduleFleetPoses = [] },
            appearance = new MechaAppearance(),
            diyAppearance = new MechaAppearance(),
            diyItems = new ItemBundle()
        };
        mecha.appearance.Init(); mecha.diyAppearance.Init();
        mecha.appearance.description = "applied"; mecha.diyAppearance.description = "unfinished draft";
        SetBackingField(player, nameof(Player.mecha), mecha);
        player.package.AddItemStacked(1001, 5, 17, out _);
        player.package.AddItemStacked(1201, 8, 16, out _);
        player.package.AddItemStacked(2000, 7, 0, out _);
        Multiplayer.Session = Uninitialized<MultiplayerSession>();
        Multiplayer.Session.Life = new PlayerLifeManager();
    }

    [TestCleanup]
    public void Cleanup()
    {
        harmony.UnpatchSelf();
        GameMain.data = previousGame; Multiplayer.Session = previousSession;
        SetStatic(typeof(UIRoot), "_instance", previousRoot);
        StorageComponent.staticLoaded = previousLoaded; StorageComponent.itemStackCount = previousStacks;
        ItemProto.mechaMaterials = previousMaterials;
    }

    private static bool SkipUI() => false;
    private static bool MaterialAssets(MechaMaterial __instance) { __instance.colors = []; return false; }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter",
        Justification = "The audio stub keeps the native call signature for the transfer-method transpiler.")]
    private static VFAudio SilentAudio(string name, Transform transform, Vector3 position, bool play, int time, int clip, long id) => null!;
    private static IEnumerable<CodeInstruction> WithoutAudio(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var code in instructions)
        {
            if (code.operand is System.Reflection.MethodInfo method && method.DeclaringType == typeof(VFAudio) && method.Name == nameof(VFAudio.Create))
            { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(MechaCustomizationInventoryTest), nameof(SilentAudio)); }
            yield return code;
        }
    }

    [TestMethod]
    [DataRow(false, 0, 3, 4)]
    [DataRow(true, 0, 5, 8)]
    [DataRow(false, 1001, 3, 0)]
    [DataRow(true, 1001, 5, 0)]
    public void NativeHalfAndAllTransfersSurviveCheckpointAndRejoinBeforeClosingEditor(bool all, int selected, int iron, int other)
    {
        var ui = Uninitialized<UIMechaMatsGroup>();
        ui.mechaEditor = Uninitialized<UIMechaEditor>();
        ui.mechaEditor.player = player; ui.mechaEditor.mecha = player.mecha;
        ui.selectedItemId = selected;
        if (all) ui.OnTransferAllClick(); else ui.OnTransferHalfClick();
        TestAssert.IsTrue(Multiplayer.Session.Life.BlocksInventory, "Each transfer must request a personal checkpoint.");
        TestAssert.AreEqual(iron, player.mecha.diyItems.GetCount(1001));
        TestAssert.AreEqual(other, player.mecha.diyItems.GetCount(1201));
        TestAssert.AreEqual(5 - iron, player.package.GetItemCount(1001));
        TestAssert.AreEqual(8 - other, player.package.GetItemCount(1201));
        TestAssert.AreEqual(7, player.package.GetItemCount(2000), "Unrelated backpack items stay in the backpack.");

        var local = new PlayerData(2, 101) { Mecha = new MechaData(player), Life = new PlayerLifeData { Revision = 10 } };
        var snapshot = local.CreatePersonalSnapshot(player.mecha);
        var stored = new PlayerData(2, 101);
        TestAssert.AreEqual(CheckpointReceiptStatus.Accepted,
            PlayerLifeManager.StoreServerResult(stored, SerializePlayer(snapshot), out _));
        var restored = new PlayerData(); restored.Deserialize(new NetDataReader(SerializePlayer(stored)));
        TestAssert.AreEqual(5, restored.Mecha.Inventory.GetItemCount(1001) + SavedCount(restored, 1001));
        TestAssert.AreEqual(8, restored.Mecha.Inventory.GetItemCount(1201) + SavedCount(restored, 1201));
        TestAssert.AreEqual(iron, SavedCount(restored, 1001));
        TestAssert.AreEqual(other, SavedCount(restored, 1201));
        TestAssert.AreEqual("unfinished draft", restored.DIYAppearance.description);
        TestAssert.AreEqual("applied", restored.Appearance.description);
        // Capturing a snapshot must not retain the mutable material dictionary.
        player.mecha.diyItems.Alter(1001, 100);
        TestAssert.AreEqual(iron, SavedCount(snapshot, 1001));
    }

    [TestMethod]
    public void RestoringAnEmptyMaterialStoreClearsThePreviousSessionsItems()
    {
        player.mecha.diyItems.Alter(1001, 100);
        SetStatic(typeof(UIRoot), "_instance", Uninitialized<UIRoot>());
        using var writer = new BinaryUtils.Writer();
        player.mecha.diyAppearance.Export(writer.BinaryWriter);
        var packet = new PlayerMechaDIYArmor(writer.CloseAndGetBytes(), [], []);
        AccessTools.Method(typeof(PlayerMechaDIYArmorProcessor), "ProcessPacket").Invoke(
            new PlayerMechaDIYArmorProcessor(), [packet, null!]);
        TestAssert.IsEmpty(player.mecha.diyItems.items);
    }

    [TestMethod]
    public void DuplicateOrOlderCheckpointCannotRestoreMaterialsAlreadySpent()
    {
        player.mecha.diyItems.Alter(1001, 5);
        var local = new PlayerData(2, 101) { Mecha = new MechaData(player), Life = new PlayerLifeData { Revision = 10 } };
        var before = SerializePlayer(local.CreatePersonalSnapshot(player.mecha));
        player.mecha.diyItems.Clear();
        local.Life = new PlayerLifeData { Revision = 11 };
        var after = SerializePlayer(local.CreatePersonalSnapshot(player.mecha));
        var stored = new PlayerData(2, 101);
        TestAssert.AreEqual(CheckpointReceiptStatus.Accepted, PlayerLifeManager.StoreServerResult(stored, after, out _));
        TestAssert.AreEqual(CheckpointReceiptStatus.Duplicate, PlayerLifeManager.StoreServerResult(stored, before, out _));
        TestAssert.AreEqual(CheckpointReceiptStatus.Duplicate, PlayerLifeManager.StoreServerResult(stored, after, out _));
        TestAssert.IsEmpty(stored.DIYItemId);
    }

    [TestMethod]
    [DataRow(300000, 200000, true, 350000)]
    [DataRow(300000, 0, true, 0)]
    [DataRow(300000, 200000, false, 200000)]
    [DataRow(75000, 200000, true, 100000)]
    public void ArmorChangesPreserveDamageAndResearchWhileReconnectKeepsSavedHp(int durability, int hp, bool ratio, int expected)
    {
        var mecha = player.mecha;
        mecha.history = Uninitialized<GameHistoryData>();
        mecha.history.globalHpEnhancement = 1;
        mecha.hpMax = 150000;
        mecha.hpMaxUpgrade = 50000;
        mecha.hp = hp;
        MechaCustomizationProperties.Apply(mecha, new MechaArmorPropertyDesc { durability = durability, consumption = 1.7 }, ratio);
        TestAssert.AreEqual(durability, mecha.hpMax);
        TestAssert.AreEqual(expected, mecha.hp);
        TestAssert.AreEqual(50000, mecha.hpMaxUpgrade);
        TestAssert.AreEqual(1.7, mecha.energyConsumptionCoef);
        // Replaying the same appearance cannot heal the player or repeatedly scale HP.
        MechaCustomizationProperties.Apply(mecha, new MechaArmorPropertyDesc { durability = durability, consumption = 1.7 }, true);
        TestAssert.AreEqual(expected, mecha.hp);
    }

    [TestMethod]
    public void ReceivedArmorRecalculatesPropertiesFromTheAppliedAppearance()
    {
        harmony.Patch(AccessTools.Method(typeof(MechaAppearance), nameof(MechaAppearance.CalculateProperty)),
            prefix: new HarmonyMethod(typeof(MechaCustomizationInventoryTest), nameof(CalculateTestArmor)));
        player.mecha.history = Uninitialized<GameHistoryData>();
        player.mecha.history.globalHpEnhancement = 0;
        player.mecha.hpMax = 150000; player.mecha.hp = 75000;
        var armor = Uninitialized<MechaArmorModel>(); armor.boneModels = [];
        SetBackingField(player, nameof(Player.mechaArmorModel), armor);
        player.mecha.appearance.partHideMask = 1;
        MechaCustomizationProperties.Refresh(player, preserveRatio: true);
        TestAssert.AreEqual(300000, player.mecha.hpMax);
        TestAssert.AreEqual(150000, player.mecha.hp);
        TestAssert.AreEqual(1.5, player.mecha.energyConsumptionCoef);
    }

    private static bool CalculateTestArmor(MechaAppearance __instance, MechaArmorPropertyDesc property)
    {
        property.durability = __instance.partHideMask == 1 ? 300000 : 150000;
        property.consumption = 1.5;
        return false;
    }

    [TestMethod]
    public void EveryMaterialTransferButtonRequestsTheSameCheckpoint()
    {
        var targets = DeclaredPatches(typeof(UIMechaMatsGroup_Patch)).Select(patch => patch.Target?.Name).ToArray();
        CollectionAssert.AreEquivalent(new[] { nameof(UIMechaMatsGroup.OnTransferHalfClick),
            nameof(UIMechaMatsGroup.OnTransferAllClick), nameof(UIMechaMatsGroup.OnTransferItemClick) }, targets);
    }

    private static int SavedCount(PlayerData data, int item)
    {
        var index = Array.IndexOf(data.DIYItemId, item);
        return index < 0 ? 0 : data.DIYItemValue[index];
    }
}
