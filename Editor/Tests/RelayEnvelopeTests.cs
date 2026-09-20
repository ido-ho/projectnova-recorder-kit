using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit.Tests
{
    public class RelayEnvelopeTests
    {
        [Test]
        public void ParseCommand_ReadsActionAndArgs()
        {
            var cmd = RelayEnvelope.ParseCommand("abc", "{\"id\":\"abc\",\"action\":\"run-cheat\",\"args\":{\"command\":\"SetCoins 5\"}}");
            Assert.IsNotNull(cmd);
            Assert.AreEqual("abc", cmd!.Id);
            Assert.AreEqual("run-cheat", cmd.Action);
            Assert.AreEqual("SetCoins 5", cmd.Args["command"]!.Value<string>());
        }

        [Test]
        public void ParseCommand_MissingArgs_YieldsEmptyArgs()
        {
            var cmd = RelayEnvelope.ParseCommand("abc", "{\"action\":\"ping\"}");
            Assert.IsNotNull(cmd);
            Assert.AreEqual(0, cmd!.Args.Count);
        }

        [Test]
        public void ParseCommand_MalformedJson_ReturnsNull()
        {
            Assert.IsNull(RelayEnvelope.ParseCommand("abc", "{not json"));
            Assert.IsNull(RelayEnvelope.ParseCommand("abc", "{\"args\":{}}")); // no action
        }

        [Test]
        public void SerializeResult_RoundTrips()
        {
            var json = RelayEnvelope.SerializeResult("xyz", true, new JObject { ["pong"] = true }, null);
            var o = JObject.Parse(json);
            Assert.AreEqual("xyz", o["id"]!.Value<string>());
            Assert.IsTrue(o["ok"]!.Value<bool>());
            Assert.IsTrue(o["data"]!["pong"]!.Value<bool>());
            Assert.AreEqual(JTokenType.Null, o["error"]!.Type);
        }
    }
}
