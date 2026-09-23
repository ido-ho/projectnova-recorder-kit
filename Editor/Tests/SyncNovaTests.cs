using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Slice A2′ — "Send to my editor" on the studio's disk. Every test runs the REAL handler
    /// against a real temp project root: the code under test is the code that runs in production
    /// (invariant 101), not a re-description of it.
    /// </summary>
    public class SyncNovaTests
    {
        private string _root = "";

        private static readonly string TwoShots = @"{
  ""$schemaVersion"": 1,
  ""shots"": [
    { ""name"": ""board_roll"", ""setup"": [""RollTargetType LargeCoin""],
      ""steps"": [ { ""kind"": ""click"", ""name"": ""RollBTN"" } ],
      ""settle"": { ""kind"": ""present"", ""name"": ""RollBTN"" } },
    { ""name"": ""board_win"", ""parameters"": [""hero""],
      ""steps"": [ { ""kind"": ""cheat"", ""command"": ""SelectHero {hero}"" } ],
      ""settle"": { ""kind"": ""state"", ""name"": ""Win"" } }
  ]
}";

        private static readonly string Adapter = @"{
  ""gameId"": ""snl-scratch"",
  ""ready"": {
    ""mute"": ""set Music.mute true"",
    ""muteGet"": ""Music.mute"",
    ""resistOn"": ""call Player.Resist 1"",
    ""resistOff"": ""set Player.Resist 0""
  }
}";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "sync-nova-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* a leaked handle must not fail the suite */ }
        }

        private string ShotsPath => RelayPaths.NovaShotsFile(_root);
        private string AdapterPath => SyncNova.AdapterFile(_root);
        private string SyncedPath => SyncNova.SyncedFile(_root);

        private static SyncNovaFiles Sent(string shots, string adapter,
            string? shotsSha = null, string? adapterSha = null) =>
            new(shots, adapter,
                shotsSha ?? SyncNova.Sha256OfText(shots),
                adapterSha ?? SyncNova.Sha256OfText(adapter));

        private SyncNovaResult Run(SyncNovaFiles? files, string? jobGameId = "snl-scratch",
            string runId = "run-1") =>
            SyncNova.Run(_root, files, runId, jobGameId);

        // ---- refusals: nothing is written -----------------------------------------------------

        [Test]
        public void AShotsShaThatDoesNotMatchRefusesAndWritesNothing()
        {
            var result = Run(Sent(TwoShots, Adapter, shotsSha: new string('0', 64)));
            Assert.IsNotNull(result.Refusal);
            StringAssert.Contains("shots.json", result.Refusal!);
            Assert.IsFalse(File.Exists(ShotsPath), "a file was written after a sha mismatch");
            Assert.IsFalse(File.Exists(AdapterPath));
            Assert.IsFalse(File.Exists(SyncedPath));
        }

        [Test]
        public void AnAdapterShaThatDoesNotMatchRefusesAndWritesNothing()
        {
            var result = Run(Sent(TwoShots, Adapter, adapterSha: new string('a', 64)));
            Assert.IsNotNull(result.Refusal);
            StringAssert.Contains("adapter.json", result.Refusal!);
            Assert.IsFalse(File.Exists(ShotsPath));
            Assert.IsFalse(File.Exists(AdapterPath));
        }

        [Test]
        public void AJobWithNoFilesIsRefusedByName()
        {
            Assert.AreEqual(SyncNovaFiles.MissingReason, SyncNova.Refusal(null, "snl-scratch"));
            var result = Run(null);
            Assert.AreEqual(SyncNovaFiles.MissingReason, result.Refusal);
            Assert.IsFalse(Directory.Exists(RelayPaths.NovaDir(_root)));
        }

        [Test]
        public void AnAdapterNamingAnotherGameThanTheJobIsRefused()
        {
            var refusal = SyncNova.Refusal(Sent(TwoShots, Adapter), "rogue-legend");
            Assert.IsNotNull(refusal);
            StringAssert.Contains("snl-scratch", refusal!);
            StringAssert.Contains("rogue-legend", refusal!);
            var result = Run(Sent(TwoShots, Adapter), jobGameId: "rogue-legend");
            Assert.IsNotNull(result.Refusal);
            Assert.IsFalse(File.Exists(ShotsPath));
        }

        [Test]
        public void AJobThatNamesNoGameIsNotRefusedOnIdentity()
        {
            Assert.IsNull(SyncNova.Refusal(Sent(TwoShots, Adapter), null));
        }

        [Test]
        public void AnUnwritableTargetIsANamedErrorNotAThrow()
        {
            // shots.json is a DIRECTORY: every write onto it fails, and the handler has to come
            // back with a sentence rather than an exception out of the agent's coroutine.
            Directory.CreateDirectory(ShotsPath);
            var result = Run(Sent(TwoShots, Adapter));
            Assert.IsNotNull(result.Refusal, "an unwritable target must be reported, not thrown");
            StringAssert.Contains("shots.json", result.Refusal!);
        }

        // ---- the first sync -------------------------------------------------------------------

        [Test]
        public void AFirstSyncWritesBothFilesAndReportsWhatLoaded()
        {
            var result = Run(Sent(TwoShots, Adapter));

            Assert.IsNull(result.Refusal, result.Refusal);
            CollectionAssert.AreEqual(new[] { "board_roll", "board_win" }, result.Shots.ToArray());
            CollectionAssert.IsEmpty(result.ShotLoadErrors);
            Assert.AreEqual("snl-scratch", result.GameId);
            Assert.IsFalse(result.ReplacedLocalEdits);

            // The bytes on disk are the bytes that were sent — UTF-8, no BOM, nothing re-serialised.
            CollectionAssert.AreEqual(new UTF8Encoding(false).GetBytes(TwoShots),
                File.ReadAllBytes(ShotsPath));
            CollectionAssert.AreEqual(new UTF8Encoding(false).GetBytes(Adapter),
                File.ReadAllBytes(AdapterPath));

            var synced = JObject.Parse(File.ReadAllText(SyncedPath));
            Assert.AreEqual(SyncNova.Sha256OfText(TwoShots), synced["shotsSha256"]!.Value<string>());
            Assert.AreEqual(SyncNova.Sha256OfText(Adapter), synced["adapterSha256"]!.Value<string>());
            Assert.AreEqual("run-1", synced["runId"]!.Value<string>());
            Assert.IsNotNull(synced["at"]);
        }

        [Test]
        public void TheReportedShasAreOfTheBYTESONDISK()
        {
            Run(Sent(TwoShots, Adapter));
            // Read back, never echoed: the facts have to be able to disagree with the input, or
            // the server's "did it write what I sent?" check compares a value with itself
            // (invariant 100).
            var result = Run(Sent(TwoShots, Adapter), runId: "run-2");
            Assert.AreEqual(SyncNova.Sha256OfFile(ShotsPath), result.ShotsSha256);
            Assert.AreEqual(SyncNova.Sha256OfFile(AdapterPath), result.AdapterSha256);
            Assert.AreEqual(SyncNova.Sha256OfText(TwoShots), result.ShotsSha256);
        }

        [Test]
        public void NoTemporaryFileIsLeftBehind()
        {
            Run(Sent(TwoShots, Adapter));
            var strays = Directory.GetFiles(RelayPaths.NovaDir(_root))
                .Where(f => Path.GetFileName(f).Contains(".tmp-")).ToArray();
            CollectionAssert.IsEmpty(strays, "an atomic write left its temp file behind");
        }

        /// <summary>
        /// The eleventh audit, ruling 1 — REVERSED FROM "WRITTEN AND ITS ERRORS REPORTED". The delivery check runs the
        /// LOADER's own validation (<c>JsonShotLoader.Read</c>, the function <c>LoadFromPath</c> runs after
        /// <c>File.ReadAllText</c>) on the text it is about to write, and refuses with the loader's own first error. A file
        /// the loader refuses any part of used to be written and reported as a measurement; rounds 8–11 each found a loader
        /// rule the parallel checklist missed, and one of them (a deep <c>$schemaVersion</c>) stalled every later load.
        /// </summary>
        [Test]
        public void AShotsFileTheLoaderRefusesIsRefusedWithTheLoadersOwnFirstErrorAndNothingIsWritten()
        {
            const string broken = @"{ ""$schemaVersion"": 1, ""shots"": [ { ""name"": ""nope"" } ] }";
            Assert.AreEqual("shots.json would not load as the kit loads it (shots[0] ('nope'): shot needs a non-empty " +
                            "'steps' array) — nothing was written", Run(Sent(broken, Adapter)).Refusal);
            Assert.IsFalse(Directory.Exists(RelayPaths.NovaDir(_root)), "a shots.json the loader refuses was written");

            // two errors: the first, and how many more
            const string twice = @"{ ""$schemaVersion"": 1, ""shots"": [ { ""name"": ""nope"" }, 5 ] }";
            Assert.AreEqual("shots.json would not load as the kit loads it (shots[0] ('nope'): shot needs a non-empty " +
                            "'steps' array; and 1 more) — nothing was written", Run(Sent(twice, Adapter)).Refusal);
            // one good shot does not carry a bad one: partial loading is the loader's contract on disk, not a delivery's
            var half = TwoShots.Replace(@"""kind"": ""click"", ""name"": ""RollBTN""", @"""kind"": ""clcik"", ""name"": ""RollBTN""");
            Assert.AreNotEqual(TwoShots, half, "control: the test changed the text");
            Assert.AreEqual("shots.json would not load as the kit loads it (shots[0] ('board_roll').steps[0]: unknown step " +
                            "kind 'clcik') — nothing was written", Run(Sent(half, Adapter)).Refusal);
            Assert.IsFalse(Directory.Exists(RelayPaths.NovaDir(_root)), "a shots.json the loader half-refuses was written");

            // CONTROL, and the positive control ruling 1 asks for: what is written loads with no error at all
            var result = Run(Sent(TwoShots, Adapter));
            Assert.IsNull(result.Refusal);
            CollectionAssert.IsEmpty(result.ShotLoadErrors);
            CollectionAssert.AreEqual(new[] { "board_roll", "board_win" }, result.Shots.ToArray());
        }

        /// <summary>
        /// The twelfth audit, M1 — THE AUDITOR'S FILE: one shot at the send cap, its name 263,799 characters long, over 9,300
        /// wait steps. It was written (1,909 ms in this call) and then cost 0.85 s on every load, because the loader repeated
        /// the name in every step's address. The site refuses a name over 80 characters and a server that skipped its lint
        /// could still send one; the kit now refuses it at the name, before any step, and the refusal does not repeat it.
        /// </summary>
        [Test]
        public void TheTwelfthAuditsLongNameFileIsRefusedBeforeAnythingIsWritten()
        {
            string OneShot(int nameLength, int steps = 9300)
            {
                var sb = new System.Text.StringBuilder("{\"$schemaVersion\":1,\"shots\":[{\"name\":\"n");
                sb.Append('a', nameLength).Append("\",\"steps\":[");
                for (var i = 0; i < steps; i++) sb.Append(i > 0 ? "," : "").Append("{\"kind\":\"wait\",\"seconds\":1}");
                return sb.Append("],\"settle\":{\"kind\":\"state\",\"name\":\"a\"}}]}").ToString();
            }
            var filler = SyncNova.MaxShotsBytes - SyncNova.Utf8NoBom(OneShot(0)).Length;
            var auditors = OneShot(filler);
            Assert.AreEqual(SyncNova.MaxShotsBytes, SyncNova.Utf8NoBom(auditors).Length, "the file sits at the send cap");
            Assert.AreEqual($"shots.json would not load as the kit loads it (shots[0]: the shot name is {filler + 1} characters long, " +
                            "and this kit takes names of at most 80) — nothing was written", Run(Sent(auditors, Adapter)).Refusal);
            Assert.AreEqual(263_799, filler + 1, "the auditor's name length");
            Assert.IsFalse(Directory.Exists(RelayPaths.NovaDir(_root)), "the long-name file was written");
            // CONTROL: the name is what refuses it. Under a two-character name the same 9,300 steps are refused since the
            // thirteenth audit — by their count, after the name — and 500 of them are written, and load with no error.
            Assert.AreEqual("shots.json would not load as the kit loads it (shots[0] ('na'): the shot has 9300 steps, " +
                            "and this kit takes at most 500) — nothing was written", Run(Sent(OneShot(1), Adapter)).Refusal);
            var result = Run(Sent(OneShot(1, JsonShotLoader.MaxSteps), Adapter));
            Assert.IsNull(result.Refusal);
            CollectionAssert.IsEmpty(result.ShotLoadErrors);
        }

        // ---- the tenth audit (fresh context, 2026-09-22): the check reads what the readers will read, and fails closed ----

        /// <summary>
        /// The tenth audit, S1, ruling (b) — REVERSED FROM "WRITTEN AND REPORTED". A shots.json this kit cannot parse used
        /// to be written, because every check <see cref="SyncNova"/> makes before it writes answered "nothing found" for it
        /// (no lever, no placeholder) and the loader reported it afterwards. A check that answers "nothing found" when it
        /// could not read the file is a check that did not run. Now: not JSON, a top level that is not an object, or no
        /// <c>shots</c> array is refused by name, before anything is written — since the eleventh audit in the LOADER's
        /// own words, because the check is the loader
        /// (<see cref="AShotsFileTheLoaderRefusesIsRefusedWithTheLoadersOwnFirstErrorAndNothingIsWritten"/>).
        /// </summary>
        [Test]
        public void AShotsTextTheChecksCannotReadIsRefusedByNameAndNothingIsWritten()
        {
            foreach (var (text, why) in new[]
                     {
                         ("not json at all", "shots file is not valid JSON: Unexpected character encountered while parsing value"),
                         ("[1, 2]", "Current JsonReader item is not an object: StartArray"),
                         ("{ \"$schemaVersion\": 1 }", "(shots file has no 'shots' array)"),
                         ("{ \"$schemaVersion\": 1, \"shots\": { \"a\": 1 } }", "(shots file has no 'shots' array)"),
                     })
            {
                var result = Run(Sent(text, Adapter));
                Assert.IsNotNull(result.Refusal, "written: " + text);
                StringAssert.StartsWith("shots.json would not load as the kit loads it (", result.Refusal!, text);
                if (!why.StartsWith("(", StringComparison.Ordinal))
                    StringAssert.Contains("(shots file is not valid JSON: ", result.Refusal!, text);
                StringAssert.Contains(why, result.Refusal!, text);
                StringAssert.EndsWith(") — nothing was written", result.Refusal!, text);
                Assert.IsFalse(Directory.Exists(RelayPaths.NovaDir(_root)), "something was written for " + text);
            }
            // CONTROL: a document the checks can read is written
            Assert.IsNull(Run(Sent(TwoShots, Adapter)).Refusal);
            Assert.AreEqual(TwoShots, File.ReadAllText(ShotsPath));
        }

        /// <summary>
        /// The tenth audit, S1, ruling (a) — A TEXT THAT BEGINS WITH U+FEFF. <see cref="SyncNova.Utf8NoBom"/> writes it as
        /// EF BB BF, which every reader of the file (<c>File.ReadAllText</c>) takes as a byte-order mark and reads past —
        /// so the loader, the window and the director read the whole document, while every check before the write parsed
        /// the mark, failed, and answered "nothing found". Refused by name for either file, before anything is written; the
        /// character is named by the kit's one set of characters it will not take (<see cref="Levers.UnsendableCharacter"/>).
        /// The same rule refuses a lone half of a surrogate pair ANYWHERE in the text: the bytes written carry U+FFFD in
        /// its place, so the readers would not read the text that was checked either.
        /// </summary>
        [Test]
        public void ATextTheReadersWouldReadDifferentlyIsRefusedByNameAndNothingIsWritten()
        {
            const string bom = "\uFEFF";
            Assert.AreEqual("shots.json cannot be read as the kit will read it (it begins with U+FEFF, which the kit's " +
                            "readers drop as a byte-order mark) — nothing was written", Run(Sent(bom + TwoShots, Adapter)).Refusal);
            Assert.IsFalse(Directory.Exists(RelayPaths.NovaDir(_root)), "a shots.json led by U+FEFF was written");
            // two marks: the readers drop the first and keep the second, so the text still begins with one they drop
            Assert.AreEqual("shots.json cannot be read as the kit will read it (it begins with U+FEFF, which the kit's " +
                            "readers drop as a byte-order mark) — nothing was written", Run(Sent(bom + bom + TwoShots, Adapter)).Refusal);

            const string adapterSentence = "adapter.json cannot be read as the kit will read it (it begins with U+FEFF, " +
                                           "which the kit's readers drop as a byte-order mark) — nothing was written";
            Assert.AreEqual(adapterSentence, SyncNova.Refusal(Sent(TwoShots, bom + Adapter), "snl-scratch"),
                "the agent asks Refusal before Run: the mark is named, not read as 'names no gameId'");
            Assert.AreEqual(adapterSentence, Run(Sent(TwoShots, bom + Adapter)).Refusal);
            Assert.IsFalse(Directory.Exists(RelayPaths.NovaDir(_root)), "an adapter.json led by U+FEFF was written");

            // a lone half of a surrogate pair in a CLICK NAME — not a lever, so no lever rule would see it
            var lone = TwoShots.Replace("\"name\": \"RollBTN\" } ]", "\"name\": \"Roll\uD800BTN\" } ]");
            Assert.AreNotEqual(TwoShots, lone, "control: the test changed the text");
            Assert.AreEqual($"shots.json cannot be read as the kit will read it (its character {lone.IndexOf('\uD800')} is U+D800, " +
                            "which the kit's readers read back as U+FFFD) — nothing was written", Run(Sent(lone, Adapter)).Refusal);
            Assert.IsFalse(Directory.Exists(RelayPaths.NovaDir(_root)), "a text holding a lone surrogate was written");

            // CONTROLS: the same texts without the mark are written; a U+FEFF that is NOT first is read back as itself
            var inside = TwoShots.Replace("\"name\": \"RollBTN\" } ]", "\"name\": \"Roll\uFEFFBTN\" } ]");
            Assert.AreNotEqual(TwoShots, inside, "control: the test changed the text");
            Assert.IsNull(Run(Sent(inside, Adapter)).Refusal, "a U+FEFF inside a click name is read back as sent");
            Assert.IsNull(Run(Sent(TwoShots, Adapter), runId: "run-2").Refusal);
            Assert.AreEqual(TwoShots, File.ReadAllText(ShotsPath));
        }

        /// <summary>
        /// The tenth audit, S1 — THE TEXT THE CHECK READS IS THE TEXT <c>File.ReadAllText</c> READS BACK. Pinned against
        /// <c>File.ReadAllText</c> itself, on the exact bytes <see cref="SyncNova.Utf8NoBom"/> writes: a leading U+FEFF
        /// (dropped), a U+FEFF inside (kept), a lone half of a surrogate pair of either kind (U+FFFD), a plain text and an
        /// empty one (themselves) — and two byte sequences UTF-8 never writes, led by a UTF-16 mark, which switch the
        /// decoding (the fold's own mutant KN5: without them, turning the mark detection off changed no answer, because a
        /// UTF-8 reader skips a UTF-8 mark either way). A decoder that differed from the readers' would make every check
        /// below it read another file than the one the kit then runs.
        /// </summary>
        [Test]
        public void TheCheckDecodesTheBytesExactlyAsFileReadAllTextDoes()
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, "decode.json");
            foreach (var text in new[]
                     {
                         "\uFEFF{ \"a\": 1 }", "{ \"a\": \"x\uFEFFy\" }", "{ \"a\": \"x\uD800y\" }", "{ \"a\": \"x\uDC00\" }",
                         "\uFEFF\uFEFF{ }", TwoShots, "",
                     })
            {
                var bytes = SyncNova.Utf8NoBom(text);
                File.WriteAllBytes(path, bytes);
                Assert.AreEqual(File.ReadAllText(path), SyncNova.ReadBackAsTheKitWill(bytes),
                    "decoded differently from File.ReadAllText: " + string.Concat(text.Select(c => c < 0x20 || c > 0x7E ? $"\\u{(int)c:X4}" : c.ToString())));
            }
            // …and on bytes UTF-8 never writes: a UTF-16 mark switches File.ReadAllText's decoding, so it switches this one's
            foreach (var bytes in new[] { new byte[] { 0xFF, 0xFE, 0x7B, 0x00, 0x7D, 0x00 }, new byte[] { 0xFE, 0xFF, 0x00, 0x7B, 0x00, 0x7D } })
            {
                File.WriteAllBytes(path, bytes);
                Assert.AreEqual(File.ReadAllText(path), SyncNova.ReadBackAsTheKitWill(bytes), BitConverter.ToString(bytes));
                Assert.AreEqual("{}", SyncNova.ReadBackAsTheKitWill(bytes), "control: " + BitConverter.ToString(bytes) + " is UTF-16 for {}");
            }
            // CONTROLS: the two texts the readers change are not read back as sent, and a plain one is
            Assert.AreEqual("{ \"a\": 1 }", SyncNova.ReadBackAsTheKitWill(SyncNova.Utf8NoBom("\uFEFF{ \"a\": 1 }")));
            Assert.AreEqual("{ \"a\": \"x\uFFFDy\" }", SyncNova.ReadBackAsTheKitWill(SyncNova.Utf8NoBom("{ \"a\": \"x\uD800y\" }")));
            Assert.AreEqual(TwoShots, SyncNova.ReadBackAsTheKitWill(SyncNova.Utf8NoBom(TwoShots)));
        }

        /// <summary>
        /// The tenth audit, M1 — A READY VALUE THAT IS NOT A COMMAND STRING. <c>HygieneReadyGate</c> reads <c>"mute": 5</c>
        /// as the command "5" (a cast renders a number or a bool as text), while the lever list reads strings only, so no
        /// row named what the ready block would run. Refused by name at delivery, for the four fields the ready block
        /// reads, as the site refuses them (<c>READY_FIELDS</c> in <c>shots-lint.ts</c>); a <c>ready</c> that is not an
        /// object is refused the same way. Asked of <see cref="SyncNova.Refusal"/> too, because the agent asks it first.
        /// </summary>
        [Test]
        public void AReadyValueThatIsNotACommandStringIsRefusedByName()
        {
            foreach (var (ready, why) in new[]
                     {
                         ("{ \"mute\": 5 }", "its ready.mute is a number, not a command string"),
                         ("{ \"muteGet\": true }", "its ready.muteGet is a boolean, not a command string"),
                         ("{ \"resistOn\": null }", "its ready.resistOn is null, not a command string"),
                         ("{ \"resistOff\": [\"set A 0\"] }", "its ready.resistOff is an array, not a command string"),
                         ("{ \"mute\": { \"cmd\": \"set A 0\" } }", "its ready.mute is an object, not a command string"),
                         ("5", "its \"ready\" is a number, not an object"),
                         ("[]", "its \"ready\" is an array, not an object"),
                         // the eleventh audit, TS2: a blank write is read by the ready gate as no command at all
                         // (HygieneSpec.Str) while the lever list asks a person to tick " " — refused, as the site does
                         ("{ \"mute\": \" \", \"resistOn\": \"call Net.Resist 1\" }", "its ready.mute is blank, not a command string"),
                         ("{ \"resistOff\": \"\", \"mute\": \"set A 0\" }", "its ready.resistOff is blank, not a command string"),
                     })
            {
                var adapter = "{ \"gameId\": \"snl-scratch\", \"ready\": " + ready + " }";
                var sentence = $"adapter.json cannot be read as the kit will read it ({why}) — nothing was written";
                Assert.AreEqual(sentence, SyncNova.Refusal(Sent(TwoShots, adapter), "snl-scratch"), ready);
                Assert.AreEqual(sentence, Run(Sent(TwoShots, adapter)).Refusal, ready);
                Assert.IsFalse(Directory.Exists(RelayPaths.NovaDir(_root)), "written: " + ready);
            }
            // CONTROL: the four fields as command strings, and fields the ready block does not read, are written
            Assert.IsNull(Run(Sent(TwoShots, Adapter)).Refusal);
            Assert.IsNull(Run(Sent(TwoShots, "{ \"gameId\": \"snl-scratch\", \"ready\": { \"note\": 5, \"mute\": \"set A 0\" } }"),
                    runId: "run-2").Refusal,
                "a field the ready block does not read is not a ready write");
        }

        /// <summary>
        /// The eleventh audit, ruling 1 — THE READY GATE'S OWN REFUSAL, AT DELIVERY. <c>HygieneReadyGate</c> fails every
        /// capture on a <c>ready</c> block that is present but names no command it reads (<c>HygieneSpec.Problem</c>): an
        /// empty block, or one whose only field is a typo. The delivery check used to write such a file, which then failed
        /// ready on every capture. Refused with the gate's own words, through <see cref="SyncNova.Refusal"/> (the agent asks
        /// it first) and through <see cref="SyncNova.Run"/>.
        /// </summary>
        [Test]
        public void AReadyBlockTheReadyGateRefusesIsRefusedWithTheGatesOwnWords()
        {
            const string sentence = "adapter.json ready block is present but empty or unreadable, which fails ready on every " +
                                    "capture — nothing was written";
            foreach (var ready in new[] { "{ }", "{ \"mutee\": \"typo\" }", "{ \"note\": 5 }" })
            {
                var adapter = "{ \"gameId\": \"snl-scratch\", \"ready\": " + ready + " }";
                Assert.AreEqual(sentence, SyncNova.Refusal(Sent(TwoShots, adapter), "snl-scratch"), ready);
                Assert.AreEqual(sentence, Run(Sent(TwoShots, adapter)).Refusal, ready);
                Assert.IsFalse(Directory.Exists(RelayPaths.NovaDir(_root)), "written: " + ready);
            }
            // the gate says the same of each (the words are one constant, not a copy)
            var spec = HygieneSpec.Parse("{ \"ready\": { \"mutee\": \"typo\" } }");
            Assert.IsTrue(spec.Declared && !spec.HasAny, "control: the gate reads this block as declared and empty");
            // CONTROL: one command the gate reads — muteGet alone included — is enough
            Assert.IsNull(Run(Sent(TwoShots, "{ \"gameId\": \"snl-scratch\", \"ready\": { \"muteGet\": \"Music.mute\" } }")).Refusal);
        }

        /// <summary>
        /// The eleventh audit, M3 — A LINE BREAK IN <c>ready.muteGet</c>. muteGet is not a lever, so no lever rule saw it: the
        /// kit wrote <c>"A.b\nraw X"</c>, the site refuses it, and <c>HygieneReadyGate</c> would send <c>get A.b\nraw X</c>
        /// to a studio's own bridge as a "read". Every ready field is now held to the kit's one set of characters it will
        /// not take (<see cref="Levers.UnsendableCharacter"/>), by name, before anything is written.
        /// </summary>
        [Test]
        public void AReadyFieldHoldingAnUnsendableCharacterIsRefusedByNameMuteGetToo()
        {
            foreach (var (field, value, code) in new[]
                     {
                         ("muteGet", "A.b\nraw X", "U+000A"),
                         ("muteGet", "Music\u2028.mute", "U+2028"),
                         ("mute", "set Music.mute\u0085 true", "U+0085"),
                     })
            {
                var adapter = new JObject { ["gameId"] = "snl-scratch", ["ready"] = new JObject { [field] = value } }.ToString();
                var sentence = $"adapter.json's ready.{field} holds {code}, a line break, control or invisible formatting " +
                               "character, so the command the ready gate sends is not the one written — nothing was written";
                Assert.AreEqual(sentence, SyncNova.Refusal(Sent(TwoShots, adapter), "snl-scratch"), field);
                Assert.AreEqual(sentence, Run(Sent(TwoShots, adapter)).Refusal, field);
                Assert.IsFalse(Directory.Exists(RelayPaths.NovaDir(_root)), "written: " + field);
            }
            // CONTROL: the same field without the character is written
            Assert.IsNull(Run(Sent(TwoShots, new JObject { ["gameId"] = "snl-scratch", ["ready"] = new JObject { ["muteGet"] = "A.b" } }
                .ToString())).Refusal);
        }

        // ---- a person's edit --------------------------------------------------------------------

        [Test]
        public void ALocalEditIsBackedUpBeforeItIsReplaced()
        {
            Run(Sent(TwoShots, Adapter));
            File.WriteAllText(ShotsPath, "{ \"$schemaVersion\": 1, \"shots\": [] }  // mine");

            var result = Run(Sent(TwoShots, Adapter), runId: "run-2");

            Assert.IsTrue(result.ReplacedLocalEdits, "a person's edit was replaced without saying so");
            var backups = Directory.GetFiles(RelayPaths.NovaDir(_root), "shots.json.local-*.bak");
            Assert.AreEqual(1, backups.Length, "the edit was not backed up");
            StringAssert.Contains("// mine", File.ReadAllText(backups[0]));
            Assert.AreEqual(TwoShots, File.ReadAllText(ShotsPath));
        }

        [Test]
        public void AFileThisKitWroteItselfIsNotBackedUp()
        {
            Run(Sent(TwoShots, Adapter));
            // Same project, a NEW send: the file on disk is exactly what the last sync recorded, so
            // there is nothing of the studio's to preserve.
            const string other = @"{ ""$schemaVersion"": 1, ""shots"": [] }";
            var result = Run(Sent(other, Adapter), runId: "run-2");

            Assert.IsFalse(result.ReplacedLocalEdits);
            CollectionAssert.IsEmpty(Directory.GetFiles(RelayPaths.NovaDir(_root), "*.bak"));
            Assert.AreEqual(other, File.ReadAllText(ShotsPath));
        }

        [Test]
        public void AFileIdenticalToWhatIsSentIsLeftAloneEntirely()
        {
            // Never synced, but byte-identical to what is arriving: there is nothing to preserve and
            // nothing to write. Proven by the file's own timestamp, not by its contents.
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            File.WriteAllBytes(ShotsPath, new UTF8Encoding(false).GetBytes(TwoShots));
            var old = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(ShotsPath, old);

            var result = Run(Sent(TwoShots, Adapter));

            Assert.AreEqual(old, File.GetLastWriteTimeUtc(ShotsPath), "an identical file was rewritten");
            CollectionAssert.IsEmpty(Directory.GetFiles(RelayPaths.NovaDir(_root), "*.bak"));
            Assert.IsFalse(result.ReplacedLocalEdits);
        }

        [Test]
        public void AnEditedADAPTERIsBackedUpToo()
        {
            Run(Sent(TwoShots, Adapter));
            File.WriteAllText(AdapterPath, @"{ ""gameId"": ""snl-scratch"", ""note"": ""mine"" }");

            var result = Run(Sent(TwoShots, Adapter), runId: "run-2");

            Assert.IsTrue(result.ReplacedLocalEdits);
            Assert.AreEqual(1, Directory.GetFiles(RelayPaths.NovaDir(_root), "adapter.json.local-*.bak").Length);
        }

        // ---- levers ------------------------------------------------------------------------------

        [Test]
        public void TheFactsCarryTheLeversTheseFilesNeedAndTheOnesTicked()
        {
            Levers.SetApproved(_root, "SelectHero {hero}", true);
            var result = Run(Sent(TwoShots, Adapter));

            CollectionAssert.AreEqual(
                new[]
                {
                    "RollTargetType LargeCoin", "SelectHero {hero}", "call Player.Resist 1",
                    "get Music.mute", "set Music.mute true", "set Player.Resist 0", // the ready block's read, too (the fourteenth audit)
                },
                result.LeversNeeded.ToArray());
            CollectionAssert.AreEqual(new[] { "SelectHero {hero}" }, result.LeversApproved.ToArray());
        }

        [Test]
        public void LastSyncedReadsBackWhatWasWrittenAndIsNullBeforeAnySync()
        {
            var before = SyncNova.LastSynced(_root);
            Assert.IsNull(before.ShotsSha256);
            Assert.IsNull(before.AdapterSha256);

            Run(Sent(TwoShots, Adapter));
            var after = SyncNova.LastSynced(_root);
            Assert.AreEqual(SyncNova.Sha256OfText(TwoShots), after.ShotsSha256);
            Assert.AreEqual(SyncNova.Sha256OfText(Adapter), after.AdapterSha256);
        }

        [Test]
        public void AMalformedSyncedFileReadsAsNeverSynced()
        {
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            File.WriteAllText(SyncedPath, "{ not json");
            var read = SyncNova.LastSynced(_root);
            Assert.IsNull(read.ShotsSha256);
        }

        // ---- the facts document ------------------------------------------------------------------

        [Test]
        public void TheFactsJsonCarriesEveryFieldTheApiReads()
        {
            var result = Run(Sent(TwoShots, Adapter));
            var o = JObject.Parse(result.ToFactsJson("0.7.0"));

            CollectionAssert.AreEqual(new[] { "board_roll", "board_win" },
                o["shots"]!.Select(t => t.Value<string>()).ToArray());
            Assert.AreEqual(JTokenType.Array, o["shotLoadErrors"]!.Type);
            Assert.AreEqual("snl-scratch", o["gameId"]!.Value<string>());
            Assert.AreEqual(SyncNova.Sha256OfText(TwoShots), o["shotsSha256"]!.Value<string>());
            Assert.AreEqual(SyncNova.Sha256OfText(Adapter), o["adapterSha256"]!.Value<string>());
            Assert.AreEqual(JTokenType.Array, o["leversNeeded"]!.Type);
            Assert.AreEqual(JTokenType.Array, o["leversApproved"]!.Type);
            Assert.IsFalse(o["replacedLocalEdits"]!.Value<bool>());
            Assert.AreEqual("0.7.0", o["kitVersion"]!.Value<string>());
        }

        /// <summary>K3d — an adapter.json with no gameId names no game, so nothing can check that
        /// the right project was written. It is refused before anything is touched.</summary>
        [Test]
        public void AnAdapterThatNamesNoGameIdIsRefusedBeforeAnythingIsWritten()
        {
            Assert.AreEqual("adapter.json names no gameId", SyncNova.Refusal(Sent(TwoShots, "{ }"), null));
            Assert.AreEqual("adapter.json names no gameId",
                SyncNova.Refusal(Sent(TwoShots, @"{ ""gameId"": ""   "" }"), "snl-scratch"));
            Assert.AreEqual("adapter.json names no gameId",
                SyncNova.Refusal(Sent(TwoShots, @"{ ""gameId"": 7 }"), "snl-scratch"));

            var result = Run(Sent(TwoShots, "{ }"), jobGameId: null);
            Assert.AreEqual("adapter.json names no gameId", result.Refusal);
            Assert.IsFalse(File.Exists(ShotsPath));
            Assert.IsFalse(File.Exists(AdapterPath));
            Assert.IsFalse(File.Exists(SyncedPath));
        }

        [Test]
        public void TheFactsJsonReportsAMissingGameIdAsNull()
        {
            var o = JObject.Parse(new SyncNovaResult { GameId = null }.ToFactsJson("0.7.0"));
            Assert.AreEqual(JTokenType.Null, o["gameId"]!.Type);
        }

        /// <summary>
        /// K7 — THE WIRE. The server grades exactly these fields, by these names, in these JSON
        /// types (<c>apps/api/src/studio/studio-capture-job.ts</c> <c>SyncNovaFacts</c>). A rename
        /// or a dropped field here is a send that reads as a failure on the website with nothing
        /// wrong on the studio's disk, so it is pinned field by field rather than sampled.
        /// </summary>
        [Test]
        public void TheFactsJsonIsExactlyTheFieldsTheServerGrades()
        {
            var result = new SyncNovaResult
            {
                Shots = new[] { "a", "b" },
                ShotLoadErrors = new[] { "shots[0]: boom" },
                GameId = "snl-scratch",
                ShotsSha256 = "aa",
                AdapterSha256 = "bb",
                LeversNeeded = new[] { "set Coins 5", "timeScale" },
                LeversApproved = new[] { "timeScale" },
                ReplacedLocalEdits = true,
            };
            var o = JObject.Parse(result.ToFactsJson("0.7.0"));

            CollectionAssert.AreEqual(
                new[]
                {
                    "shots", "shotLoadErrors", "gameId", "shotsSha256", "adapterSha256",
                    "leversNeeded", "leversApproved", "replacedLocalEdits", "kitVersion",
                },
                o.Properties().Select(x => x.Name).ToArray(),
                "the facts document grew, lost or renamed a field the server reads");

            CollectionAssert.AreEqual(new[] { "a", "b" }, o["shots"]!.Select(t => t.Value<string>()).ToArray());
            Assert.AreEqual(JTokenType.Array, o["shots"]!.Type);
            CollectionAssert.AreEqual(new[] { "shots[0]: boom" },
                o["shotLoadErrors"]!.Select(t => t.Value<string>()).ToArray());
            Assert.AreEqual("snl-scratch", o["gameId"]!.Value<string>());
            Assert.AreEqual("aa", o["shotsSha256"]!.Value<string>());
            Assert.AreEqual("bb", o["adapterSha256"]!.Value<string>());
            CollectionAssert.AreEqual(new[] { "set Coins 5", "timeScale" },
                o["leversNeeded"]!.Select(t => t.Value<string>()).ToArray());
            CollectionAssert.AreEqual(new[] { "timeScale" },
                o["leversApproved"]!.Select(t => t.Value<string>()).ToArray());
            Assert.AreEqual(JTokenType.Boolean, o["replacedLocalEdits"]!.Type);
            Assert.IsTrue(o["replacedLocalEdits"]!.Value<bool>());
            Assert.AreEqual("0.7.0", o["kitVersion"]!.Value<string>());
            Assert.AreEqual("", JObject.Parse(result.ToFactsJson(null!))["kitVersion"]!.Value<string>());
        }

        // ---- K1: synced.json v2 — every pair the cloud has delivered ------------------------------

        private static string ShotsNamed(string name) =>
            "{ \"$schemaVersion\": 1, \"shots\": [ { \"name\": \"" + name +
            "\", \"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], " +
            "\"settle\": { \"kind\": \"present\", \"name\": \"X\" } } ] }";

        private JObject Synced() => JObject.Parse(File.ReadAllText(SyncedPath));

        private static string[] Strings(JToken? t) =>
            t is JArray a ? a.Select(x => x.Value<string>() ?? "").ToArray() : Array.Empty<string>();

        [Test]
        public void SyncedJsonIsVersion2AndRemembersEveryPairTheCloudHasDelivered()
        {
            Run(Sent(ShotsNamed("one"), Adapter));
            Run(Sent(ShotsNamed("two"), Adapter), runId: "run-2");

            var o = Synced();
            Assert.AreEqual(2, o["$schemaVersion"]!.Value<int>());
            Assert.AreEqual(SyncNova.Sha256OfText(ShotsNamed("two")), o["shotsSha256"]!.Value<string>());
            Assert.AreEqual(SyncNova.Sha256OfText(Adapter), o["adapterSha256"]!.Value<string>());
            Assert.AreEqual("run-2", o["runId"]!.Value<string>());
            CollectionAssert.AreEqual(
                new[] { SyncNova.Sha256OfText(ShotsNamed("one")), SyncNova.Sha256OfText(ShotsNamed("two")) },
                Strings(o["cloudShots"]));
            CollectionAssert.AreEqual(new[] { SyncNova.Sha256OfText(Adapter) }, Strings(o["cloudAdapters"]),
                "the same adapter sent twice is one entry, not two");
        }

        [Test]
        public void TheHistoryKeepsTheNEWEST20AndNoMore()
        {
            for (var i = 0; i < 22; i++)
                Run(Sent(ShotsNamed("s" + i), Adapter), runId: "run-" + i);

            var cloudShots = Strings(Synced()["cloudShots"]);
            Assert.AreEqual(20, cloudShots.Length);
            Assert.AreEqual(SyncNova.Sha256OfText(ShotsNamed("s21")), cloudShots[19], "the newest is last");
            Assert.AreEqual(SyncNova.Sha256OfText(ShotsNamed("s2")), cloudShots[0], "the oldest two are gone");
            CollectionAssert.DoesNotContain(cloudShots, SyncNova.Sha256OfText(ShotsNamed("s0")));
        }

        /// <summary>
        /// K1 — THE FAIL-OPEN. A HANDLED failure writing adapter.json leaves the cloud's shots.json
        /// on disk. Before this, synced.json still held the PREVIOUS pair, so the gate read the new
        /// file as a local edit and every command in it ran un-ticked.
        /// </summary>
        [Test]
        public void AFailureWritingTheAdapterLeavesTheGateON()
        {
            Assert.IsNull(Run(Sent(ShotsNamed("one"), Adapter)).Refusal);
            Assert.IsTrue(Levers.GateActive(_root), "control: the gate is live right after a send");

            File.Delete(AdapterPath);
            Directory.CreateDirectory(AdapterPath); // every write onto it now fails

            var second = Run(Sent(ShotsNamed("wipe"), Adapter), runId: "run-2");

            Assert.IsNotNull(second.Refusal);
            StringAssert.Contains("wipe", File.ReadAllText(ShotsPath), "the cloud's shots.json did land");
            Assert.IsTrue(Levers.GateActive(_root),
                "cloud bytes on disk with the gate OFF — an un-ticked cloud cheat would reach the game");
        }

        [Test]
        public void AFailureWritingSyncedJsonWritesNeitherFile()
        {
            Directory.CreateDirectory(SyncedPath); // synced.json cannot be written at all

            var result = Run(Sent(TwoShots, Adapter));

            Assert.IsNotNull(result.Refusal);
            StringAssert.Contains(SyncNova.SyncedFileName, result.Refusal!);
            Assert.IsFalse(File.Exists(ShotsPath), "a file was written although its record could not be");
            Assert.IsFalse(File.Exists(AdapterPath));
        }

        [Test]
        public void TheKitsOwnBytesFromAHalfFailedSendAreNotAPersonsEdit()
        {
            Run(Sent(ShotsNamed("one"), Adapter));
            File.Delete(AdapterPath);
            Directory.CreateDirectory(AdapterPath);
            Assert.IsNotNull(Run(Sent(ShotsNamed("two"), Adapter), runId: "run-2").Refusal);
            Directory.Delete(AdapterPath); // the obstruction is gone; send again

            var third = Run(Sent(ShotsNamed("three"), Adapter), runId: "run-3");

            Assert.IsNull(third.Refusal, third.Refusal);
            Assert.IsFalse(third.ReplacedLocalEdits,
                "the kit's own bytes from the half-failed send were reported to the studio as their edit");
            CollectionAssert.IsEmpty(Directory.GetFiles(RelayPaths.NovaDir(_root), "shots.json.local-*.bak"));
        }

        // ---- K4: a file that cannot be read cannot be backed up -----------------------------------

        private static void Chmod(string mode, string path)
        {
            using (var p = System.Diagnostics.Process.Start("chmod", mode + " \"" + path + "\""))
                p!.WaitForExit();
        }

        [Test]
        public void AFileThatExistsButCannotBeReadRefusesTheWholeSync()
        {
            Run(Sent(ShotsNamed("one"), Adapter));
            File.WriteAllText(ShotsPath, "{ \"mine\": true }");
            Chmod("000", ShotsPath);
            try
            {
                var result = Run(Sent(ShotsNamed("two"), Adapter), runId: "run-2");

                Assert.IsNotNull(result.Refusal, "an unreadable file was treated as absent and replaced");
                StringAssert.Contains(SyncNova.ShotsFileName, result.Refusal!);
                CollectionAssert.IsEmpty(Directory.GetFiles(RelayPaths.NovaDir(_root), "*.bak"),
                    "nothing could be backed up, so nothing may be written");
            }
            finally
            {
                Chmod("644", ShotsPath);
            }
            StringAssert.Contains("mine", File.ReadAllText(ShotsPath), "the person's file was replaced anyway");
        }

        // ---- M1 (second audit): a failed send over an UNREADABLE synced.json ----------------------

        private static readonly string AdapterMuteTrue =
            @"{ ""gameId"": ""snl-scratch"", ""ready"": { ""mute"": ""set Music.mute true"" } }";
        private static readonly string AdapterMuteFalse =
            @"{ ""gameId"": ""snl-scratch"", ""ready"": { ""mute"": ""set Music.mute false"" } }";

        /// <summary>
        /// The M1 scenario, staged once: this project has been delivered to, its synced.json has
        /// since become unreadable, and the next send fails half-way (its shots.json target is a
        /// directory, the way every other failure in this suite is staged).
        /// </summary>
        private SyncNovaResult AFailedSendOverAnUnreadableRecord()
        {
            Assert.IsNull(Run(Sent(ShotsNamed("one"), AdapterMuteTrue)).Refusal);
            File.WriteAllText(SyncedPath, "{ not json");
            Assert.IsTrue(Levers.GateActive(_root), "control: an unreadable synced.json gates");

            File.Delete(ShotsPath);
            Directory.CreateDirectory(ShotsPath);
            Assert.IsTrue(Levers.GateActive(_root), "control: still gated before the send");

            var second = Run(Sent(ShotsNamed("two"), AdapterMuteFalse), runId: "run-2");
            Assert.IsNotNull(second.Refusal, "control: the send failed");
            StringAssert.Contains("set Music.mute true", File.ReadAllText(AdapterPath),
                "control: the cloud's FIRST adapter.json is still the one on disk");
            return second;
        }

        /// <summary>
        /// M1 — an unreadable synced.json read as an EMPTY history, and the next send then recorded
        /// only the incoming pair. When that send failed, the CLOUD's earlier files were still on
        /// disk with nothing in any history naming them: the gate was live before the send and open
        /// after it, which is the one direction a damaged record must never move in.
        /// </summary>
        [Test]
        public void AFailedSendOverAnUnreadableSyncedJsonLeavesTheGateON()
        {
            AFailedSendOverAnUnreadableRecord();

            Assert.IsTrue(Levers.GateActive(_root),
                "the cloud's own adapter.json is on disk, un-edited, and its ready block is un-gated");
        }

        /// <summary>
        /// The other half of M1: nothing knows whose bytes those are, so the copy is taken anyway.
        /// Treating them as the cloud's for the BACKUP decision as well would silently replace a
        /// person's file with no copy of it, the moment a synced.json went bad.
        /// </summary>
        [Test]
        public void AFailedSendOverAnUnreadableSyncedJsonStillCopiesWhatItIsAboutToReplace()
        {
            AFailedSendOverAnUnreadableRecord();

            Assert.AreEqual(1,
                Directory.GetFiles(RelayPaths.NovaDir(_root), "adapter.json.local-*.bak").Length,
                "a file of unknown provenance was replaced with no copy of it kept");
        }

        /// <summary>
        /// …and it is NOT reported as a person's edit. The claim outlives the run in synced.json —
        /// a retry of the same send reads it back (M5) — so a file the cloud itself put here used
        /// to come back to the studio as "we replaced something you wrote".
        /// </summary>
        [Test]
        public void AFailedSendOverAnUnreadableSyncedJsonClaimsNoPersonsEdit()
        {
            AFailedSendOverAnUnreadableRecord();

            Assert.IsFalse(Synced()["replacedLocalEdits"]!.Value<bool>(),
                "the cloud's own earlier file was recorded as a person's edit");
        }

        // ---- M5 (second audit): a retried result POST ---------------------------------------------

        /// <summary>
        /// M5 — the agent re-runs the whole of SyncNova.Run whenever the result POST has to be
        /// retried (a network failure, a resumed job). The second run sees its own bytes, so the
        /// studio was never told that a file they had edited was replaced.
        /// </summary>
        [Test]
        public void ARetriedRunStillReportsThatItReplacedAPersonsEdit()
        {
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            File.WriteAllText(ShotsPath, "{ \"mine\": true }");

            var first = Run(Sent(ShotsNamed("one"), Adapter), runId: "run-7");
            Assert.IsTrue(first.ReplacedLocalEdits, "control: the first run backed the edit up");

            var retry = Run(Sent(ShotsNamed("one"), Adapter), runId: "run-7");
            Assert.IsTrue(retry.ReplacedLocalEdits,
                "the retry of the SAME run forgot it had replaced a person's file — the page never says so");

            var later = Run(Sent(ShotsNamed("two"), Adapter), runId: "run-8");
            Assert.IsFalse(later.ReplacedLocalEdits,
                "a DIFFERENT send must answer about itself, not inherit the last send's answer");
        }

        // ---- K14: the reported shas are read back, never echoed -----------------------------------

        /// <summary>
        /// K14 — the old test sent the same bytes twice, so an ECHO of the input and a READ-BACK
        /// from disk were equal by construction and it could not fail (invariant 100). Here the
        /// bytes that land differ from the bytes that were sent, which is the only way to tell the
        /// two apart.
        /// </summary>
        [Test]
        public void TheReportedShasAreReadBackFromDiskAndNotEchoedFromTheInput()
        {
            SyncNovaResult result;
            // A fresh project: every file lands by a MOVE (nothing is there to replace), and this
            // move lands one byte more than it was given.
            AtomicFile.MoveForTests = (tmp, dest) =>
            {
                var bytes = File.ReadAllBytes(tmp);
                File.Delete(tmp);
                File.WriteAllBytes(dest, bytes.Concat(new byte[] { (byte)'\n' }).ToArray());
            };
            try
            {
                result = Run(Sent(TwoShots, Adapter));
            }
            finally
            {
                AtomicFile.MoveForTests = null;
            }

            Assert.IsNull(result.Refusal, result.Refusal);
            Assert.AreEqual(SyncNova.Sha256OfFile(ShotsPath), result.ShotsSha256);
            Assert.AreEqual(SyncNova.Sha256OfFile(AdapterPath), result.AdapterSha256);
            Assert.AreNotEqual(SyncNova.Sha256OfText(TwoShots), result.ShotsSha256,
                "the facts echoed the sha that arrived instead of hashing what is on disk");
            Assert.AreNotEqual(SyncNova.Sha256OfText(Adapter), result.AdapterSha256);
            Assert.AreEqual(result.ShotsSha256, Synced()["shotsSha256"]!.Value<string>(),
                "synced.json must name the same bytes the facts do");
        }

        // ---- K2: a write that fails leaves the OLD file whole -------------------------------------

        /// <summary>
        /// K2 — "temp-then-replace, never truncate-in-place" had no test at all: every failure this
        /// suite can stage (the target is a directory) fails both ways, and no input makes a delete
        /// succeed and the move after it fail. The failure is staged AT the one OS call the kit
        /// makes over an existing file — <c>File.Replace</c>, behind <c>AtomicFile.ReplaceForTests</c>
        /// (third audit: the second audit's seam replaced the whole swap, so its body never ran) —
        /// and what is asserted is what matters: when it fails, the old shots.json is still there,
        /// whole, with its bytes, and nothing was moved over it.
        /// </summary>
        [Test]
        public void WhenTheSwapFailsTheOldShotsFileIsStillThereAndWhole()
        {
            Assert.IsNull(Run(Sent(ShotsNamed("one"), Adapter)).Refusal);
            var before = File.ReadAllBytes(ShotsPath);
            var oldFileWasStillThere = false;
            var movedOntoShots = false;

            AtomicFile.ReplaceForTests = (tmp, dest) =>
            {
                if (string.Equals(dest, ShotsPath, StringComparison.Ordinal))
                {
                    oldFileWasStillThere = File.Exists(dest) && File.ReadAllBytes(dest).SequenceEqual(before);
                    throw new IOException("the replace was refused");
                }
                File.Replace(tmp, dest, null);
            };
            AtomicFile.MoveForTests = (tmp, dest) =>
            {
                if (string.Equals(dest, ShotsPath, StringComparison.Ordinal)) movedOntoShots = true;
                File.Move(tmp, dest);
            };
            SyncNovaResult second;
            try
            {
                second = Run(Sent(ShotsNamed("two"), Adapter), runId: "run-2");
            }
            finally
            {
                AtomicFile.ReplaceForTests = null;
                AtomicFile.MoveForTests = null;
            }

            Assert.IsNotNull(second.Refusal, "a swap that failed must come back as a named refusal");
            StringAssert.Contains(SyncNova.ShotsFileName, second.Refusal!);
            Assert.IsTrue(oldFileWasStillThere,
                "the old shots.json was already gone when the new one was put in its place");
            Assert.IsFalse(movedOntoShots, "a failed replace fell back to a move over the old file");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(ShotsPath),
                "the studio lost the file that was there");
            CollectionAssert.IsEmpty(
                Directory.GetFiles(RelayPaths.NovaDir(_root)).Where(f => f.Contains(".tmp-")).ToArray(),
                "the temp file outlived the attempt");
        }

        // ---- K19 / K20: what the history says, and when ------------------------------------------

        [Test]
        public void ARESENTPairIsMovedToTheENDOfTheHistory()
        {
            Run(Sent(ShotsNamed("one"), Adapter));
            Run(Sent(ShotsNamed("two"), Adapter), runId: "run-2");
            Run(Sent(ShotsNamed("one"), Adapter), runId: "run-3");

            CollectionAssert.AreEqual(
                new[] { SyncNova.Sha256OfText(ShotsNamed("two")), SyncNova.Sha256OfText(ShotsNamed("one")) },
                Strings(Synced()["cloudShots"]),
                "the pair that is ON DISK sat at the oldest end of its own history, so it ages out " +
                "first and stops being gated while it is still the file the cloud put here");
        }

        [Test]
        public void TheRecordNeverClaimsTheINCOMINGPairBeforeItIsWritten()
        {
            Assert.IsNull(Run(Sent(ShotsNamed("one"), Adapter)).Refusal);
            var firstPair = (shots: SyncNova.Sha256OfText(ShotsNamed("one")),
                adapter: SyncNova.Sha256OfText(Adapter));

            File.Delete(AdapterPath);
            Directory.CreateDirectory(AdapterPath); // the adapter write will fail

            var second = Run(Sent(ShotsNamed("two"), Adapter), runId: "run-2");
            Assert.IsNotNull(second.Refusal, "control: the send failed");

            var o = Synced();
            Assert.AreEqual(firstPair.shots, o["shotsSha256"]!.Value<string>(),
                "step 3 recorded the incoming pair as the pair on disk before anything was written");
            Assert.AreEqual(firstPair.adapter, o["adapterSha256"]!.Value<string>());
            Assert.AreNotEqual(SyncNova.Sha256OfText(ShotsNamed("two")), o["shotsSha256"]!.Value<string>());
            CollectionAssert.Contains(Strings(o["cloudShots"]), SyncNova.Sha256OfText(ShotsNamed("two")),
                "the HISTORY is where an incoming pair goes before it is written — that is the gate");
        }

        // ---- K32: both files edited here ----------------------------------------------------------

        [Test]
        public void BOTHFilesEditedOnThisMachineAreBOTHBackedUp()
        {
            Run(Sent(TwoShots, Adapter));
            File.WriteAllText(ShotsPath, @"{ ""$schemaVersion"": 1, ""shots"": [] }  // mine");
            File.WriteAllText(AdapterPath, @"{ ""gameId"": ""snl-scratch"", ""note"": ""mine too"" }");

            var result = Run(Sent(TwoShots, Adapter), runId: "run-2");

            Assert.IsTrue(result.ReplacedLocalEdits);
            var shotsBaks = Directory.GetFiles(RelayPaths.NovaDir(_root), "shots.json.local-*.bak");
            var adapterBaks = Directory.GetFiles(RelayPaths.NovaDir(_root), "adapter.json.local-*.bak");
            Assert.AreEqual(1, shotsBaks.Length, "the edited shots.json was not copied");
            Assert.AreEqual(1, adapterBaks.Length,
                "the edited adapter.json was not copied — one flag is not two files");
            StringAssert.Contains("// mine", File.ReadAllText(shotsBaks[0]));
            StringAssert.Contains("mine too", File.ReadAllText(adapterBaks[0]));
        }

        // ---- S2 (third audit, surviving mutant): M1's SHOTS half ----------------------------------

        /// <summary>
        /// P4 (the auditor's probe, kept). The second audit's M1 tests stage the failure with
        /// shots.json as a DIRECTORY, so the shots file on disk is never readable and M1's shots
        /// line — "an unreadable record puts the shots file on disk into the history" — could be
        /// deleted with every test green. Here the cloud's shots.json IS on disk, adapter.json is
        /// NOT (so only the shots half can keep the gate on), the record is unreadable, and the
        /// shots replace fails.
        /// </summary>
        [Test]
        public void AFailedShotsWriteOverAnUnreadableRecordLeavesTheCloudsShotsGated()
        {
            var first = ShotsNamed("one");
            Assert.IsNull(Run(Sent(first, Adapter)).Refusal);
            File.Delete(AdapterPath);
            File.WriteAllText(SyncedPath, "{ not json");
            Assert.IsTrue(Levers.GateActive(_root), "control: an unreadable record gates");

            AtomicFile.ReplaceForTests = (tmp, dest) =>
            {
                if (string.Equals(dest, ShotsPath, StringComparison.Ordinal))
                    throw new IOException("staged");
                File.Replace(tmp, dest, null);
            };
            SyncNovaResult second;
            try
            {
                second = Run(Sent(ShotsNamed("two"), Adapter), runId: "run-2");
            }
            finally
            {
                AtomicFile.ReplaceForTests = null;
            }

            Assert.IsNotNull(second.Refusal, "control: the send failed");
            CollectionAssert.AreEqual(SyncNova.Utf8NoBom(first), File.ReadAllBytes(ShotsPath),
                "control: the cloud's FIRST shots.json is still on disk");
            Assert.IsFalse(File.Exists(AdapterPath), "control: no adapter.json — only the shots half can gate");
            Assert.IsTrue(Levers.GateActive(_root), "the cloud's own shots.json is on disk and the gate is OFF");
        }

        /// <summary>
        /// The eleventh audit, M1 — A LEVER LONGER THAN <see cref="Levers.MaxLeverLength"/> IS REFUSED, by name, before
        /// anything is written. The window's cost grows with needed × ticked × lever length; the lever cap bounded the
        /// first two and the byte cap let a lever reach about 1.7 KB, where the worst shape at the cap took 925–980 ms. The
        /// length is the lever's own text, prefix and all (<c>hide-overlay </c> is 13 of an overlay lever's characters).
        /// </summary>
        [Test]
        public void ALeverLongerThanTheCapIsRefusedByNameAndNothingIsWritten()
        {
            Assert.AreEqual(256, Levers.MaxLeverLength, "the number the fixture and the site share");
            string ShotsWithOneSetup(string lever) => new JObject
            {
                ["$schemaVersion"] = 1,
                ["shots"] = new JArray(new JObject
                {
                    ["name"] = "a", ["setup"] = new JArray(lever),
                    ["steps"] = new JArray(new JObject { ["kind"] = "wait", ["seconds"] = 1 }),
                    ["settle"] = new JObject { ["kind"] = "present", ["name"] = "X" },
                }),
            }.ToString();
            var over = "raw " + new string('x', Levers.MaxLeverLength + 1 - 4);
            Assert.AreEqual(Levers.MaxLeverLength + 1, over.Length, "control");
            Assert.AreEqual("the lever \"raw " + new string('x', 36) + "…\" is 257 characters long, and this kit takes levers of " +
                            "at most 256 — nothing was written", Run(Sent(ShotsWithOneSetup(over), Adapter)).Refusal);
            Assert.IsFalse(Directory.Exists(RelayPaths.NovaDir(_root)), "a lever over the cap was written");

            // an overlay lever counts its prefix: a 244-character type name is a 257-character lever
            var overlay = new JObject { ["gameId"] = "snl-scratch", ["overlayTypeNames"] = new JArray(new string('H', 244)) }.ToString();
            Assert.AreEqual("the lever \"hide-overlay " + new string('H', 27) + "…\" is 257 characters long, and this kit takes " +
                            "levers of at most 256 — nothing was written", Run(Sent(TwoShots, overlay)).Refusal);
            Assert.IsFalse(Directory.Exists(RelayPaths.NovaDir(_root)), "an overlay lever over the cap was written");

            // CONTROLS: at exactly the cap, both are written
            Assert.IsNull(Run(Sent(ShotsWithOneSetup(over.Substring(1)), Adapter)).Refusal);
            Assert.IsNull(Run(Sent(TwoShots, new JObject { ["gameId"] = "snl-scratch", ["overlayTypeNames"] = new JArray(new string('H', 243)) }
                .ToString()), runId: "run-2").Refusal);
        }

        /// <summary>
        /// The fourteenth audit, S1 and ruling 3 — <c>ready.muteGet</c> WAS CAPPED BY NOTHING BUT THE 64 KB FILE: it was not a
        /// lever, so no lever rule saw it, and the ready gate sent `get` + it through the gate un-ticked (51 s in one pump at
        /// 2 KB). It is a lever now, as the command the ready gate sends, `get &lt;muteGet&gt;`, so it counts its prefix like an
        /// overlay lever, and <c>ReadAdapter</c> holds every ready field's command to <see cref="Levers.MaxLeverLength"/>
        /// by name, in the lever cap's own words, before anything is written. <c>deliveries.cases.json</c> holds the site to
        /// the same two numbers.
        /// </summary>
        [Test]
        public void AReadyMuteGetIsHeldToTheLeverCapAsTheGetItSends()
        {
            string Ready(string field, string value) =>
                new JObject { ["gameId"] = "snl-scratch", ["ready"] = new JObject { [field] = value } }.ToString();
            var over = "A." + new string('b', Levers.MaxLeverLength - 4 + 1 - 2); // 253 characters: `get ` makes 257
            Assert.AreEqual("adapter.json's ready.muteGet: the lever \"get A." + new string('b', 34) + "…\" is 257 characters long, " +
                            "and this kit takes levers of at most 256 — nothing was written", Run(Sent(TwoShots, Ready("muteGet", over))).Refusal);
            Assert.IsFalse(Directory.Exists(RelayPaths.NovaDir(_root)), "a muteGet over the cap was written");
            var mute = "set " + new string('m', Levers.MaxLeverLength + 1 - 4);
            Assert.AreEqual("adapter.json's ready.mute: the lever \"set " + new string('m', 36) + "…\" is 257 characters long, " +
                            "and this kit takes levers of at most 256 — nothing was written", Run(Sent(TwoShots, Ready("mute", mute))).Refusal);

            // CONTROL: at exactly the cap (252 characters, a 256-character `get`) it is written, and listed as that lever
            var at = over.Substring(0, over.Length - 1);
            var result = Run(Sent(TwoShots, Ready("muteGet", at)), runId: "run-2");
            Assert.IsNull(result.Refusal);
            CollectionAssert.Contains(result.LeversNeeded.ToArray(), "get " + at);
        }

        /// <summary>
        /// The eleventh audit, M2 — THE ADAPTER HALF OF THE LEVER COUNT IS LINEAR. <c>Levers.NeededFromAdapter</c> kept its
        /// distinct list with <c>List.Contains</c> (the ninth fold moved only the shots half to a set), so a 64 KB adapter.json
        /// of 10,905 distinct three-character overlay names took 860 ms to count before it was refused. The auditor's shape,
        /// through the real <see cref="SyncNova.Run"/>, timed warm.
        /// </summary>
        [Test]
        public void AnAdapterOfManyOverlayNamesIsCountedInLinearTime()
        {
            const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            string Name(int i) => new string(new[] { letters[i / (52 * 52)], letters[i / 52 % 52], letters[i % 52] });
            string AdapterOf(int n) => new JObject
            {
                ["gameId"] = "snl-scratch",
                ["overlayTypeNames"] = new JArray(Enumerable.Range(0, n).Select(i => (object)Name(i)).ToArray()),
            }.ToString(Newtonsoft.Json.Formatting.None);
            var empty = AdapterOf(0).Length;
            var n = (SyncNova.MaxAdapterBytes - empty + 1) / 6; // each name is "abc" and a comma
            var adapter = AdapterOf(n);
            Assert.LessOrEqual(adapter.Length, SyncNova.MaxAdapterBytes, "control: under the adapter cap");
            Assert.Greater(adapter.Length, SyncNova.MaxAdapterBytes - 6, "control: the cap is filled");

            Run(Sent(TwoShots, AdapterOf(400)), runId: "warm"); // JIT, off the clock
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var refusal = Run(Sent(TwoShots, adapter)).Refusal;
            watch.Stop();
            TestContext.WriteLine($"SyncNova.Run over {n} overlay names ({adapter.Length} bytes): {watch.ElapsedMilliseconds} ms");
            Assert.AreEqual($"these files ask for {n + 2} levers, and this kit takes at most {Levers.MaxLevers} in one send — " +
                            "nothing was written", refusal);
            Assert.Less(watch.ElapsedMilliseconds, 250, $"counting {n} overlay levers took {watch.ElapsedMilliseconds} ms");
        }

        /// <summary>The ninth audit, S1 (ruling 1): the kit cannot know that the server it talks to applied the API's send
        /// caps, and the window's cost grows with what the files ask for. A file over its cap is refused, by name, before
        /// anything is written; a file at exactly its cap is taken.</summary>
        [Test]
        public void AFileOverTheApisSendCapIsRefusedAndNothingIsWritten()
        {
            // one shot, padded with a note to an exact byte count
            string ShotsOf(int bytes)
            {
                var head = "{ \"$schemaVersion\": 1, \"note\": \"";
                var tail = "\", \"shots\": [ { \"name\": \"a\", \"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], " +
                           "\"settle\": { \"kind\": \"present\", \"name\": \"X\" } } ] }";
                return head + new string('x', bytes - head.Length - tail.Length) + tail;
            }
            string AdapterOf(int bytes)
            {
                var head = "{ \"gameId\": \"snl-scratch\", \"note\": \"";
                return head + new string('x', bytes - head.Length - 3) + "\" }";
            }
            Assert.AreEqual(SyncNova.MaxShotsBytes + 1, SyncNova.Utf8NoBom(ShotsOf(SyncNova.MaxShotsBytes + 1)).Length, "control");
            Assert.AreEqual(SyncNova.MaxAdapterBytes + 1, SyncNova.Utf8NoBom(AdapterOf(SyncNova.MaxAdapterBytes + 1)).Length, "control");

            Assert.AreEqual($"shots.json is {SyncNova.MaxShotsBytes + 1} bytes, and a send may carry at most {SyncNova.MaxShotsBytes} " +
                            "— nothing was written", Run(Sent(ShotsOf(SyncNova.MaxShotsBytes + 1), Adapter)).Refusal);
            Assert.AreEqual($"adapter.json is {SyncNova.MaxAdapterBytes + 1} bytes, and a send may carry at most " +
                            $"{SyncNova.MaxAdapterBytes} — nothing was written",
                Run(Sent(TwoShots, AdapterOf(SyncNova.MaxAdapterBytes + 1))).Refusal);
            Assert.IsFalse(Directory.Exists(RelayPaths.NovaDir(_root)), "a file was written, or Library/Nova made, for a refused send");

            // CONTROL: at exactly the caps, the pair is written
            var atCap = Run(Sent(ShotsOf(SyncNova.MaxShotsBytes), AdapterOf(SyncNova.MaxAdapterBytes)));
            Assert.IsNull(atCap.Refusal);
            Assert.AreEqual(SyncNova.MaxShotsBytes, new FileInfo(ShotsPath).Length);
            Assert.AreEqual(SyncNova.MaxAdapterBytes, new FileInfo(AdapterPath).Length);
        }
    }
}
