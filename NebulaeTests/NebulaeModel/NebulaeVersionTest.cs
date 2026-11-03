using PluginInfo = BepInEx.PluginInfo;
using BepInEx;
using NebulaeModel.Networking;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Packets.Session;
using NebulaeModel.Utils;
using NebulaePatcher;
using NebulaeNetwork.PacketProcessors.Session;
using ModelConfig = global::NebulaeModel.Config;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeModel;

[TestClass]
public class NebulaeVersionTest
{
    private PluginInfo? previousModInfo;
    private string previousVersion = "";

    [TestInitialize]
    public void RememberModInfo() { previousModInfo = ModelConfig.ModInfo; previousVersion = ModelConfig.ModVersion; }

    [TestCleanup]
    public void RestoreModInfo() { ModelConfig.ModInfo = previousModInfo!; ModelConfig.ModVersion = previousVersion; }

    private static NebulaePlugin Plugin() =>
        Uninitialized<NebulaePlugin>();

    private static PluginInfo Info(NebulaePlugin? plugin)
    {
        var info = Uninitialized<PluginInfo>();
        typeof(PluginInfo).GetProperty(nameof(PluginInfo.Instance))!.SetValue(info, plugin);
        typeof(PluginInfo).GetProperty(nameof(PluginInfo.Metadata))!.SetValue(info,
            new BepInPlugin("dsp.nebulae", "Nebulae", "9.9.9"));
        return info;
    }

    [TestMethod]
    public void UninitializedPluginVersionIsUnknownRatherThanABuildVersion()
    {
        ModelConfig.ModInfo = null!;
        TestAssert.AreEqual(string.Empty, ModelConfig.ModVersion);
        TestAssert.IsFalse(NebulaeVersion.IsCompatible(ModelConfig.ModVersion, string.Empty));
        TestAssert.IsFalse(NebulaeVersion.IsKnownStatusVersion(ModelConfig.ModVersion));

        ModelConfig.ModInfo = Info(null);
        TestAssert.AreEqual(string.Empty, ModelConfig.ModVersion);
    }

    [TestMethod]
    public void AdvertisedVersionComesFromTheHandshakePluginRatherThanNumericMetadata()
    {
        var plugin = Plugin();
        ModelConfig.ModInfo = Info(plugin);
        ModelConfig.ModVersion = global::NebulaePatcher.PluginInfo.PLUGIN_DISPLAY_VERSION;
        TestAssert.AreEqual(global::NebulaePatcher.PluginInfo.PLUGIN_DISPLAY_VERSION, ModelConfig.ModVersion);
        TestAssert.IsTrue(NebulaeVersion.IsKnownStatusVersion(ModelConfig.ModVersion));
        TestAssert.AreNotEqual(ModelConfig.ModInfo.Metadata.Version.ToString(), ModelConfig.ModVersion);
    }

    [TestMethod]
    [DataRow("1.0.0-dev.7+abcdef0", "1.0.0-dev.7+1234567", true)]
    [DataRow(" 1.0.0-DEV.7 +abcdef0 ", "1.0.0-dev.7", true)]
    [DataRow("1.0.0", "1.0.0+abcdef0", true)]
    [DataRow("1.0.0-dev.6", "1.0.0-dev.7", false)]
    [DataRow("1.0.1-dev.7", "1.0.0-dev.7", false)]
    [DataRow("1.0.0", "1.0.0-dev.7", false)]
    [DataRow("1.0.0-dev.7", "1.0.0", false)]
    [DataRow("1.0.0-dev.7", "1.0.0-dev.70", false)]
    [DataRow(null, null, false)]
    [DataRow("", "", false)]
    [DataRow(" ", "+abcdef0", false)]
    [DataRow("1.0.0-dev.7", null, false)]
    public void ServerListComparisonAndActualHandshakeAgree(string host, string client, bool compatible)
    {
        TestAssert.AreEqual(compatible, NebulaeVersion.IsCompatible(host, client));
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow(" ", false)]
    [DataRow("+abcdef0", false)]
    [DataRow("1.0.0+abcdef0", true)]
    [DataRow("1.0.0.123+abcdef0", true)]
    [DataRow("1.0.0-dev.7", true)]
    [DataRow(" 1.0.0-DEV.7+abcdef0 ", true)]
    [DataRow("1.0.0", true)]
    public void EveryNonemptyNormalizedModVersionCanBeDisplayed(string version, bool known)
    {
        TestAssert.AreEqual(known, NebulaeVersion.IsKnownStatusVersion(version));
    }

    [TestMethod]
    public void FullDevVersionRoundTripsThroughTheStatusPacket()
    {
        ModelConfig.ModInfo = Info(Plugin());
        ModelConfig.ModVersion = global::NebulaePatcher.PluginInfo.PLUGIN_DISPLAY_VERSION;
        var processor = new NebulaeNetPacketProcessor();
        ServerStatusResponse received = null!;
        processor.SubscribeReusable<ServerStatusResponse>(packet => received = packet);
        var sent = new ServerStatusResponse(3, true,
            NebulaeVersion.Normalize(ModelConfig.ModVersion), "Server description");
        var writer = new NetDataWriter();
        processor.Write(writer, sent);
        processor.ReadPacket(new NetDataReader(writer));

        TestAssert.AreEqual(sent.NebulaeVersion, received.NebulaeVersion);
        TestAssert.IsTrue(NebulaeVersion.IsCompatible(global::NebulaePatcher.PluginInfo.PLUGIN_DISPLAY_VERSION, received.NebulaeVersion));
        TestAssert.AreEqual(sent.NumPlayers, received.NumPlayers);
        TestAssert.AreEqual(sent.IsGameLoaded, received.IsGameLoaded);
        TestAssert.AreEqual(sent.Description, received.Description);
    }

    [TestMethod]
    public void LobbyDeclarationCarriesVersionAndCertificateDirectly()
    {
        ModelConfig.ModInfo = Info(Plugin());
        ModelConfig.ModVersion = "1.0.0-dev.7";
        var request = RoundTrip(new LobbyRequest(new byte[] { 1, 2, 3 }, "player"));
        TestAssert.AreEqual(ModelConfig.ModVersion, request.ModVersion);
        TestAssert.AreEqual("player", request.Username);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, request.ClientCert);
        TestAssert.IsTrue(LobbyRequestProcessor.VersionCheck(request, out var reason, out _));
        TestAssert.AreEqual(DisconnectionReason.Normal, reason);
        request.ModVersion = "1.0.0-dev.6";
        TestAssert.IsFalse(LobbyRequestProcessor.VersionCheck(request, out reason, out _));
        TestAssert.AreEqual(DisconnectionReason.ModVersionMismatch, reason);
        request.ModVersion = "";
        TestAssert.IsFalse(LobbyRequestProcessor.VersionCheck(request, out reason, out _));
        TestAssert.AreEqual(DisconnectionReason.InvalidData, reason);
        request.ModVersion = ModelConfig.ModVersion;
        request.ClientCert = Array.Empty<byte>();
        TestAssert.IsFalse(LobbyRequestProcessor.VersionCheck(request, out reason, out _));
        TestAssert.AreEqual(DisconnectionReason.InvalidData, reason);
    }

    [TestMethod]
    public void ChineseStatusMessagesShowTheServerVersionWithoutCommitMetadata()
    {
        const string mismatch = "Mod version mismatch: Server {0}";
        TestAssert.IsTrue(NebulaeLocalization.TryTranslate(mismatch, true, out var translated));
        TestAssert.AreEqual("模组版本不一致：服务器 1.0.0-dev.6",
            string.Format(translated, NebulaeVersion.Normalize("1.0.0-dev.6+abcdef0")));
        TestAssert.IsTrue(NebulaeLocalization.TryTranslate("Unable to determine mod version", true, out translated));
        TestAssert.AreEqual("无法确认模组版本", translated);
    }
}
