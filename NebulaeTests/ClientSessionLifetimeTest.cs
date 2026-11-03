using HarmonyLib;
using NebulaeModel.Utils;
using NebulaeNetwork;
using NebulaeWorld;
using NebulaeWorld.GameStates;
using WebSocketSharp;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass, DoNotParallelize]
public class ClientSessionLifetimeTest
{
    [TestMethod]
    public void DelayedCloseFromPreviousClientCannotCancelTheNewJoin()
    {
        var previous = Multiplayer.Session;
        var oldClient = new Client("localhost", 8469, "ws");
        var newClient = new Client("localhost", 8469, "ws");
        var loadingCloses = 0;
        var joining = new JoinSyncManager { CloseLoading = () => loadingCloses++ };
        var session = Uninitialized<MultiplayerSession>();
        session.Client = newClient;
        session.IsGameLoaded = true;
        AccessTools.Field(typeof(MultiplayerSession), "<JoinSync>k__BackingField").SetValue(session, joining);
        joining.BeginLocal(); joining.MarkLoaded();
        try
        {
            Multiplayer.Session = session;
            var closed = (CloseEventArgs)Activator.CreateInstance(typeof(CloseEventArgs),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, new object[] { (ushort)2001, "Player left the game", true }, null);
            AccessTools.Method(typeof(Client), "ClientSocket_OnClose").Invoke(oldClient, new object?[] { null, closed });
            ((MainThreadActionQueue)AccessTools.Field(typeof(UnityDispatchQueue), "actionsQueue").GetValue(null)).Drain();
            Check.IsTrue(joining.CanSimulate, "A retired client's callback must not cancel the replacement session.");
            Check.AreEqual(0, loadingCloses);
        }
        finally { Multiplayer.Session = previous; joining.Dispose(); }
    }
}
