using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>Command/result JSON envelopes. Command: {id, action, args}. Result: {id, ok, data, error}.</summary>
    public static class RelayEnvelope
    {
        public sealed class Command
        {
            public string Id { get; }
            public string Action { get; }
            public JObject Args { get; }

            public Command(string id, string action, JObject args)
            {
                Id = id;
                Action = action;
                Args = args;
            }
        }

        /// <summary>The filename-derived id is canonical; an id inside the JSON is ignored.
        /// Returns null on malformed JSON or a missing action.</summary>
        public static Command? ParseCommand(string id, string json)
        {
            try
            {
                var o = JObject.Parse(json);
                var action = o["action"]?.Value<string>();
                if (string.IsNullOrEmpty(action))
                    return null;
                return new Command(id, action!, o["args"] as JObject ?? new JObject());
            }
            catch
            {
                return null;
            }
        }

        public static string SerializeResult(string id, bool ok, JObject? data, string? error)
        {
            return new JObject
            {
                ["id"] = id,
                ["ok"] = ok,
                ["data"] = data ?? (JToken)JValue.CreateNull(),
                ["error"] = error == null ? JValue.CreateNull() : new JValue(error),
            }.ToString();
        }
    }
}
