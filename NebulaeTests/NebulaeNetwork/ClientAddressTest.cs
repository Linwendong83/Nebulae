using System.Net;
using NebulaeClient = NebulaeNetwork.Client;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaeTests.NebulaeNetwork;

[TestClass]
public class ClientAddressTest
{
    [TestMethod]
    public void ClientKeepsTheHostNameAndDoesNotResolveIt()
    {
        // ".invalid" is guaranteed to never resolve (RFC 2606): constructing the client must not
        // attempt a name lookup or throw a SocketException.
        var client = new NebulaeClient("nebulae-client-test.invalid", 8469, "wss");

        TestAssert.AreEqual("nebulae-client-test.invalid", client.ServerHost);
        TestAssert.AreEqual(8469, client.ServerPort);
        TestAssert.AreEqual("wss", client.ServerProtocol);
        TestAssert.AreEqual("wss://nebulae-client-test.invalid:8469/socket", client.SocketUrl);
    }

    [TestMethod]
    public void ClientDefaultsToWsAndKeepsThePort()
    {
        var client = new NebulaeClient("example.com", 9000, "");

        TestAssert.AreEqual("ws", client.ServerProtocol);
        TestAssert.AreEqual("ws://example.com:9000/socket", client.SocketUrl);
    }

    [TestMethod]
    public void ClientAcceptsIpLiteralEndpoints()
    {
        var client = new NebulaeClient(new IPEndPoint(IPAddress.Parse("::1"), 8469));

        TestAssert.AreEqual("::1", client.ServerHost);
        TestAssert.AreEqual(8469, client.ServerPort);
        TestAssert.AreEqual("ws", client.ServerProtocol);
        TestAssert.AreEqual("ws://[::1]:8469/socket", client.SocketUrl);
    }
}
