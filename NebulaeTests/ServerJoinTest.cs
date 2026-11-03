using HarmonyLib;
using NebulaeModel;
using NebulaePatcher.UI;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class ServerJoinTest
{
    private static string? joinedAddress;
    private static string? joinedPassword;
    private static string? joinedRecordId;

    [TestMethod]
    [DataRow("")]
    [DataRow("saved-password")]
    public void SavedServerJoinsWithoutOpeningAnEditor(string password)
    {
        var folder = Path.Combine(Path.GetTempPath(), "nebulae-server-join-test-" + Guid.NewGuid().ToString("N"));
        var storeField = AccessTools.Field(typeof(ServerMemoryStore), "instance");
        var selectionField = AccessTools.Field(typeof(MultiplayerPage), "selectedId");
        var previousStore = storeField.GetValue(null);
        var previousSelection = selectionField.GetValue(null);
        var harmony = new Harmony("nebulae.tests.saved-server-join");
        try
        {
            var store = new ServerMemoryStore(Path.Combine(folder, "servers.json"), Path.Combine(folder, "goals.json"));
            var server = store.SaveServer(null, "Home", "example.com:8469", password);
            storeField.SetValue(null, store);
            selectionField.SetValue(null, server.Id);
            joinedAddress = joinedPassword = joinedRecordId = null;
            harmony.Patch(AccessTools.Method(typeof(ServerJoinFlow), nameof(ServerJoinFlow.Join)),
                prefix: new HarmonyMethod(typeof(ServerJoinTest), nameof(CaptureJoin)));

            // No scene widgets exist: opening the editor instead of joining also fails this test.
            AccessTools.Method(typeof(MultiplayerPage), "JoinSelected").Invoke(null, null);

            TestAssert.AreEqual(server.Address, joinedAddress);
            TestAssert.AreEqual(password, joinedPassword);
            TestAssert.AreEqual(server.Id, joinedRecordId);
        }
        finally
        {
            harmony.UnpatchSelf();
            storeField.SetValue(null, previousStore);
            selectionField.SetValue(null, previousSelection);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private static bool CaptureJoin(string address, string password, string recordId)
    {
        joinedAddress = address;
        joinedPassword = password;
        joinedRecordId = recordId;
        return false;
    }
}
