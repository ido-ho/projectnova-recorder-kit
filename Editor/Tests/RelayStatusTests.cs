using System.IO;
using NUnit.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor.Compilation;

namespace ProjectNova.RecorderKit.Tests
{
    public class RelayStatusTests
    {
        // JObject.Parse defaults to DateParseHandling.DateTime, which silently reinterprets any
        // ISO-8601-looking JSON string (e.g. heartbeatUtc) as a DateTime and reformats it
        // (culture-specific) on read-back, corrupting the exact string BuildStatusJson wrote. A
        // real client just does JSON.parse on the raw bytes and never sees this; it's purely a
        // .NET-side JObject.Parse default-settings landmine. Parse with DateParseHandling.None so
        // these round-trip assertions see the literal string BuildStatusJson produced.
        private static JObject ParseStatus(string json)
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            return (JObject)JToken.ReadFrom(reader);
        }

        [Test]
        public void BuildStatusJson_CarriesIdentityAndPlayState()
        {
            var json = RelayStatus.BuildStatusJson("boot123", true, null, 0, "snl", "2026-07-27T00:00:00Z");
            var o = ParseStatus(json);
            Assert.AreEqual("boot123", o["bootId"]!.Value<string>());
            Assert.IsTrue(o["isPlaying"]!.Value<bool>());
            Assert.AreEqual(JTokenType.Null, o["compileErrors"]!.Type);
            Assert.AreEqual(KitInfo.Version, o["kitVersion"]!.Value<string>());
            Assert.AreEqual("snl", o["gameId"]!.Value<string>());
            Assert.AreEqual("2026-07-27T00:00:00Z", o["heartbeatUtc"]!.Value<string>());
        }

        [Test]
        public void BuildStatusJson_ListsCompileErrors()
        {
            var json = RelayStatus.BuildStatusJson("b", false,
                new[] { "Assets/X.cs(1,1): error CS0000: nope" }, 3, "snl", "t");
            var o = ParseStatus(json);
            Assert.AreEqual(1, ((JArray)o["compileErrors"]!).Count);
        }

        // The client dates same-bootId compile errors by this counter, so it has to reach the wire
        // as a number under exactly this key — a rename or a stringified value silently reopens the
        // stale-error race that made `relay recompile` misreport a successful fix.
        [Test]
        public void BuildStatusJson_CarriesCompileGenerationAsANumber()
        {
            var o = ParseStatus(RelayStatus.BuildStatusJson("b", false, null, 7, "snl", "t"));
            Assert.AreEqual(JTokenType.Integer, o["compileGeneration"]!.Type);
            Assert.AreEqual(7, o["compileGeneration"]!.Value<int>());
        }

        [Test]
        public void ErrorMessages_KeepsOnlyErrors()
        {
            var messages = new[]
            {
                new CompilerMessage { message = "warn", type = CompilerMessageType.Warning },
                new CompilerMessage { message = "Assets/X.cs(1,1): error CS0103", type = CompilerMessageType.Error },
            };
            var errors = RelayStatus.ErrorMessages(messages);
            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains("CS0103", errors[0]);
        }
    }
}
