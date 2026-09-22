using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Slice D — `header.json`. The header is posted BEFORE any part is uploaded and is what the
    /// hosted side reconciles against, so every field has to survive the trip and an EMPTY export
    /// still has to carry every key: an absent field and an empty one read the same on a screen,
    /// and "the kit could not tell" is a different answer from "none".
    /// </summary>
    public class ExportHeaderTests
    {
        private static ExportHeader Full()
        {
            var h = new ExportHeader
            {
                KitVersion = "0.6.0",
                UnityVersion = "6000.0.63f1",
                ExportedAt = "2026-09-21T10:00:00Z",
                WorkspaceId = "cmu8u9tma000b1t4nv5rqwbmo",
                EditorGameId = "snl-scratch",
                AdapterJsonPresent = true,
                ProductName = "Rogue Legend",
                CompanyName = "A Studio",
                Caps = new ExportCaps { ThumbnailPx = 128, DocMaxFiles = 7 },
            };
            h.Totals.Assets = 12904;
            h.Totals.AssetsByType["Texture2D"] = 5012;
            h.Totals.AssetsByType["AudioClip"] = 675;
            h.Totals.Scenes = 139;
            h.Totals.BuildScenes = 12;
            h.Totals.DirsTopLevel = new List<string> { "Assets/Art", "Assets/Scripts" };
            h.Totals.DependencyRows = 9120;
            h.Totals.PatternHits = 412;
            h.Totals.ArtFiles = 1830;
            h.Totals.Thumbnails = 5012;
            h.Totals.Docs = 32;

            h.Inventory.Shards.Add("inventory.0000.jsonl");
            h.Inventory.Rows = 12904;
            h.Dependencies.Shards.Add("dependencies.0000.jsonl");
            h.Dependencies.Rows = 9120;
            h.Patterns.Shards.Add("patterns.0000.jsonl");
            h.Patterns.Rows = 412;
            h.ArtIndex.Shards.Add("art/index.0000.jsonl");
            h.ArtIndex.Rows = 6842;

            h.OverCap.Add(new ExportOverCap
            {
                Path = "Assets/Audio/theme.wav",
                Bytes = 41234567,
                Kind = ExportFormat.KindAudio,
                Reason = ExportFormat.ReasonOverArtFile,
            });
            h.Errors.Add("Assets/Broken.prefab: could not read dependencies: x");
            h.Parts.Add(new ExportPartInfo
            {
                Index = 0, Name = "part-0000.zip", Bytes = 1843200,
                Sha256 = new string('a', 64), Entries = 7,
            });
            return h;
        }

        [Test]
        public void RoundTripsThroughToJsonAndFromJson()
        {
            var json = Full().ToJsonString();
            var back = ExportHeader.FromJson(json);
            Assert.IsNotNull(back);
            Assert.AreEqual(json, back!.ToJsonString(), "the header is the wire; it must survive the trip byte for byte");

            Assert.AreEqual(1, back.SchemaVersion);
            Assert.AreEqual("0.6.0", back.KitVersion);
            Assert.AreEqual("snl-scratch", back.EditorGameId);
            Assert.IsTrue(back.AdapterJsonPresent);
            Assert.AreEqual(128, back.Caps.ThumbnailPx);
            Assert.AreEqual(7, back.Caps.DocMaxFiles);
            Assert.AreEqual(12904, back.Totals.Assets);
            Assert.AreEqual(5012, back.Totals.AssetsByType["Texture2D"]);
            CollectionAssert.AreEqual(new[] { "Assets/Art", "Assets/Scripts" }, back.Totals.DirsTopLevel);
            Assert.AreEqual("art/index.0000.jsonl", back.ArtIndex.Shards[0]);
            Assert.AreEqual(6842, back.ArtIndex.Rows);
            Assert.AreEqual("build.json", back.BuildFile);
            Assert.AreEqual("identity.json", back.IdentityFile);
            Assert.AreEqual(1, back.OverCap.Count);
            Assert.AreEqual("Assets/Audio/theme.wav", back.OverCap[0].Path);
            Assert.AreEqual(41234567, back.OverCap[0].Bytes);
            Assert.AreEqual(1, back.Parts.Count);
            Assert.AreEqual("part-0000.zip", back.Parts[0].Name);
            Assert.AreEqual(1843200, back.Parts[0].Bytes);
            Assert.AreEqual(7, back.Parts[0].Entries);
        }

        [Test]
        public void AnEmptyHeader_StillCarriesEveryFieldTheSpecPrints()
        {
            var o = new ExportHeader().ToJson();
            foreach (var key in new[]
                     {
                         "schemaVersion", "kitVersion", "unityVersion", "exportedAt", "workspaceId",
                         "editorGameId", "adapterJsonPresent", "productName", "companyName",
                         "caps", "totals", "files", "overCap", "errors", "parts",
                     })
                Assert.IsTrue(o.ContainsKey(key), "header." + key);

            Assert.AreEqual(JTokenType.Null, o["editorGameId"]!.Type, "unknown is null, never \"\"");

            var caps = (JObject)o["caps"]!;
            foreach (var key in new[]
                     {
                         "partMaxBytes", "partSoftBytes", "artFileMaxBytes", "artTotalMaxBytes",
                         "docFileMaxBytes", "docMaxFiles", "thumbnailPx", "thumbnailMaxCount",
                         "jsonlShardRows", "patternFileMaxBytes", "patternHitsMax",
                     })
                Assert.IsTrue(caps.ContainsKey(key), "caps." + key);
            Assert.AreEqual(11, caps.Count, "caps carries exactly the eleven the spec names");
            Assert.AreEqual(33554432L, caps["partMaxBytes"]!.Value<long>());
            Assert.AreEqual(25165824L, caps["partSoftBytes"]!.Value<long>());
            Assert.AreEqual(8388608L, caps["artFileMaxBytes"]!.Value<long>());
            Assert.AreEqual(1073741824L, caps["artTotalMaxBytes"]!.Value<long>());
            Assert.AreEqual(256, caps["thumbnailPx"]!.Value<int>());
            Assert.AreEqual(20000, caps["jsonlShardRows"]!.Value<int>());

            var totals = (JObject)o["totals"]!;
            foreach (var key in new[]
                     {
                         "assets", "assetsByType", "scenes", "buildScenes", "dirsTopLevel",
                         "dependencyRows", "patternHits", "artFiles", "thumbnails", "docs",
                     })
                Assert.IsTrue(totals.ContainsKey(key), "totals." + key);

            var files = (JObject)o["files"]!;
            foreach (var key in new[] { "inventory", "dependencies", "patterns", "artIndex", "build", "identity" })
                Assert.IsTrue(files.ContainsKey(key), "files." + key);
            Assert.IsTrue(((JObject)files["inventory"]!).ContainsKey("shards"));
            Assert.IsTrue(((JObject)files["inventory"]!).ContainsKey("rows"));
        }

        [Test]
        public void FromJson_OnGarbage_IsNull_NotAnException()
        {
            Assert.IsNull(ExportHeader.FromJson("not json"));
            Assert.IsNull(ExportHeader.FromJson("[1,2,3]"));
            Assert.IsNull(ExportHeader.FromJson(""));
        }

        [Test]
        public void FromJson_OnAnOlderShape_KeepsTheDefaults()
        {
            var h = ExportHeader.FromJson(@"{ ""schemaVersion"": 1, ""kitVersion"": ""0.5.0"" }");
            Assert.IsNotNull(h);
            Assert.AreEqual("0.5.0", h!.KitVersion);
            Assert.IsNull(h.EditorGameId);
            Assert.AreEqual(33554432L, h.Caps.PartMaxBytes);
            Assert.AreEqual(0, h.Totals.Assets);
            Assert.AreEqual(0, h.Parts.Count);
        }

        // ---- the bounded listings (ExportFormat.ListingMax / ListedTextMax) ---------------

        private const int Max = ExportFormat.ListingMax;

        [Test]
        public void Errors_AreCapped_WithATailThatSaysHowManyMore()
        {
            var h = new ExportHeader();
            for (var i = 0; i < Max + 500; i++) h.Errors.Add("e" + i);

            var arr = (JArray)h.ToJson()["errors"]!;
            Assert.AreEqual(Max, arr.Count);
            Assert.AreEqual("e0", arr[0].Value<string>());
            Assert.AreEqual("e" + (Max - 2), arr[Max - 2].Value<string>());
            Assert.AreEqual("… and 501 more", arr[Max - 1].Value<string>());

            // the COUNT stays true; only the listing is bounded
            Assert.AreEqual(Max + 500, h.Errors.Count);
        }

        [Test]
        public void Errors_AtExactlyTheCap_HaveNoTail()
        {
            var h = new ExportHeader();
            for (var i = 0; i < Max; i++) h.Errors.Add("e" + i);
            var arr = (JArray)h.ToJson()["errors"]!;
            Assert.AreEqual(Max, arr.Count);
            Assert.AreEqual("e" + (Max - 1), arr[Max - 1].Value<string>());
        }

        [Test]
        public void OverCap_IsCapped_WithAnObjectTail_CarryingTheDroppedBytes()
        {
            var h = new ExportHeader();
            for (var i = 0; i < Max + 100; i++)
                h.OverCap.Add(new ExportOverCap
                {
                    Path = "Assets/a" + i + ".png", Bytes = 10,
                    Kind = ExportFormat.KindTexture, Reason = ExportFormat.ReasonOverArtFile,
                });

            var arr = (JArray)h.ToJson()["overCap"]!;
            Assert.AreEqual(Max, arr.Count);
            var tail = (JObject)arr[Max - 1];
            Assert.AreEqual(ExportFormat.KindMore, tail["kind"]!.Value<string>());
            Assert.AreEqual("… and 101 more", tail["path"]!.Value<string>());
            Assert.AreEqual("… and 101 more", tail["reason"]!.Value<string>());
            Assert.AreEqual(1010L, tail["bytes"]!.Value<long>(), "the tail carries the bytes it dropped");
            Assert.AreEqual(Max + 100, h.OverCap.Count);
        }

        [Test]
        public void AVerboseErrorOrAVeryLongPath_IsBoundedOnTheWire_NotInMemory()
        {
            // The hosted schema refuses a header BY FIELD when a listed string is over its bound —
            // so one verbose exception would otherwise fail a whole export.
            var h = new ExportHeader();
            var longText = new string('x', ExportFormat.ListedTextMax * 3);
            h.Errors.Add(longText);
            h.OverCap.Add(new ExportOverCap { Path = "Assets/" + longText, Bytes = 1, Kind = "texture", Reason = "r" });

            var json = h.ToJson();
            Assert.AreEqual(ExportFormat.ListedTextMax, ((JArray)json["errors"]!)[0].Value<string>()!.Length);
            Assert.AreEqual(ExportFormat.ListedTextMax, ((JArray)json["overCap"]!)[0]["path"]!.Value<string>()!.Length);
            StringAssert.EndsWith("…", ((JArray)json["errors"]!)[0].Value<string>());
            Assert.AreEqual(longText, h.Errors[0], "the in-memory value is untouched");
            // positive control: an ordinary string goes out byte for byte
            Assert.AreEqual("short", ExportFormat.Listed("short"));
        }

        [Test]
        public void ProductCompanyAndEditorGameId_GoOutScrubbedAndBounded_LikeEveryOtherListedString()
        {
            // M7: these three were sent RAW. The hosted schema refuses any bounded string holding a
            // control character and caps them at 200/200/120 (`apps/api/src/onboarding/export/
            // export-format.ts`), so a newline in a product name — which Unity allows — failed the
            // WHOLE export at the header door, after the studio had uploaded a gigabyte.
            var h = new ExportHeader
            {
                ProductName = "Rogue\nLegend\t" + new string('x', 400),
                CompanyName = "A" + (char)0x01 + "Studio",   // a raw 0x01 byte in the SOURCE makes git call the file binary
                EditorGameId = "snl" + (char)0x2028 + "scratch",   // a LINE SEPARATOR, never a literal
            };
            var json = h.ToJson();

            var product = json["productName"]!.Value<string>()!;
            Assert.AreEqual(200, product.Length, "the hosted cap on productName is 200");
            StringAssert.StartsWith("Rogue Legend ", product, "every control character is a space");
            StringAssert.EndsWith("…", product);
            Assert.AreEqual("A Studio", json["companyName"]!.Value<string>());
            Assert.AreEqual("snl scratch", json["editorGameId"]!.Value<string>());

            // …and the LENGTH cap on the game id is its own (120), not the 400 of a listed string.
            var longId = new ExportHeader { EditorGameId = new string('g', 300) };
            Assert.AreEqual(120, longId.ToJson()["editorGameId"]!.Value<string>()!.Length);

            // NULL STAYS NULL. The hosted identity gate compares `editorGameId` with the game this
            // workspace is bound to; "" is a value and would be compared as one.
            var none = new ExportHeader { EditorGameId = null };
            Assert.AreEqual(JTokenType.Null, none.ToJson()["editorGameId"]!.Type,
                "a project with no adapter.json answers null, never an empty string");

            // POSITIVE CONTROL: an ordinary name goes out byte for byte.
            var plain = new ExportHeader { ProductName = "Rogue Legend", CompanyName = "A Studio", EditorGameId = "snl" };
            var plainJson = plain.ToJson();
            Assert.AreEqual("Rogue Legend", plainJson["productName"]!.Value<string>());
            Assert.AreEqual("A Studio", plainJson["companyName"]!.Value<string>());
            Assert.AreEqual("snl", plainJson["editorGameId"]!.Value<string>());
        }

        [Test]
        public void TheWorstCaseHeader_FitsTheBodyTheServerAccepts()
        {
            // The hosted side reads the header as ONE JSON body bounded at 4 MiB
            // (EXPORT_HEADER_MAX_BYTES). Fill every bounded list to its cap with maximal strings:
            // the kit must never be able to produce a header the server cannot even receive.
            var h = new ExportHeader { WorkspaceId = "w", KitVersion = "0.6.0", UnityVersion = "6000" };
            // 3-byte characters here too, not ASCII: the bound is in UTF-16 units and the body is
            // measured in UTF-8 bytes. The first version of this proof was ASCII and was wrong by 3x
            // for a studio with CJK asset paths.
            var fat = new string('中', ExportFormat.ListedTextMax * 2);
            // …and the three SEPARATORS Newtonsoft escapes as SIX bytes each (N5). An exception
            // message from a studio's machine can hold any of them, and before they were scrubbed
            // "3 bytes per character" was not the worst case at all.
            var separators = new string((char)0x2028, ExportFormat.ListedTextMax * 2);
            for (var i = 0; i < Max * 4; i++)
            {
                h.Errors.Add((i % 4 == 0 ? separators : fat) + i);
                h.OverCap.Add(new ExportOverCap { Path = (i % 4 == 1 ? separators : fat) + i, Bytes = long.MaxValue, Kind = "texture", Reason = ExportFormat.ReasonOverArtTotal });
            }
            for (var i = 0; i < 256; i++)
                h.Parts.Add(new ExportPartInfo { Index = i, Name = ExportFormat.PartName(i), Bytes = 33554432, Sha256 = new string('a', 64), Entries = 99999 });

            // …AND THE TWO FIELDS THE KIT DID NOT BOUND. `assetsByType` and `dirsTopLevel` were
            // left out of this test AND out of the bounding, so a project with thousands of
            // ScriptableObject types could still build a header the server cannot receive
            // (fresh-context audit, 2026-09-21). 3-byte characters, because the bound is in UTF-16
            // units and the body is measured in UTF-8 bytes.
            var wide = new string('中', 400);
            // The index goes FIRST so the keys are still distinct once truncated — otherwise they
            // would all merge and the fold-to-"(other)" path would never be reached.
            for (var i = 0; i < ExportFormat.AssetTypeKeysMax * 3; i++)
                h.Totals.AssetsByType[i.ToString("D6") + wide] = i + 1;
            for (var i = 0; i < ExportFormat.DirsTopLevelMax * 3; i++)
                h.Totals.DirsTopLevel.Add("Assets/" + i.ToString("D6") + wide);
            h.Totals.Assets = 0;
            foreach (var kv in h.Totals.AssetsByType) h.Totals.Assets += kv.Value;

            var json = h.ToJson();
            var flat = json.ToString(Newtonsoft.Json.Formatting.None);
            var bytes = System.Text.Encoding.UTF8.GetByteCount(flat);
            Assert.Less(bytes, 3 * 1024 * 1024,
                "every bounded list at its cap at once must stay under the 4 MiB body bound (" + bytes + " bytes)");
            // NOTHING ON THE WIRE COSTS SIX BYTES A CHARACTER. Newtonsoft escapes U+0085/2028/2029
            // even though they are not control characters, so the byte bound above only holds while
            // nothing listed can contain one.
            foreach (var escape in new[] { "\\u0085", "\\u2028", "\\u2029" })
                Assert.IsFalse(flat.IndexOf(escape, System.StringComparison.Ordinal) >= 0,
                    "a listed string reached the wire holding " + escape + " — six bytes for one character");

            var byType = (JObject)json["totals"]!["assetsByType"]!;
            Assert.LessOrEqual(byType.Count, ExportFormat.AssetTypeKeysMax, "assetsByType key count");
            var sum = 0;
            foreach (var p in byType) sum += p.Value!.Value<int>();
            Assert.AreEqual(h.Totals.Assets, sum,
                "the per-type counts must STILL sum to totals.assets — §7 reconciles on it");
            Assert.IsTrue(byType.ContainsKey(ExportFormat.OtherTypeKey), "and the remainder is named");
            foreach (var p in byType)
                Assert.LessOrEqual(p.Key.Length, ExportFormat.AssetTypeKeyTextMax, "type key '" + p.Key + "'");

            var dirs = (JArray)json["totals"]!["dirsTopLevel"]!;
            Assert.AreEqual(ExportFormat.DirsTopLevelMax, dirs.Count);
            foreach (var d in dirs)
                Assert.LessOrEqual(d.Value<string>()!.Length, ExportFormat.DirTextMax);
        }

        // ---- audit round 4, S2 + M12: the fold the SERVER has to be able to mirror ----------

        /// <summary>
        /// The hosted reconcile (`apps/api/src/onboarding/export/export-reconcile.ts`) walks the
        /// types that LANDED, lists each one, and asks "is that key in the header?" — counting it
        /// there if it is and under `"(other)"` if it is not. For a valid export to reconcile, the
        /// kit's fold must be the exact mirror of that rule, and the only way it can be is to LIST
        /// FIRST: a raw type that lists to a key the header already carries belongs under that key
        /// whatever the kept-count is. Deciding "fold" before listing is what let a legitimate
        /// export fail reconcile (audit round 4, S2).
        ///
        /// Both cases here are the auditor's own, reproduced against the real hosted module.
        /// </summary>
        [Test]
        public void TheTypeFold_CountsAFoldedNameUNDERTheKeyItListsTo_NotUnderOther()
        {
            // A TRUNCATION COLLISION STRADDLING THE BOUNDARY. Two 101-unit names sharing their
            // first 99 units: the first lands as the last KEPT key, the second is past the cap.
            var straddle = new SortedDictionary<string, int>(StringComparer.Ordinal);
            straddle["SceneAsset"] = 2;                               // 'S' sorts after 'C','D','E'
            for (var i = 0; i < 1998; i++) straddle["Cfg" + i.ToString("00000")] = 1;
            var stem = "Dd" + new string('q', 97);                    // 99 units
            straddle[stem + "X1"] = 3;
            straddle[stem + "X2"] = 4;
            for (var i = 0; i < 10; i++) straddle["E" + i.ToString("0000")] = 1;

            var folded = ExportTotals.FoldByType(straddle, out var foldedKeys);
            var key = ExportFormat.Listed(stem + "X1", ExportFormat.AssetTypeKeyTextMax);
            Assert.AreEqual(ExportFormat.Listed(stem + "X2", ExportFormat.AssetTypeKeyTextMax), key,
                "the fixture only means anything if the two names really do list to ONE key");
            Assert.AreEqual(7, folded[key]!.Value<int>(),
                "both names list to '" + key + "', so the server counts 3+4 there — and so must the kit");
            Assert.AreEqual(12, folded[ExportFormat.OtherTypeKey]!.Value<int>(),
                "…and only the names whose key is NOT in the header ride in (other)");
            Assert.AreEqual(11, foldedKeys, "ten E-keys and SceneAsset");
            AssertSum(straddle, folded);

            // A SCRUB COLLISION STRADDLING THE BOUNDARY: `Listed` turns U+2028 into a space, so
            // U+2028+"Foo" lists to the very same key as " Foo" — which is kept, being ordinally
            // first, while U+2028 sorts last of all.
            var scrub = new SortedDictionary<string, int>(StringComparer.Ordinal);
            scrub["SceneAsset"] = 2;
            scrub[" Foo"] = 2;
            scrub[(char)0x2028 + "Foo"] = 3;
            for (var i = 0; i < 2005; i++) scrub["Cfg" + i.ToString("00000")] = 1;

            var scrubbed = ExportTotals.FoldByType(scrub, out _);
            Assert.AreEqual(5, scrubbed[" Foo"]!.Value<int>(),
                "a control character is not a different type to the server, and must not be to the kit");
            AssertSum(scrub, scrubbed);

            // POSITIVE CONTROL: nothing at all is folded when the cap does not bite, and the keys
            // are exactly the listed names.
            var small = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["Texture2D"] = 5, ["SceneAsset"] = 1 };
            var plain = ExportTotals.FoldByType(small, out var none);
            Assert.AreEqual(0, none);
            Assert.AreEqual(2, plain.Count);
            Assert.AreEqual(5, plain["Texture2D"]!.Value<int>());
            Assert.IsFalse(plain.ContainsKey(ExportFormat.OtherTypeKey));
        }

        [Test]
        public void TheTypeFoldNote_IsWrittenOnlyWhenSomethingWasREALLYFolded()
        {
            // M12. 2,001 raw type NAMES that list to 1,502 keys fold nothing and produce no
            // "(other)" key — but the row was written off the RAW count and said two types had
            // been counted under a key that is not in the header.
            var collapsing = new SortedDictionary<string, int>(StringComparer.Ordinal);
            collapsing["SceneAsset"] = 2;
            for (var i = 0; i < 1500; i++) collapsing["Cfg" + i.ToString("00000")] = 1;
            var stem = "Bb" + new string('q', 97);                    // 99 units: all 500 collide
            for (var i = 0; i < 500; i++) collapsing[stem + i.ToString("000")] = 1;
            Assert.Greater(collapsing.Count, ExportFormat.AssetTypeKeysMax, "2,001 RAW types");

            var json = ExportTotals.FoldByType(collapsing, out var foldedKeys);
            Assert.AreEqual(0, foldedKeys, "1,502 listed keys fit under the 2,000-key cap");
            Assert.IsFalse(json.ContainsKey(ExportFormat.OtherTypeKey),
                "nothing was folded, so there is no (other) key for a row to point at");
            Assert.IsNull(ExportTotals.TypeFoldNote(collapsing),
                "the row must not claim a fold that did not happen");
            AssertSum(collapsing, json);

            // POSITIVE CONTROL: a fold that really happens is still named, and the number is the
            // number of KEYS that went into "(other)".
            var folding = new SortedDictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < ExportFormat.AssetTypeKeysMax + 5; i++) folding["Cfg" + i.ToString("00000")] = 1;
            ExportTotals.FoldByType(folding, out var reallyFolded);
            Assert.AreEqual(6, reallyFolded, "2,005 distinct keys, 1,999 kept");
            var note = ExportTotals.TypeFoldNote(folding);
            Assert.IsNotNull(note);
            // N is folded KEYS — what the header can show — not raw types or assets (round-5 audit C6)
            StringAssert.Contains("6 type key(s) past the", note!, note);
            StringAssert.Contains(ExportFormat.OtherTypeKey, note, note);
        }

        private static void AssertSum(SortedDictionary<string, int> raw, JObject folded)
        {
            var expected = 0;
            foreach (var kv in raw) expected += kv.Value;
            var got = 0;
            foreach (var p in folded) got += p.Value!.Value<int>();
            Assert.AreEqual(expected, got, "the per-type counts must STILL sum to totals.assets");
            Assert.LessOrEqual(folded.Count, ExportFormat.AssetTypeKeysMax, "assetsByType key count");
        }
    }
}
