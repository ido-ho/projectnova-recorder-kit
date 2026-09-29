using System;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// The `caps` block of `header.json` (spec §2). Defaults are the numbers the spec pins; every
    /// field is settable so a test can force the boundary with tiny caps instead of building a
    /// 32 MiB fixture.
    ///
    /// The caps travel IN the header on purpose: the hosted side must be able to say "this export
    /// is smaller than the game because of THIS number", not guess.
    /// </summary>
    public sealed class ExportCaps
    {
        /// <summary>32 MiB — a hard ceiling for one part.</summary>
        public long PartMaxBytes = 33554432L;
        /// <summary>24 MiB — a part closes once it reaches this.</summary>
        public long PartSoftBytes = 25165824L;
        /// <summary>8 MiB — one original art/audio/font file.</summary>
        public long ArtFileMaxBytes = 8388608L;
        /// <summary>2 GiB — all of art/ together (thumbnails do NOT count against it). Raised from 1 GiB
        /// in kit 0.9.3: on Rogue Legend the art the game USES alone is over 1 GiB, so even used-first
        /// the cap cut ~500 of its own screens (owner, 2026-09-23: "fix the 2 GB limit first"). The
        /// API allows 256 parts x 32 MiB; the upload ran at ~14 MB/s.</summary>
        public long ArtTotalMaxBytes = 2147483648L;
        /// <summary>1 MiB — one `docs/` file. (Config has its own cap since 0.9.0.)</summary>
        public long DocFileMaxBytes = 1048576L;
        /// <summary>16 MiB — one `config/` file (spec §8.3). Config is text: RL's
        /// `remote_config_latest.json` is 4.66 MB and was skipped under the 1 MiB doc cap.</summary>
        public long ConfigFileMaxBytes = 16777216L;
        /// <summary>8 — `config/` files. With the 16 MiB file cap this bounds config at 128 MiB, so the
        /// worst valid export (200 × 1 MiB docs + 8 × 16 MiB config + 20 × 8 MiB strings ≈ 488 MiB of
        /// text) stays under the API's 768 MiB facts-extract ceiling (`FACTS_EXTRACT_LIMITS`) — without
        /// it, 200 × 16 MiB was a valid export the server could never read (consumer audit, §8.3).</summary>
        public int ConfigMaxFiles = 8;
        /// <summary>24 MiB — one AUDIO original (spec §8.3): a music track, kept under the part cap.</summary>
        public long AudioFileMaxBytes = 25165824L;
        /// <summary>8 MiB / 20 files — the game's source-language strings (spec §8.6).</summary>
        public long StringsFileMaxBytes = 8388608L;
        public int StringsMaxFiles = 20;
        /// <summary>50,000 — clickable UI rows (spec §8.7).</summary>
        public int UiRowsMax = 50000;
        /// <summary>Shared budget across `docs/**` AND `config/**`.</summary>
        public int DocMaxFiles = 200;
        public int ThumbnailPx = 256;
        public int ThumbnailMaxCount = 20000;
        public int JsonlShardRows = 20000;
        /// <summary>.cs files larger than this are not scanned (listed in `errors`).</summary>
        public long PatternFileMaxBytes = 1048576L;
        public int PatternHitsMax = 50000;

        /// <summary>
        /// NOT ON THE WIRE. Spec §2 states the 16 MiB raw-bytes shard rule in prose but gives it no
        /// field in the `caps` object, and the field names in that document ARE the wire — so this
        /// stays a C#-side knob (tests set it tiny) and <see cref="ToJson"/> emits exactly the
        /// documented caps (eleven, plus the five of spec §8.3).
        /// </summary>
        public long JsonlShardBytes = 16777216L;

        /// <summary>
        /// NOT ON THE WIRE either, for the same reason. The largest asset file whose TEXT the
        /// identity pass will read (colours, the TMP family name). Reading an 11 MB
        /// ScriptableObject's YAML is one long slice; past this the asset gets an `errors` row
        /// instead. It exists because the identity pass no longer LOADS the asset — loading it ran
        /// the studio's `OnEnable`/`OnValidate` (fresh-context audit K6, 2026-09-21).
        /// </summary>
        public long AssetTextMaxBytes = 8388608L;

        /// <summary>
        /// NOT ON THE WIRE either. How many BYTES of loaded source art may accumulate between two
        /// memory sweeps in the thumbnail pass.
        ///
        /// The count trigger alone is not a memory bound: 256 real 2048x2048 RGBA textures between
        /// sweeps is several GB, and "bounded by the sweep interval" was only ever true of the tiny
        /// fixtures it was measured on (fresh-context audit, 2026-09-21). The running estimate comes
        /// from <c>Profiler.GetRuntimeMemorySizeLong</c> on each source texture, and either trigger
        /// sweeps — see <see cref="ExportSweepPolicy"/>.
        /// </summary>
        public long ThumbnailSweepBytes = 512L * 1024L * 1024L;

        public JObject ToJson()
        {
            return new JObject
            {
                ["partMaxBytes"] = PartMaxBytes,
                ["partSoftBytes"] = PartSoftBytes,
                ["artFileMaxBytes"] = ArtFileMaxBytes,
                ["artTotalMaxBytes"] = ArtTotalMaxBytes,
                ["docFileMaxBytes"] = DocFileMaxBytes,
                ["docMaxFiles"] = DocMaxFiles,
                ["thumbnailPx"] = ThumbnailPx,
                ["thumbnailMaxCount"] = ThumbnailMaxCount,
                ["jsonlShardRows"] = JsonlShardRows,
                ["patternFileMaxBytes"] = PatternFileMaxBytes,
                ["patternHitsMax"] = PatternHitsMax,
                // spec §8.3 (kit 0.9.0)
                ["configFileMaxBytes"] = ConfigFileMaxBytes,
                ["configMaxFiles"] = ConfigMaxFiles,
                ["audioFileMaxBytes"] = AudioFileMaxBytes,
                ["stringsFileMaxBytes"] = StringsFileMaxBytes,
                ["stringsMaxFiles"] = StringsMaxFiles,
                ["uiRowsMax"] = UiRowsMax,
            };
        }

        /// <summary>Missing fields keep the default — an older exporter's header still reads.</summary>
        public static ExportCaps FromJson(JObject? o)
        {
            var c = new ExportCaps();
            if (o == null) return c;
            c.PartMaxBytes = ExportJson.Long(o, "partMaxBytes", c.PartMaxBytes);
            c.PartSoftBytes = ExportJson.Long(o, "partSoftBytes", c.PartSoftBytes);
            c.ArtFileMaxBytes = ExportJson.Long(o, "artFileMaxBytes", c.ArtFileMaxBytes);
            c.ArtTotalMaxBytes = ExportJson.Long(o, "artTotalMaxBytes", c.ArtTotalMaxBytes);
            c.DocFileMaxBytes = ExportJson.Long(o, "docFileMaxBytes", c.DocFileMaxBytes);
            c.DocMaxFiles = ExportJson.Int(o, "docMaxFiles", c.DocMaxFiles);
            c.ThumbnailPx = ExportJson.Int(o, "thumbnailPx", c.ThumbnailPx);
            c.ThumbnailMaxCount = ExportJson.Int(o, "thumbnailMaxCount", c.ThumbnailMaxCount);
            c.JsonlShardRows = ExportJson.Int(o, "jsonlShardRows", c.JsonlShardRows);
            c.PatternFileMaxBytes = ExportJson.Long(o, "patternFileMaxBytes", c.PatternFileMaxBytes);
            c.PatternHitsMax = ExportJson.Int(o, "patternHitsMax", c.PatternHitsMax);
            c.ConfigFileMaxBytes = ExportJson.Long(o, "configFileMaxBytes", c.ConfigFileMaxBytes);
            c.ConfigMaxFiles = (int)ExportJson.Long(o, "configMaxFiles", c.ConfigMaxFiles);
            c.AudioFileMaxBytes = ExportJson.Long(o, "audioFileMaxBytes", c.AudioFileMaxBytes);
            c.StringsFileMaxBytes = ExportJson.Long(o, "stringsFileMaxBytes", c.StringsFileMaxBytes);
            c.StringsMaxFiles = (int)ExportJson.Long(o, "stringsMaxFiles", c.StringsMaxFiles);
            c.UiRowsMax = (int)ExportJson.Long(o, "uiRowsMax", c.UiRowsMax);
            return c;
        }
    }

    /// <summary>
    /// WHEN A MEMORY SWEEP IS DUE — the whole rule, as a pure function, so the trigger can be
    /// tested without an editor and the two callers cannot drift apart (invariant 99's corollary).
    ///
    /// COUNT **OR** BYTES. A count alone is not a bound on memory: the "sweep every 256" interval
    /// was measured on 16x16 fixtures at ~0.15 MB each, and 256 of a shipped game's 2048x2048
    /// textures is several GB between sweeps. A byte budget alone is not one either — an estimate
    /// that reads back 0 (a texture the profiler cannot size) would never fire it. So both.
    /// </summary>
    public static class ExportSweepPolicy
    {
        /// <summary><paramref name="sinceCount"/> items and <paramref name="sinceBytes"/> bytes have
        /// been loaded since the last sweep. True when one of the two triggers has been reached; a
        /// trigger that is 0 or negative is OFF rather than always-on.</summary>
        public static bool ShouldSweep(int sinceCount, long sinceBytes, int everyCount, long byteBudget)
        {
            if (everyCount > 0 && sinceCount >= everyCount) return true;
            if (byteBudget > 0 && sinceBytes >= byteBudget) return true;
            return false;
        }
    }

    /// <summary>Tolerant readers. Newtonsoft's Unity build is not nullable-annotated, so every
    /// read goes through here rather than scattering `!` across the format classes.</summary>
    internal static class ExportJson
    {
        internal static JToken? Tok(JObject? o, string key)
        {
            if (o == null) return null;
            var t = o[key];
            return (t == null || t.Type == JTokenType.Null) ? null : t;
        }

        internal static string Str(JObject? o, string key, string fallback = "")
        {
            var t = Tok(o, key);
            if (t == null) return fallback;
            try { return t.Value<string>() ?? fallback; } catch (Exception) { return fallback; }
        }

        internal static string? StrOrNull(JObject? o, string key)
        {
            var t = Tok(o, key);
            if (t == null) return null;
            try { return t.Value<string>(); } catch (Exception) { return null; }
        }

        internal static int Int(JObject? o, string key, int fallback = 0)
        {
            var t = Tok(o, key);
            if (t == null) return fallback;
            try { return t.Value<int>(); } catch (Exception) { return fallback; }
        }

        internal static long Long(JObject? o, string key, long fallback = 0L)
        {
            var t = Tok(o, key);
            if (t == null) return fallback;
            try { return t.Value<long>(); } catch (Exception) { return fallback; }
        }

        internal static bool Bool(JObject? o, string key, bool fallback = false)
        {
            var t = Tok(o, key);
            if (t == null) return fallback;
            try { return t.Value<bool>(); } catch (Exception) { return fallback; }
        }

        internal static JObject? Obj(JObject? o, string key) => Tok(o, key) as JObject;

        internal static JArray? Arr(JObject? o, string key) => Tok(o, key) as JArray;
    }
}
