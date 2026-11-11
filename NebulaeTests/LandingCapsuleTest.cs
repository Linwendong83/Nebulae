using HarmonyLib;
using NebulaeWorld;
using NebulaeWorld.Factory;
using NebulaeWorld.Planet;
using UnityEngine;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class LandingCapsuleTest
{
    [TestMethod]
    public void TickZeroReloadDoesNotCreateASecondCapsuleAndClientsCannotCreateOne()
    {
        var previous = Multiplayer.Session;
        var factory = Factory();
        var mission = Uninitialized<GuideMissionStandardMode>();
        mission.localPlanet = new PlanetData { factory = factory };
        var session = Uninitialized<MultiplayerSession>();
        session.Server = Uninitialized<global::NebulaeNetwork.Server>();
        try
        {
            Multiplayer.Session = session;
            Check.IsTrue(GuideMissionStandardMode_Patch.CreateSpaceCapsuleVegetable_Prefix(mission));
            factory.vegePool[1] = Capsule(1, Vector3.up * 200);
            Check.IsFalse(GuideMissionStandardMode_Patch.CreateSpaceCapsuleVegetable_Prefix(mission));
            session.Server = null!;
            factory.vegePool[1] = default;
            Check.IsFalse(GuideMissionStandardMode_Patch.CreateSpaceCapsuleVegetable_Prefix(mission));
            Multiplayer.Session = null!;
            Check.IsTrue(GuideMissionStandardMode_Patch.CreateSpaceCapsuleVegetable_Prefix(mission));
        }
        finally { Multiplayer.Session = previous; }
    }

    [TestMethod]
    public void LegacyOverlapsAreRemovedOnceWithoutHarvestingOrDeletingOtherVegetation()
    {
        var previous = Multiplayer.Session;
        var factory = Factory();
        factory.vegePool[1] = Capsule(1, Vector3.up * 200);
        factory.vegePool[2] = Capsule(2, Vector3.up * 200);
        factory.vegePool[3] = Capsule(3, Vector3.up * 200); factory.vegePool[3].protoId = 1001;
        factory.vegePool[4] = Capsule(4, Vector3.right * 200);
        var session = Uninitialized<MultiplayerSession>(); session.Planets = new PlanetManager();
        var patches = new Harmony("nebulae.tests.capsule-cleanup");
        removed = [];
        patches.Patch(AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.RemoveVegeWithComponents)),
            prefix: new HarmonyMethod(typeof(LandingCapsuleTest), nameof(RemoveWithoutUnity)));
        try
        {
            Multiplayer.Session = session;
            Check.AreEqual(1, LandingCapsule.RepairDuplicates(factory));
            Check.AreEqual(0, LandingCapsule.RepairDuplicates(factory));
            CollectionAssert.AreEqual(new[] { 2 }, removed.ToArray());
            Check.AreEqual(1, factory.vegePool[1].id); Check.AreEqual(3, factory.vegePool[3].id);
            Check.AreEqual(4, factory.vegePool[4].id);
            Check.IsFalse(session.Planets.IsIncomingRequest.Value);
            Check.IsFalse(LandingCapsule.IsRepairing);
            Check.IsTrue(LandingCapsuleRemoval_Patch.Prefix());
        }
        finally { patches.UnpatchSelf(); Multiplayer.Session = previous; }
    }
    private static List<int> removed = [];
    private static bool RemoveWithoutUnity(PlanetFactory __instance, int id)
    {
        Check.IsTrue(Multiplayer.Session.Planets.IsIncomingRequest.Value);
        Check.IsTrue(LandingCapsule.IsRepairing);
        Check.IsFalse(LandingCapsuleRemoval_Patch.Prefix());
        removed.Add(id); __instance.vegePool[id] = default; return false;
    }
    private static PlanetFactory Factory()
    {
        var factory = Uninitialized<PlanetFactory>();
        SetBackingField(factory, nameof(PlanetFactory.planet), new PlanetData { id = 104 });
        factory.vegePool = new VegeData[5]; factory.vegeCursor = 5;
        return factory;
    }
    private static VegeData Capsule(int id, Vector3 pos) => new() { id = id, protoId = 9999, pos = pos };
}
