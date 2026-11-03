using HarmonyLib;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class RestorationPatchCompatibilityTest
{
    [TestMethod]
    public void HiddenGamePropertiesUsedByRemotePlayersHaveSetters()
    {
        foreach (var (type, name) in new[]
                 {
                     (typeof(Player), nameof(Player.transform)),
                     (typeof(Player), nameof(Player.mecha)),
                     (typeof(Player), nameof(Player.animator)),
                     (typeof(Player), nameof(Player.controller)),
                     (typeof(Player), nameof(Player.cameraTarget)),
                     (typeof(Player), nameof(Player.vegetableCollection)),
                     (typeof(MechaForge), nameof(MechaForge.mecha)),
                     (typeof(ManualBehaviour), nameof(ManualBehaviour.data))
                 })
        {
            var property = AccessTools.Property(type, name);
            TestAssert.IsNotNull(property, type.Name + "." + name);
            TestAssert.IsNotNull(property.GetSetMethod(true), type.Name + "." + name);
        }
        var armor = Uninitialized<MechaArmorModel>();
        var player = new Player();
        global::NebulaeModel.Utils.NativeGameAccess.SetHiddenProperty(armor, nameof(ManualBehaviour.data), player);
        TestAssert.AreSame(player, armor.data);
    }

    [TestMethod]
    public void NativeTrashPoolUsesZeroAsItsFirstValidIdentifier()
    {
        var pool = Uninitialized<TrashContainer>();
        pool.trashObjPool = new TrashObject[8];
        pool.trashDataPool = new TrashData[8];
        pool.trashRecycle = new int[8];
        AccessTools.Field(typeof(TrashContainer), "trashCapacity").SetValue(pool, 8);
        var item = new TrashObject { item = 1101, count = 37 };
        TestAssert.AreEqual(0, pool.NewTrash(item, new TrashData()));
        TestAssert.AreEqual(37, pool.trashObjPool[0].count);
    }
}
