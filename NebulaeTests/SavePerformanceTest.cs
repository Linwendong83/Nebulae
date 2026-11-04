using NebulaeWorld.GameStates;
using HarmonyLib;
using NebulaeWorld;
using Check = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests;

[TestClass]
public class SavePerformanceTest
{
    private static int pauseMessages;
    private static bool Pause(bool paused)
    {
        pauseMessages++;
        Multiplayer.Session.World.SetWorldPause(paused, paused ? GameMain.gameTick : 0);
        return false;
    }

    [TestMethod]
    public void NestedAndFailedSavesReleaseOnlyTheirOwnPauseAndExcludeSaveTimeFromHostRate()
    {
        var previousSession = Multiplayer.Session; var previousGame = GameMain.instance; var previousData = GameMain.data;
        // HostSaveScope only brackets a save the host is allowed to make; the fixture has no save
        // data loaded, so mark the server data as loaded for the duration of the test.
        var loadedField = AccessTools.Field(typeof(SaveManager), "serverDataLoaded");
        var previousLoaded = loadedField.GetValue(null);
        var harmony = new Harmony("nebulae.tests.save-boundary");
        try
        {
            loadedField.SetValue(null, true);
            var game = Uninitialized<GameMain>();
            AccessTools.Field(typeof(GameMain), "m_CachedPtr").SetValue(game, new IntPtr(1));
            SetStatic(typeof(GameMain), "<instance>k__BackingField", game);
            GameMain.data = Uninitialized<GameData>(); GameMain.gameTick = 100;
            var session = Multiplayer.Session = Uninitialized<MultiplayerSession>();
            session.Server = Uninitialized<global::NebulaeNetwork.Server>();
            session.World = Uninitialized<SimulatedWorld>(); session.IsGameLoaded = true;
            double now = 0;
            session.State = new GameStatesManager { ClockTime = () => now };
            harmony.Patch(AccessTools.Method(typeof(SimulatedWorld), nameof(SimulatedWorld.HostSetWorldPause)),
                prefix: new HarmonyMethod(typeof(SavePerformanceTest), nameof(Pause)));
            pauseMessages = 0;
            session.State.ObserveHostTick(100);
            Check.Throws<IOException>(() =>
            {
                using var outer = HostSaveScope.Begin();
                using (HostSaveScope.Begin()) { Check.IsTrue(session.WorldPaused); Check.AreEqual(100L, session.WorldPauseStepTick); }
                Check.IsTrue(session.WorldPaused); Check.AreEqual(1, pauseMessages);
                now = 4; throw new IOException("Injected save failure");
            });
            Check.IsFalse(session.WorldPaused); Check.IsFalse(GameMain.isFullscreenPaused); Check.AreEqual(2, pauseMessages);
            session.State.ObserveHostTick(100); now = 5; session.State.ObserveHostTick(143);
            Check.AreEqual(43f, GameStatesManager.RealUPS);
            session.World.SetWorldPause(true, 145);
            using (HostSaveScope.Begin()) { }
            Check.IsTrue(session.WorldPaused); Check.AreEqual(145L, session.WorldPauseStepTick);
            Check.AreEqual(2, pauseMessages, "A player's existing pause must not be resumed by a save.");
        }
        finally
        {
            harmony.UnpatchSelf(); Multiplayer.Session = previousSession;
            loadedField.SetValue(null, previousLoaded);
            SetStatic(typeof(GameMain), "<instance>k__BackingField", previousGame); GameMain.data = previousData;
        }
    }

    [TestMethod]
    public void NativeWriterCanBePatchedAndFloatingPointPayloadsAreByteIdentical()
    {
        var harmony = new Harmony("nebulae.tests.save-writer");
        try { harmony.CreateClassProcessor(typeof(SaveStream_Patch)).Patch(); harmony.CreateClassProcessor(typeof(SaveBoundary_Patch)).Patch(); }
        finally { harmony.UnpatchSelf(); }
        using var expected = new MemoryStream(); using var actual = new MemoryStream();
        using var native = new BinaryWriter(expected); using var fast = new SaveBinaryWriter(actual);
        var random = new Random(182);
        var bits = new byte[8];
        for (var i = 0; i < 20000; i++)
        {
            random.NextBytes(bits);
            var single = BitConverter.ToSingle(bits, 0); var number = BitConverter.ToDouble(bits, 0);
            native.Write(single); fast.Write(single); native.Write(number); fast.Write(number);
        }
        native.Write(-0f); fast.Write(-0f); native.Write(-0d); fast.Write(-0d);
        CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
    }

    [TestMethod]
    public void BufferedFilePreservesNativeHeaderSeekLengthAndLargeWrites()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dsv");
        try
        {
            using var expected = new MemoryStream();
            var random = new Random(83); var data = new byte[2500000]; random.NextBytes(data);
            using (var stream = new SaveWriteStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                for (var i = 0; i < 3; i++)
                {
                    stream.WriteByte(13); expected.WriteByte(13);
                    stream.Write(data, 17, 250000); expected.Write(data, 17, 250000);
                    stream.Write(data, 0, data.Length); expected.Write(data, 0, data.Length);
                }
                Check.AreEqual(expected.Position, stream.Position);
                Check.AreEqual(expected.Length, stream.Length);
                stream.Seek(6, SeekOrigin.Begin); expected.Seek(6, SeekOrigin.Begin);
                var length = BitConverter.GetBytes(stream.Length);
                stream.Write(length, 0, length.Length); expected.Write(length, 0, length.Length);
                stream.Seek(-3, SeekOrigin.End); expected.Seek(-3, SeekOrigin.End);
                stream.WriteByte(77); expected.WriteByte(77);
                stream.Flush(true);
            }
            CollectionAssert.AreEqual(expected.ToArray(), File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }
}
