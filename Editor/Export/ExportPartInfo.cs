using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// One `part-NNNN.zip` as the header names it (spec §2). The byte count and the sha256 are what
    /// make "received 6 of 7 parts" a sentence the hosted side can SAY — a single truncated zip
    /// reads as a small game, which is the failure this whole format exists to prevent.
    /// </summary>
    public sealed class ExportPartInfo
    {
        public int Index;
        public string Name = "";
        public long Bytes;
        /// <summary>Lowercase hex, 64 chars.</summary>
        public string Sha256 = "";
        public int Entries;

        public JObject ToJson()
        {
            return new JObject
            {
                ["index"] = Index,
                ["name"] = Name,
                ["bytes"] = Bytes,
                ["sha256"] = Sha256,
                ["entries"] = Entries,
            };
        }

        public static ExportPartInfo FromJson(JObject? o)
        {
            return new ExportPartInfo
            {
                Index = ExportJson.Int(o, "index"),
                Name = ExportJson.Str(o, "name"),
                Bytes = ExportJson.Long(o, "bytes"),
                Sha256 = ExportJson.Str(o, "sha256"),
                Entries = ExportJson.Int(o, "entries"),
            };
        }
    }
}
