using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Slice E.1 — "Try this shot" (the <c>probe</c> job), everything that can be decided without
    /// Play Mode: the claim block, the sha check, the one-shot load through the REAL loader, the
    /// lever pre-check, the gate FORCED on for cloud content, the director's failure facts and stop
    /// hook, the facts document, the progress file. Real temp project roots and the real director
    /// throughout (invariant 101): what runs here is what runs in production.
    /// </summary>
    public class ProbeTests
    {
        private string _root = "";

        /// <summary>The cloud's shot: one setup write, one cheat, one click, settles on Board.
        /// Its levers, sorted ordinal: "SelectHero Knight" &lt; "set Coins 5".</summary>
        private const string CheatShot = @"{ ""name"": ""try-me"", ""setup"": [""set Coins 5""],
  ""steps"": [ { ""kind"": ""cheat"", ""command"": ""SelectHero Knight"" },
               { ""kind"": ""click"", ""name"": ""Go"" } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";

        private const string PlainAdapter = @"{ ""gameId"": ""g"" }";

        /// <summary>The STUDIO'S OWN files — authored on this machine, never synced.</summary>
        private const string OwnShots = @"{ ""$schemaVersion"": 1, ""shots"": [
  { ""name"": ""mine"", ""steps"": [ { ""kind"": ""click"", ""name"": ""Go"" } ],
    ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } } ] }";
        private const string OwnAdapter = @"{ ""gameId"": ""g"" }";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            // A try runs only on a project whose adapter.json IS the one the site sent (audit S3):
            // every test starts from a project that was sent this adapter, byte for byte.
            OnDisk(_root, PlainAdapter);
        }

        /// <summary>This project's Library/Nova/adapter.json, as a send writes it (UTF-8, no BOM).</summary>
        private static void OnDisk(string root, string adapter) =>
            File.WriteAllBytes(SyncNova.AdapterFile(root), new UTF8Encoding(false).GetBytes(adapter));

        [TearDown]
        public void TearDown()
        {
            AdDirector.Active?.Finish("test teardown");
            LeverGateBridge.ForgetRefused();
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        // ---- helpers -----------------------------------------------------------------------------

        private static ProbeRequest Sent(string shot, string adapter, string? shotSha = null,
            string? adapterSha = null) =>
            new(shot, adapter, shotSha ?? SyncNova.Sha256OfText(shot),
                adapterSha ?? SyncNova.Sha256OfText(adapter));

        private void WriteOwnFiles()
        {
            File.WriteAllBytes(RelayPaths.NovaShotsFile(_root), new UTF8Encoding(false).GetBytes(OwnShots));
            File.WriteAllBytes(SyncNova.AdapterFile(_root), new UTF8Encoding(false).GetBytes(OwnAdapter));
        }

        private void Tick(params string[] levers)
        {
            foreach (var l in levers)
                Assert.IsNull(Levers.SetApproved(_root, l, true), "could not tick " + l);
        }

        private sealed class FakeUi : IUiDriver
        {
            public readonly HashSet<string> Names = new() { "Board", "Go" };
            public bool Exists(string name) => Names.Contains(name);
            public bool IsInteractable(string name) => Names.Contains(name);
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) => Names.Contains(name);
            public void PointerDown(string name, int index = 0) { }
            public void PointerUp(string name, int index = 0) { }
        }

        private sealed class RecordingBridge : ICheatBridge
        {
            public readonly List<string> Run_ = new();
            public bool Run(string command) { Run_.Add(command); return true; }
        }

        /// <summary>A recorder that says when it was on — for the stop-hook tests, which need to
        /// see the hook run BEFORE the recorder stops.</summary>
        private sealed class WatchedRecorder : IRecorderDriver
        {
            public string OutputDir { get; }
            public bool IsRecording { get; private set; }
            public int Stops;
            public Action? OnStop;
            public WatchedRecorder(string dir) { OutputDir = dir; }
            public int OutputWidth => 0;
            public int OutputHeight => 0;
            public void Start(string clipName) => IsRecording = true;
            public void Stop()
            {
                OnStop?.Invoke();
                IsRecording = false;
                Stops++;
            }
        }

        private sealed class NeverReadyGate : IReadyGate
        {
            public IEnumerable WaitUntilReady(DirectorContext ctx)
            {
                ctx.LastOpSucceeded = false;
                yield break;
            }
        }

        /// <summary>Everything the director sent the game that is not one of the read-only shapes
        /// (the director opens every run with `show-ui`, which must keep working).</summary>
        private static string[] Writes(RecordingBridge b) => b.Run_.Where(c => !Levers.IsReadOnly(c)).ToArray();

        private static ProbePlan Loaded(string root, string shotText, string adapter = PlainAdapter)
        {
            OnDisk(root, adapter);
            var plan = ProbePlan.Prepare(root, Sent(shotText, adapter));
            Assert.IsNull(plan.Refusal, plan.Refusal);
            Assert.IsNotNull(plan.Shot);
            return plan;
        }

        /// <summary>Drive a director built the way the agent builds a probe's
        /// (<see cref="ProbeRun.DirectorOptions"/>), pumped by hand.</summary>
        private (AdDirector? d, RecordingBridge cheats, FakeUi ui) RunDirector(ProbePlan plan,
            Func<IEnumerable>? hook = null, IReadyGate? gate = null,
            Action<AdDirector.Options>? tweak = null, bool bypassPlan = false,
            Action<AdDirector>? midRun = null, RecoveryThatRevealsLate? recovery = null,
            Action<GameAdapter>? adapterTweak = null)
        {
            var ui = new FakeUi();
            var cheats = new RecordingBridge();
            if (recovery != null) recovery.Ui = ui;
            var adapter = new GameAdapter
            {
                GameId = "g",
                Ui = ui,
                CheatBridge = cheats,
                ReadyGate = gate ?? NullReadyGate.Instance,
                Recovery = recovery ?? (IRecoveryPolicy)NullRecoveryPolicy.Instance,
            };
            adapterTweak?.Invoke(adapter);
            var clock = 0.0;
            var options = ProbeRun.DirectorOptions(_root, hook);
            options.AutoPump = false;
            options.Now = () => clock;
            options.ReleaseCameraHold = () => { };
            tweak?.Invoke(options);
            var d = bypassPlan
                ? AdDirector.Run(adapter, new[] { plan.Shot! }, options)
                : ProbeRun.Start(adapter, plan, options);
            if (d == null) return (null, cheats, ui);
            var ticks = 0;
            while (!d.IsFinished && ticks++ < 100_000)
            {
                d.PumpOnce();
                clock += 0.1;
                if (ticks == 3) midRun?.Invoke(d);
            }
            Assert.IsTrue(d.IsFinished, "director never finished");
            return (d, cheats, ui);
        }

        private static string Shot(string name, string steps, string settle = @"{ ""kind"": ""present"", ""name"": ""Board"" }",
            string extra = "") =>
            @"{ ""name"": """ + name + @""", ""steps"": [ " + steps + @" ], ""settle"": " + settle + extra + " }";

        // ---- the claim --------------------------------------------------------------------------

        [Test]
        public void ParseClaim_ReadsTheProbeBlock()
        {
            var job = CaptureJob.ParseClaim(
                @"{ ""job"": { ""runId"": ""r1"", ""workspaceId"": ""w"", ""kind"": ""probe"", ""gameId"": ""g"" },
                    ""leaseUntil"": ""x"",
                    ""probe"": { ""shot"": ""S"", ""adapter"": ""A"", ""shotSha256"": ""aa"", ""adapterSha256"": ""bb"" } }",
                out var error, out var files, out var probe);
            Assert.IsNull(error);
            Assert.AreEqual(CaptureJob.ProbeKind, job!.Kind);
            Assert.IsNull(files, "a probe carries no sync-nova files");
            Assert.IsNotNull(probe);
            Assert.AreEqual("S", probe!.Shot);
            Assert.AreEqual("A", probe.Adapter);
            Assert.AreEqual("aa", probe.ShotSha256);
            Assert.AreEqual("bb", probe.AdapterSha256);
        }

        [Test]
        public void ParseClaim_AProbeWithNoBlockStillParses_AndIsRefusedByName()
        {
            // It must PARSE, so it reaches the handler and is reported through `done` — a claim that
            // failed to parse would sit "running in your editor" until the lease lapsed.
            var job = CaptureJob.ParseClaim(
                @"{ ""job"": { ""runId"": ""r1"", ""kind"": ""probe"" }, ""leaseUntil"": ""x"" }",
                out var error, out _, out var probe);
            Assert.IsNull(error);
            Assert.IsNotNull(job);
            Assert.IsNull(probe);
            var plan = ProbePlan.Prepare(_root, probe);
            Assert.AreEqual(ProbeRequest.MissingReason, plan.Refusal);
            Assert.IsFalse(plan.MayRun);
        }

        [Test]
        public void ParseClaim_AnIncompleteProbeBlockIsNoScriptAtAll()
        {
            foreach (var partial in new[]
                     {
                         @"{ ""shot"": ""S"", ""adapter"": ""A"", ""shotSha256"": ""aa"" }",
                         @"{ ""shot"": """", ""adapter"": ""A"", ""shotSha256"": ""aa"", ""adapterSha256"": ""bb"" }",
                         @"{ ""shot"": { ""name"": ""x"" }, ""adapter"": ""A"", ""shotSha256"": ""aa"", ""adapterSha256"": ""bb"" }",
                         @"{ ""shot"": ""S"", ""adapter"": 3, ""shotSha256"": ""aa"", ""adapterSha256"": ""bb"" }",
                         @"[]",
                         @"null",
                     })
            {
                CaptureJob.ParseClaim(
                    @"{ ""job"": { ""runId"": ""r1"", ""kind"": ""probe"" }, ""probe"": " + partial + " }",
                    out var error, out _, out var probe);
                Assert.IsNull(error, partial);
                Assert.IsNull(probe, "a half-delivered script must never run: " + partial);
            }
        }

        [Test]
        public void ParseClaim_AProbeBlockOnAnotherKindIsIgnored_AndTheOlderOverloadsAreUnchanged()
        {
            CaptureJob.ParseClaim(
                @"{ ""job"": { ""runId"": ""r1"", ""kind"": ""self-test"" },
                    ""probe"": { ""shot"": ""S"", ""adapter"": ""A"", ""shotSha256"": ""aa"", ""adapterSha256"": ""bb"" } }",
                out _, out _, out var probe);
            Assert.IsNull(probe);

            var sync = CaptureJob.ParseClaim(
                @"{ ""job"": { ""runId"": ""r2"", ""kind"": ""sync-nova"" },
                    ""files"": { ""shots"": ""S"", ""adapter"": ""A"", ""shotsSha256"": ""aa"", ""adapterSha256"": ""bb"" } }",
                out var e, out var files);
            Assert.IsNull(e);
            Assert.AreEqual("r2", sync!.RunId);
            Assert.IsNotNull(files);
        }

        [Test]
        public void JobGuard_AProbeIsHandled_AndIsRefusedForTheWrongGame()
        {
            Assert.IsTrue(CaptureJob.IsHandled(CaptureJob.ProbeKind));
            Assert.AreEqual("probe", CaptureJob.ProbeKind, "the wire's spelling");
            // NOT exempt from the capture's identity rule: it runs a script in the studio's game.
            StringAssert.Contains("wrong game", JobGuard.RefusalReason("probe", "askie", "snl-scratch"));
            StringAssert.Contains("cannot verify", JobGuard.RefusalReason("probe", "askie", null));
            Assert.IsNull(JobGuard.RefusalReason("probe", "snl-scratch", "snl-scratch"));
        }

        // ---- the shas ------------------------------------------------------------------------------

        [Test]
        public void Prepare_AShotShaMismatchIsRefusedBeforeAnyUse()
        {
            Tick("SelectHero Knight", "set Coins 5");
            var plan = ProbePlan.Prepare(_root, Sent(CheatShot, PlainAdapter, shotSha: new string('0', 64)));
            Assert.IsNotNull(plan.Refusal);
            StringAssert.StartsWith("the shot that arrived is not the one the website sent", plan.Refusal!);
            Assert.IsNull(plan.Shot, "a shot whose sha does not match was loaded");
            CollectionAssert.IsEmpty(plan.LeversNeeded);
            Assert.IsFalse(plan.MayRun);
        }

        [Test]
        public void Prepare_AnAdapterShaMismatchIsRefusedBeforeAnyUse()
        {
            Tick("SelectHero Knight", "set Coins 5");
            var plan = ProbePlan.Prepare(_root, Sent(CheatShot, PlainAdapter, adapterSha: new string('0', 64)));
            Assert.IsNotNull(plan.Refusal);
            StringAssert.StartsWith("the adapter.json that arrived is not the one the website sent", plan.Refusal!);
            Assert.IsNull(plan.Shot);
            Assert.IsFalse(plan.MayRun);
        }

        /// <summary>
        /// Audit S3(b) — THE ADAPTER THAT RUNS IS THE ONE ON THIS EDITOR'S DISK, so a try is run
        /// only when that file is byte-for-byte the one the site sent with it. One byte different,
        /// or no file at all (a project nobody pressed "Send to my editor" for): the whole job is
        /// refused by name — before Play Mode (the agent's early branch asks exactly this plan),
        /// no director, no facts — and never as "wrong game".
        /// </summary>
        [Test]
        public void Prepare_TheAdapterOnThisEditorsDiskMustBeTheOneTheSiteSent()
        {
            Tick("SelectHero Knight", "set Coins 5");
            foreach (var disk in new[] { PlainAdapter + " ", null })
            {
                if (disk == null) File.Delete(SyncNova.AdapterFile(_root));
                else OnDisk(_root, disk);

                var plan = ProbePlan.Prepare(_root, Sent(CheatShot, PlainAdapter));

                Assert.AreEqual(ProbePlan.DiskAdapterMismatch, plan.Refusal, disk == null ? "absent" : "one byte more");
                StringAssert.Contains("press Send to my editor, then try again", plan.Refusal!);
                StringAssert.DoesNotContain("wrong game", plan.Refusal!);
                Assert.IsNull(plan.Shot, "a try whose adapter is not the sent one was loaded");
                Assert.IsFalse(plan.MayRun);
                var (d, cheats, _) = RunDirector(plan);
                Assert.IsNull(d, "a director was built for a try the plan refused");
                CollectionAssert.IsEmpty(cheats.Run_, "a command reached the game");

                // …and it is reported the way the agent reports it: a whole-job refusal, no facts
                var progress = CaptureProgress.Start(
                    new CaptureJob("r", "w", Array.Empty<CaptureJobItem>(), 60, "probe", "g"),
                    Sent(CheatShot, PlainAdapter));
                progress.RecordJobFailure(plan.Refusal!);
                Assert.IsTrue(progress.Done, "a refused try still owed a result");
                Assert.IsNull(progress.ProbeFactsJson);
                StringAssert.Contains(ProbePlan.DiskAdapterMismatch, progress.DoneReportJson(null));
            }
            // POSITIVE CONTROL: the very bytes that were sent → the try runs
            OnDisk(_root, PlainAdapter);
            Assert.IsNull(ProbePlan.Prepare(_root, Sent(CheatShot, PlainAdapter)).Refusal);
        }

        [Test]
        public void Prepare_HashesTheUtf8BytesOfWhatArrived()
        {
            // A name outside ASCII: its UTF-8 bytes and its UTF-16 / Latin-1 bytes all differ, so a
            // check on the wrong bytes either refuses the website's own sha or accepts a wrong one.
            var shot = Shot("héros-✓", @"{ ""kind"": ""click"", ""name"": ""Go"" }");
            string Hex(byte[] b) => string.Concat(SHA256.Create().ComputeHash(b).Select(x => x.ToString("x2")));
            var utf8 = Hex(new UTF8Encoding(false).GetBytes(shot));
            var utf16 = Hex(Encoding.Unicode.GetBytes(shot));

            var ok = ProbePlan.Prepare(_root, Sent(shot, PlainAdapter, shotSha: utf8.ToUpperInvariant()));
            Assert.IsNull(ok.Refusal, ok.Refusal);
            Assert.AreEqual("héros-✓", ok.Shot!.Name);
            Assert.AreEqual(utf8, ok.ShotSha256, "the facts echo what was MEASURED, lowercase");

            var wrong = ProbePlan.Prepare(_root, Sent(shot, PlainAdapter, shotSha: utf16));
            Assert.IsNotNull(wrong.Refusal);
        }

        // ---- one shot, the real loader ------------------------------------------------------------

        [Test]
        public void Prepare_LoadsOneShotThroughTheRealLoader_AndLeavesTheStudiosLoadErrorsAlone()
        {
            // What the relay's ping quotes as "your shots.json's errors" before the probe…
            JsonShotLoader.LoadFrom(@"{ ""$schemaVersion"": 1, ""shots"": [ { ""name"": ""x"" } ] }");
            var before = JsonShotLoader.LastErrors.ToArray();
            Assert.AreEqual(1, before.Length);

            var plan = ProbePlan.Prepare(_root, Sent(Shot("bad", @"{ ""kind"": ""clcik"" }"), PlainAdapter));
            Assert.IsNotNull(plan.Refusal);
            CollectionAssert.AreEqual(before, JsonShotLoader.LastErrors,
                "a probe's refused shot was left behind as the studio's own shots.json errors");

            var good = Loaded(_root, Shot("fine", @"{ ""kind"": ""wait"", ""seconds"": 0.25 }, { ""kind"": ""click"", ""name"": ""Go"" }"));
            Assert.AreEqual("fine", good.Shot!.Name);
            Assert.AreEqual(2, good.Shot.Steps.Count);
            Assert.AreEqual(AdStepKind.Click, good.Shot.Steps[1].Kind);
        }

        [Test]
        public void Prepare_TheTextMustBeExactlyOneShotObject()
        {
            foreach (var text in new[]
                     {
                         Shot("a", @"{ ""kind"": ""click"", ""name"": ""Go"" }") + Shot("b", @"{ ""kind"": ""click"", ""name"": ""Go"" }"),
                         "[" + Shot("a", @"{ ""kind"": ""click"", ""name"": ""Go"" }") + "]",
                         Shot("a", @"{ ""kind"": ""click"", ""name"": ""Go"" }") + " trailing",
                         "not json",
                     })
            {
                var plan = ProbePlan.Prepare(_root, Sent(text, PlainAdapter));
                Assert.IsNotNull(plan.Refusal, text);
                StringAssert.StartsWith("the shot that arrived is not one JSON object", plan.Refusal!, text);
                Assert.IsNull(plan.Shot, "a second shot rode in on the same string: " + text);
            }
        }

        [Test]
        public void Prepare_AShotTheSharedGrammarRefusesIsRefusedWithTheLoadersOwnSentence()
        {
            var fixture = SharedFixture();
            foreach (var caseName in new[] { "settle missing", "a bad step drops the WHOLE shot, by step index" })
            {
                var c = fixture.First(x => x["name"]!.Value<string>() == caseName);
                var shotText = c["doc"]!["shots"]![0]!.ToString(Newtonsoft.Json.Formatting.None);
                var expected = c["kitErrors"]![0]!.Value<string>()!;
                StringAssert.StartsWith("shots[0]", expected, "the fixture case's first error is about its first shot");

                var plan = ProbePlan.Prepare(_root, Sent(shotText, PlainAdapter));
                Assert.AreEqual("this shot does not load: " + expected, plan.Refusal, caseName);
                Assert.IsNull(plan.Shot);
            }
        }

        private static List<JObject> SharedFixture()
        {
            const string rel = "Editor/Tests/Fixtures/shots-grammar.cases.json";
            string? path = null;
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(JsonShotLoader).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath)
                && File.Exists(Path.Combine(info.resolvedPath, rel)))
                path = Path.Combine(info.resolvedPath, rel);
            Assert.IsNotNull(path, "the shared grammar fixture was not found");
            return ((JArray)NovaJson.ParseObject(File.ReadAllText(path!))["cases"]!).Cast<JObject>().ToList();
        }

        // ---- the levers ---------------------------------------------------------------------------

        [Test]
        public void Prepare_LeversNeededIsTheShotsCommandsAndTheAdaptersLevers_Sorted()
        {
            var shot = Shot("levers",
                @"{ ""kind"": ""cheat"", ""command"": ""call Wave.Next"" },
                  { ""kind"": ""cheatUntil"", ""command"": ""call Shop.Open"", ""until"": { ""kind"": ""present"", ""name"": ""Board"" } },
                  { ""kind"": ""timeScale"", ""factor"": 0.5 },
                  { ""kind"": ""click"", ""name"": ""Go"" }",
                extra: @", ""setup"": [""set Coins 5"", ""ui-dump""]");
            const string adapter = @"{ ""gameId"": ""g"",
              ""ready"": { ""mute"": ""set Music.mute true"", ""muteGet"": ""Music.mute"" },
              ""overlayTypeNames"": [""Fps""] }";
            var plan = Loaded(_root, shot, adapter);
            CollectionAssert.AreEqual(new[]
            {
                // `get Music.mute` is the ready block's muteGet as the ready gate sends it (`Levers.MuteGetLever`):
                // a lever since A2′'s fourteenth audit (a delivered `get` runs property getters in the game).
                "call Shop.Open", "call Wave.Next", "get Music.mute", "hide-overlay Fps", "set Coins 5",
                "set Music.mute true", "timeScale", "ui-dump",
            }, plan.LeversNeeded);
            // …which is, by construction, the shared definition over the two texts.
            CollectionAssert.AreEqual(Levers.NeededFrom(plan.ShotsDocument, adapter), plan.LeversNeeded);
        }

        [Test]
        public void Prepare_RefusesTheFirstUntickedLever_AndReportsBothListsWhole()
        {
            Tick("set Coins 5");
            var plan = ProbePlan.Prepare(_root, Sent(CheatShot, PlainAdapter));
            Assert.IsNull(plan.Refusal);
            Assert.AreEqual("SelectHero Knight", plan.LeverRefused);
            CollectionAssert.AreEqual(new[] { "SelectHero Knight", "set Coins 5" }, plan.LeversNeeded);
            CollectionAssert.AreEqual(new[] { "set Coins 5" }, plan.LeversApproved);
            Assert.IsFalse(plan.MayRun);
        }

        [Test]
        public void Prepare_ATemplateTickedVerbatimCoversItself()
        {
            // The window ticks `SelectHero {hero}` as that exact text — the only way it can. The
            // runtime gate then allows `SelectHero Knight`. The pre-check must not refuse it.
            var shot = Shot("tpl", @"{ ""kind"": ""cheat"", ""command"": ""SelectHero {hero}"" }",
                extra: @", ""parameters"": [""hero""]");
            Tick("SelectHero {hero}");
            var plan = Loaded(_root, shot);
            CollectionAssert.AreEqual(new[] { "SelectHero {hero}" }, plan.LeversNeeded);
            Assert.IsNull(plan.LeverRefused, "a ticked template was refused for itself");
            Assert.IsTrue(plan.MayRun);
        }

        [Test]
        public void Prepare_ALiteralLeverIsCoveredByATickedTemplate()
        {
            Tick("SelectHero {hero}", "set Coins 5");
            var plan = Loaded(_root, CheatShot);
            Assert.IsNull(plan.LeverRefused, "`SelectHero Knight` is exactly what the runtime gate lets the template through as");
        }

        [Test]
        public void Prepare_TheKitsFixedVerbsNeedNoTick_AGetDoes_AllAreListed()
        {
            // A2′'s fourteenth audit: `Levers.IsReadOnly` is exactly the kit's `ui-dump`, `show-ui`, `hide-ui` — a
            // delivered `get` runs property getters in the game, so it is a lever. The pre-check covers as the runtime
            // gate covers (invariant 99): the fixed verbs pass unticked, the `get` is refused until it is ticked.
            var shot = Shot("look", @"{ ""kind"": ""click"", ""name"": ""Go"" }",
                extra: @", ""setup"": [""ui-dump"", ""get Player.Coins"", ""show-ui""]");
            var plan = Loaded(_root, shot);
            CollectionAssert.AreEqual(new[] { "get Player.Coins", "show-ui", "ui-dump" }, plan.LeversNeeded);
            Assert.AreEqual("get Player.Coins", plan.LeverRefused, "an un-ticked delivered get is refused, as the runtime gate refuses it");
            Tick("get Player.Coins");
            Assert.IsNull(Loaded(_root, shot).LeverRefused, "ticked, the get runs; the kit's fixed verbs never needed a tick");

            var fixedOnly = Shot("look2", @"{ ""kind"": ""click"", ""name"": ""Go"" }",
                extra: @", ""setup"": [""ui-dump"", ""show-ui"", ""hide-ui""]");
            Assert.IsNull(Loaded(_root, fixedOnly).LeverRefused, "the kit's three fixed verbs pass the runtime gate unticked");
        }

        [Test]
        public void Prepare_APlaceholderFirstLeverIsNeverCovered_EvenIfTheFileSaysSo()
        {
            var shot = Shot("wild", @"{ ""kind"": ""cheat"", ""command"": ""{verb} Knight"" }",
                extra: @", ""parameters"": [""verb""]");
            // Hand-written into levers.json: the window refuses to tick it, the gate ignores it.
            File.WriteAllText(Levers.FilePath(_root), @"{ ""$schemaVersion"": 1, ""approved"": [""{verb} Knight""] }");
            var plan = Loaded(_root, shot);
            Assert.AreEqual("{verb} Knight", plan.LeverRefused);
        }

        [Test]
        public void Prepare_AnUnreadableLeversFileApprovesNothing()
        {
            File.WriteAllText(Levers.FilePath(_root), "{ oops");
            var plan = Loaded(_root, CheatShot);
            Assert.AreEqual("SelectHero Knight", plan.LeverRefused);
            CollectionAssert.IsEmpty(plan.LeversApproved);
        }

        // ---- THE MANDATORY GATE TESTS: the studio's own files on disk --------------------------------

        [Test]
        public void Mandatory_OwnFilesOnDisk_AnUntickedCheatIsNeverRun_AndTheFactsSayLeverRefused()
        {
            WriteOwnFiles();
            Assert.IsFalse(Levers.GateActive(_root),
                "precondition: the studio's own files, no synced.json — the ordinary gate is OFF");

            var plan = ProbePlan.Prepare(_root, Sent(CheatShot, PlainAdapter));
            Assert.AreEqual("SelectHero Knight", plan.LeverRefused);

            var (d, cheats, _) = RunDirector(plan);
            Assert.IsNull(d, "a director was built for a probe with an un-ticked lever");
            CollectionAssert.IsEmpty(cheats.Run_, "a command reached the game: " + string.Join(", ", cheats.Run_));

            var facts = ProbeFacts.Build(plan.Shot!.Name, false, null, null,
                Levers.NotApprovedLog(plan.LeverRefused!), null, null, 0, 0, plan.ShotSha256,
                plan.AdapterSha256, plan.LeversNeeded, plan.LeversApproved, plan.LeverRefused, 1f, false,
                "not run", "0.0.0");
            Assert.AreEqual("SelectHero Knight", facts["leverRefused"]!.Value<string>());
            Assert.IsFalse(facts["reached"]!.Value<bool>());
        }

        [Test]
        public void Mandatory_PastThePreCheck_TheDirectorStillGatesCloudContent()
        {
            // The second layer, on its own: even if a later edit ran the director without the
            // pre-check, a probe's director gates every un-ticked write although the ordinary gate
            // (the files on disk) is off.
            WriteOwnFiles();
            Assert.IsFalse(Levers.GateActive(_root));
            var plan = ProbePlan.Prepare(_root, Sent(CheatShot, PlainAdapter));

            var (d, cheats, _) = RunDirector(plan, bypassPlan: true);
            CollectionAssert.IsEmpty(Writes(cheats), "cloud content wrote into the game with nothing ticked");
            CollectionAssert.Contains(cheats.Run_, "show-ui", "the director's own read-only restore still reaches the game");
            StringAssert.Contains("lever not approved on this machine: 'set Coins 5'", d!.Summary);
            StringAssert.Contains("lever not approved on this machine: 'SelectHero Knight'", d.Summary);
            Assert.AreEqual(0, d.FailedStepIndex);
            Assert.AreEqual("cheat", d.FailedStepKindName);
            Assert.IsFalse(d.AllCaptured);

            // THE CONTROL: the same project, the same shot, the flag off — the writes go through.
            // So it is the flag that gates this run, not something else about the fixture.
            AdDirector.Active?.Finish("next run");
            var (_, open, _) = RunDirector(plan, bypassPlan: true, tweak: o => o.CloudContent = false);
            CollectionAssert.AreEqual(new[] { "set Coins 5", "SelectHero Knight" }, Writes(open));
        }

        [Test]
        public void Mandatory_OnceTickedTheLeversRun()
        {
            WriteOwnFiles();
            Tick("SelectHero Knight", "set Coins 5");
            var plan = ProbePlan.Prepare(_root, Sent(CheatShot, PlainAdapter));
            Assert.IsTrue(plan.MayRun, plan.LeverRefused);

            var (d, cheats, _) = RunDirector(plan);
            Assert.IsNotNull(d);
            CollectionAssert.AreEqual(new[] { "set Coins 5", "SelectHero Knight" }, Writes(cheats));
            Assert.IsTrue(d!.AllCaptured, d.Summary);
            Assert.IsTrue(ProbeFacts.Reached(d.AllCaptured, d.FailedStepIndex, plan.LeverRefused));
        }

        [Test]
        public void Mandatory_ATimeScaleStepIsGatedForCloudContentToo()
        {
            WriteOwnFiles();
            var plan = ProbePlan.Prepare(_root, Sent(Shot("slow", @"{ ""kind"": ""timeScale"", ""factor"": 0.5 }"), PlainAdapter));
            Assert.AreEqual(Levers.TimeScaleLever, plan.LeverRefused);
            var (d, _, _) = RunDirector(plan, bypassPlan: true);
            StringAssert.Contains(Levers.NotApprovedLog(Levers.TimeScaleLever), d!.Summary);
            Assert.AreEqual("timeScale", d.FailedStepKindName);
        }

        [Test]
        public void DirectorOptions_PinTheGateOnOneAttemptAndNoRecorder()
        {
            var options = ProbeRun.DirectorOptions(_root, null);
            Assert.IsTrue(options.CloudContent, "a probe's director must gate as cloud content, unconditionally");
            Assert.AreEqual(1, options.MaxAttempts, "a retry would run the shot's writes twice");
            Assert.IsInstanceOf<NoRecordingDriver>(options.Recorder, "a probe records nothing");
            Assert.AreEqual(_root, options.ProjectRoot, "the gate must read THIS project's levers.json");

            // Second audit M1: the lever gate is wrapped in the camera-pose Type guard, asked with
            // THIS run's cloud flag — on a project whose own files say "ungated"
            WriteOwnFiles();
            OnDisk(_root, BoardRigAdapter);
            Assert.IsFalse(Levers.GateActive(_root), "precondition: the disk says ungated");
            var inner = new RecordingBridge();
            var log = new List<string>();
            var wrapped = options.WrapCheats!(inner, log.Add);
            Assert.IsInstanceOf<ProbeCameraTypeGuard>(wrapped);
            Assert.AreSame(inner, ((ProbeCameraTypeGuard)wrapped).Inner, "it wraps the gate it is given");
            Assert.IsFalse(wrapped.Run(PoseOtherType), "a try's guard answered with the disk's 'ungated'");
            CollectionAssert.AreEqual(new[] { OtherTypeRefused }, log);
        }

        // ---- a probe writes no file -----------------------------------------------------------------

        [Test]
        public void AProbeLeavesLibraryNovaExactlyAsItWas()
        {
            WriteOwnFiles();
            Tick("SelectHero Knight", "set Coins 5");
            var before = Snapshot(RelayPaths.NovaDir(_root));

            var plan = ProbePlan.Prepare(_root, Sent(CheatShot, PlainAdapter));
            var (d, _, _) = RunDirector(plan);
            Assert.IsTrue(d!.AllCaptured, d.Summary);

            var after = Snapshot(RelayPaths.NovaDir(_root));
            CollectionAssert.AreEquivalent(before.Keys, after.Keys, "a file appeared or went under Library/Nova");
            foreach (var kv in before)
                CollectionAssert.AreEqual(kv.Value, after[kv.Key], kv.Key + " changed");
        }

        [Test]
        public void AProbeOnAProjectWithNoShotsFileCreatesNone()
        {
            // The project holds only the adapter.json a send wrote (a try needs it — audit S3).
            var plan = Loaded(_root, Shot("fresh", @"{ ""kind"": ""click"", ""name"": ""Go"" }"));
            var adapterBefore = File.ReadAllBytes(SyncNova.AdapterFile(_root));
            var (d, _, _) = RunDirector(plan);
            Assert.IsTrue(d!.AllCaptured, d.Summary);
            Assert.IsFalse(File.Exists(RelayPaths.NovaShotsFile(_root)), "the probe wrote a shots.json");
            CollectionAssert.AreEqual(adapterBefore, File.ReadAllBytes(SyncNova.AdapterFile(_root)),
                "the probe changed adapter.json");
            Assert.IsFalse(File.Exists(SyncNova.SyncedFile(_root)), "the probe wrote a synced.json");
        }

        private static Dictionary<string, byte[]> Snapshot(string dir) =>
            Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(dir, f), File.ReadAllBytes);

        // ---- recording OFF --------------------------------------------------------------------------

        [Test]
        public void TheProbesRecorderRecordsNothing_AndCreatesNoFolder()
        {
            foreach (var settle in new[] { @"{ ""kind"": ""present"", ""name"": ""Board"" }", @"{ ""kind"": ""present"", ""name"": ""Never"" }" })
            {
                AdDirector.Active?.Finish("next run");
                var plan = Loaded(_root, Shot("rec", @"{ ""kind"": ""click"", ""name"": ""Go"" }", settle: settle)
                    .Replace(@"""settle"":", @"""settleTimeoutSec"": 1, ""settle"":"));
                NoRecordingDriver? driver = null;
                var wasRecording = false;
                var (d, _, _) = RunDirector(plan, tweak: o =>
                {
                    driver = (NoRecordingDriver)o.Recorder!;
                    o.OnStopped = () => { wasRecording |= driver!.IsRecording; return Array.Empty<object>(); };
                });
                Assert.IsNotNull(d);
                Assert.AreEqual(1, driver!.StartsAsked, "the director asks once; nothing answers");
                Assert.IsFalse(wasRecording || driver.IsRecording, "the probe's recorder reported recording");
                Assert.IsFalse(Directory.Exists(driver.OutputDir),
                    "the probe's recorder folder exists — a failed attempt's quarantine could rename a real take there");
            }
            Assert.IsEmpty(Directory.GetFiles(_root, "*.mp4", SearchOption.AllDirectories), "a take was written");
        }

        // ---- what stopped the run ---------------------------------------------------------------------

        [Test]
        public void Director_AStepThatDoesNotResolveIsNamedByIndexAndGrammarKind()
        {
            var plan = Loaded(_root, Shot("stuck",
                @"{ ""kind"": ""wait"", ""seconds"": 0.2 },
                  { ""kind"": ""waitFor"", ""condition"": { ""kind"": ""present"", ""name"": ""Never"" }, ""timeout"": 1 },
                  { ""kind"": ""click"", ""name"": ""Go"" }"));
            var (d, _, _) = RunDirector(plan);
            Assert.AreEqual(1, d!.FailedStepIndex, d.Summary);
            Assert.AreEqual("waitFor", d.FailedStepKindName);
            Assert.AreEqual("stuck attempt 1: WaitFor Present 'Never' did not resolve", d.FailedReason);
            CollectionAssert.Contains(d.LogLines, "  " + d.FailedReason, "the reason is the director's own log line");
            Assert.IsFalse(d.AllCaptured);
        }

        [Test]
        public void Director_ASettleThatNeverHoldsIsSettle()
        {
            var plan = Loaded(_root, Shot("unsettled", @"{ ""kind"": ""click"", ""name"": ""Go"" }",
                settle: @"{ ""kind"": ""present"", ""name"": ""Never"" }, ""settleTimeoutSec"": 1"));
            var (d, _, _) = RunDirector(plan);
            Assert.IsNull(d!.FailedStepIndex, d.Summary);
            Assert.AreEqual(AdDirector.FailedKindSettle, d.FailedStepKindName);
            StringAssert.Contains("every step ran, but settle Present 'Never' did not hold within 1s", d.FailedReason);
            Assert.IsFalse(d.AllCaptured);
        }

        [Test]
        public void Director_AReadyGateThatNeverOpensIsReady()
        {
            var plan = Loaded(_root, Shot("gated", @"{ ""kind"": ""click"", ""name"": ""Go"" }"));
            var (d, _, _) = RunDirector(plan, gate: new NeverReadyGate());
            Assert.IsNull(d!.FailedStepIndex);
            Assert.AreEqual(AdDirector.FailedKindReady, d.FailedStepKindName);
            Assert.AreEqual("ABORT: ready gate never satisfied, even after recovery", d.FailedReason);
        }

        [Test]
        public void Director_APlaceholderThatCannotBeBoundIsBinding()
        {
            var plan = Loaded(_root, Shot("bind", @"{ ""kind"": ""cheat"", ""command"": ""SelectHero {hero}"" }",
                extra: @", ""parameters"": [""hero""]"));
            var (d, _, _) = RunDirector(plan, bypassPlan: true);
            Assert.IsNull(d!.FailedStepIndex);
            Assert.AreEqual(AdDirector.FailedKindBinding, d.FailedStepKindName);
            StringAssert.StartsWith("ABORT bind:", d.FailedReason);
        }

        private sealed class RecoveryThatRevealsLate : IRecoveryPolicy
        {
            public FakeUi? Ui;
            public int Calls;
            public IEnumerable Recover(DirectorContext ctx)
            {
                if (++Calls == 2) Ui!.Names.Add("Late");
                ctx.LastOpSucceeded = true;
                yield break;
            }
        }

        [Test]
        public void Director_ASuccessOnALaterAttemptClearsTheEarlierAttemptsFailure()
        {
            // Not a probe's case (one attempt) but the same properties on every director: a run
            // that ENDS captured has nothing to explain, whatever an earlier attempt tripped on.
            var plan = Loaded(_root, Shot("late",
                @"{ ""kind"": ""waitFor"", ""condition"": { ""kind"": ""present"", ""name"": ""Late"" }, ""timeout"": 1 }"));
            var recovery = new RecoveryThatRevealsLate();
            var (d, _, _) = RunDirector(plan, recovery: recovery, tweak: o => o.MaxAttempts = 2);
            Assert.AreEqual(2, recovery.Calls, d!.Summary);
            Assert.IsTrue(d.AllCaptured, d.Summary);
            Assert.IsNull(d.FailedStepIndex, "attempt 1's failure outlived the capture on attempt 2");
            Assert.IsNull(d.FailedStepKindName);
            Assert.IsNull(d.FailedReason);
        }

        private sealed class ThrowingGate : IReadyGate
        {
            public IEnumerable WaitUntilReady(DirectorContext ctx) => throw new InvalidOperationException("kaboom");
        }

        [Test]
        public void Director_ARunThatThrowsSaysSo_InItsOwnLogAndAsItsFailure()
        {
            // The director logs the exception to the console as an error; that line is expected.
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("director aborted"));
            var plan = Loaded(_root, Shot("boom", @"{ ""kind"": ""click"", ""name"": ""Go"" }"));
            var (d, _, _) = RunDirector(plan, gate: new ThrowingGate());
            Assert.IsFalse(d!.AllCaptured);
            Assert.AreEqual(AdDirector.FailedKindAborted, d.FailedStepKindName);
            Assert.AreEqual("ABORT: the director threw InvalidOperationException: kaboom", d.FailedReason);
            CollectionAssert.Contains(d.LogLines, d.FailedReason, "not reached, no reason — the one answer a run must never give");
        }

        [Test]
        public void Director_AReachedShotLeavesNothingToExplain()
        {
            var plan = Loaded(_root, Shot("ok", @"{ ""kind"": ""click"", ""name"": ""Go"" }"));
            var (d, _, _) = RunDirector(plan);
            Assert.IsTrue(d!.AllCaptured, d.Summary);
            Assert.IsNull(d.FailedStepIndex);
            Assert.IsNull(d.FailedStepKindName);
            Assert.IsNull(d.FailedReason);
        }

        // ---- the stop hook ----------------------------------------------------------------------------

        private static IEnumerable Steps(int count, Action onFirst, Action? onLast = null)
        {
            onFirst();
            for (var i = 0; i < count; i++) yield return null;
            onLast?.Invoke();
        }

        [Test]
        public void StopHook_RunsOnceAndFinishesBeforeTheRecorderStops()
        {
            foreach (var settle in new[] { "Board", "Never" })
            {
                AdDirector.Active?.Finish("next run");
                var plan = Loaded(_root, Shot("hooked", @"{ ""kind"": ""click"", ""name"": ""Go"" }",
                    settle: @"{ ""kind"": ""present"", ""name"": """ + settle + @""" }, ""settleTimeoutSec"": 1"));
                var recorder = new WatchedRecorder(Path.Combine(_root, "never"));
                var calls = 0;
                var hookDone = false;
                var recordingDuringHook = false;
                var stoppedBeforeHookDone = false;
                recorder.OnStop = () => stoppedBeforeHookDone |= !hookDone;
                var (d, _, _) = RunDirector(plan, tweak: o =>
                {
                    o.Recorder = recorder;
                    o.OnStopped = () => Steps(5, () => { calls++; recordingDuringHook = recorder.IsRecording; },
                        () => hookDone = true);
                });
                Assert.AreEqual(1, calls, "settle " + settle + ": " + d!.Summary);
                Assert.IsTrue(recordingDuringHook, "the hook ran after the recorder stopped");
                Assert.IsTrue(hookDone, "the director did not pump the hook to its end");
                Assert.IsFalse(stoppedBeforeHookDone, "the recorder stopped while the hook was still reading the screen");
            }
        }

        [Test]
        public void StopHook_RunsOnTheReadyAndBindingPathsToo()
        {
            // PUMPED TO ITS END at those stop points — not merely started by Finish's fallback,
            // which runs only the first step (and would satisfy a bare call count on its own).
            var calls = 0;
            var completed = 0;
            var plan = Loaded(_root, Shot("r", @"{ ""kind"": ""click"", ""name"": ""Go"" }"));
            RunDirector(plan, gate: new NeverReadyGate(),
                tweak: o => o.OnStopped = () => Steps(2, () => calls++, () => completed++));
            Assert.AreEqual(1, calls, "ready-gate abort");
            Assert.AreEqual(1, completed, "ready-gate abort: the hook was not pumped at the stop");

            AdDirector.Active?.Finish("next run");
            calls = 0;
            completed = 0;
            var bind = Loaded(_root, Shot("b", @"{ ""kind"": ""cheat"", ""command"": ""SelectHero {hero}"" }",
                extra: @", ""parameters"": [""hero""]"));
            RunDirector(bind, bypassPlan: true,
                tweak: o => o.OnStopped = () => Steps(2, () => calls++, () => completed++));
            Assert.AreEqual(1, calls, "binding failure");
            Assert.AreEqual(1, completed, "binding failure: the hook was not pumped at the stop");
        }

        [Test]
        public void StopHook_AThrowingHookDoesNotAbortTheRun()
        {
            var plan = Loaded(_root, Shot("t", @"{ ""kind"": ""click"", ""name"": ""Go"" }"));
            var (d, _, _) = RunDirector(plan,
                tweak: o => o.OnStopped = () => Steps(1, () => throw new InvalidOperationException("boom")));
            Assert.IsTrue(d!.AllCaptured, "evidence gathering aborted the run it was evidence about: " + d.Summary);
            StringAssert.Contains("WARN the stop hook threw: boom", d.Summary);
        }

        [Test]
        public void StopHook_ARunStoppedFromOutsideStillGetsItsFirstStep()
        {
            var plan = Loaded(_root, Shot("long", @"{ ""kind"": ""wait"", ""seconds"": 30 }"));
            var calls = 0;
            var (d, _, _) = RunDirector(plan,
                tweak: o => o.OnStopped = () => Steps(3, () => calls++),
                midRun: dd => dd.Finish("stopped from outside"));
            Assert.AreEqual(1, calls, "Finish's fallback did not read the screen: " + d!.Summary);
        }

        // ---- the facts ----------------------------------------------------------------------------------

        private static JObject Facts(IEnumerable<string>? ui = null, IEnumerable<string>? log = null,
            bool allCaptured = true, int? failedStep = null, string? leverRefused = null) =>
            ProbeFacts.Build("s", allCaptured, failedStep, failedStep == null ? null : "click", null, log, ui,
                1280, 720, "aa", "bb", new[] { "x" }, new[] { "x" }, leverRefused, 0.5f, true, null, "9.9.9");

        [Test]
        public void Facts_TheNamesOnScreenAreCappedAt200NamesOf120Characters()
        {
            var names = Enumerable.Range(0, 201).Select(i => "n" + i.ToString("000")).ToList();
            names[0] = new string('x', 500);
            var ui = (JArray)Facts(ui: names)["uiDump"]!;
            Assert.AreEqual(200, ui.Count);
            Assert.AreEqual(new string('x', 120), ui[0]!.Value<string>());
            Assert.AreEqual("n199", ui[199]!.Value<string>(), "the FIRST 200 names are kept");
        }

        [Test]
        public void Facts_TheLogIsCappedAt200LinesOf300Characters_KeepingTheLast()
        {
            var lines = Enumerable.Range(0, 201).Select(i => "line " + i).ToList();
            lines[200] = new string('y', 500);
            var log = (JArray)Facts(log: lines)["log"]!;
            Assert.AreEqual(200, log.Count);
            Assert.AreEqual("line 1", log[0]!.Value<string>(), "the LAST 200 lines are kept — the reason is at the end");
            Assert.AreEqual(new string('y', 300), log[199]!.Value<string>());
        }

        [Test]
        public void Facts_ReachedOnlyWhenEveryStepRanAndTheSettleHeld()
        {
            Assert.IsTrue(ProbeFacts.Reached(true, null, null));
            Assert.IsFalse(ProbeFacts.Reached(false, null, null), "the settle did not hold");
            Assert.IsFalse(ProbeFacts.Reached(true, 2, null), "a step failed");
            Assert.IsFalse(ProbeFacts.Reached(true, null, "set Coins 5"), "the shot was never run");
            Assert.IsFalse(ProbeFacts.Reached(false, 0, "x"));
            Assert.IsTrue(Facts()["reached"]!.Value<bool>());
            Assert.IsFalse(Facts(leverRefused: "x")["reached"]!.Value<bool>());
        }

        [Test]
        public void Facts_CarryEveryFieldTheWireNames()
        {
            // The hosted half fails the run BY NAME on a missing field, so every one is always
            // present — null, never absent.
            var f = Facts(failedStep: null);
            foreach (var key in new[]
                     {
                         "shot", "reached", "failedStep", "failedStepKind", "reason", "log", "uiDump", "screen",
                         "shotSha256", "adapterSha256", "leversNeeded", "leversApproved", "leverRefused", "timeScale",
                     })
                Assert.IsTrue(f.ContainsKey(key), "missing wire field: " + key);
            Assert.AreEqual(JTokenType.Null, f["failedStep"]!.Type);
            Assert.AreEqual(JTokenType.Null, f["leverRefused"]!.Type);
            Assert.AreEqual(1280, f["screen"]!["width"]!.Value<int>());
            Assert.AreEqual(720, f["screen"]!["height"]!.Value<int>());
            Assert.AreEqual(0.5, f["timeScale"]!.Value<double>(), 1e-9);
            Assert.AreEqual(3, Facts(failedStep: 3)["failedStep"]!.Value<int>());
        }

        [Test]
        public void Facts_ARetriedPostNeverClaimsAFrameItDoesNotCarry()
        {
            var json = ProbeFacts.ToJson(Facts());
            Assert.AreEqual(json, ProbeFacts.ReconcileFrame(json, frameOnDisk: true));
            var lost = NovaJson.ParseObject(ProbeFacts.ReconcileFrame(json, frameOnDisk: false));
            Assert.IsFalse(lost["frameCaptured"]!.Value<bool>());
            Assert.AreEqual(ProbeFacts.FrameLostNote, lost["frameNote"]!.Value<string>());
            Assert.AreEqual("s", lost["shot"]!.Value<string>(), "nothing else is touched");
        }

        // ---- the progress file ----------------------------------------------------------------------------

        [Test]
        public void Progress_RoundTripsTheScriptTheFactsAndTheStartCount()
        {
            // A date-shaped name: the progress reader must not turn it into a Date on the way back.
            var shot = Shot("2026-09-21", @"{ ""kind"": ""click"", ""name"": ""Go"" }");
            var job = CaptureJob.ParseClaim(
                @"{ ""job"": { ""runId"": ""run-p"", ""workspaceId"": ""ws"", ""kind"": ""probe"", ""gameId"": ""g"" }, ""probe"": "
                + new JObject
                {
                    ["shot"] = shot, ["adapter"] = PlainAdapter,
                    ["shotSha256"] = SyncNova.Sha256OfText(shot), ["adapterSha256"] = SyncNova.Sha256OfText(PlainAdapter),
                }.ToString() + " }",
                out var error, out _, out var probe);
            Assert.IsNull(error);
            var p = CaptureProgress.Start(job!, probe);
            p.ProbeRunStarts = 2;
            p.ProbeFactsJson = @"{""shot"":""2026-09-21""}";
            var file = Path.Combine(_root, "capture-status.json");
            p.Save(file);

            var back = CaptureProgress.Load(file)!;
            Assert.AreEqual("probe", back.Kind);
            Assert.IsNotNull(back.Probe, "the script did not survive the reload");
            Assert.AreEqual(shot, back.Probe!.Shot);
            Assert.AreEqual(PlainAdapter, back.Probe.Adapter);
            Assert.AreEqual(probe!.ShotSha256, back.Probe.ShotSha256);
            Assert.AreEqual(probe.AdapterSha256, back.Probe.AdapterSha256);
            Assert.AreEqual(2, back.ProbeRunStarts);
            Assert.AreEqual(@"{""shot"":""2026-09-21""}", back.ProbeFactsJson);
            // …and the reloaded script is still the one the website sent.
            Assert.AreEqual("2026-09-21", ProbePlan.Prepare(_root, back.Probe).Shot!.Name);
        }

        [Test]
        public void Progress_AProbeIsNotDoneUntilItsResultIsPosted()
        {
            var p = CaptureProgress.Start(new CaptureJob("r", "w", Array.Empty<CaptureJobItem>(), 60, "probe", "g"),
                Sent(CheatShot, PlainAdapter));
            Assert.IsFalse(p.Done, "a probe owes a result");
            p.ResultPosted = true;
            Assert.IsTrue(p.Done);
        }

        [Test]
        public void Progress_AHalfWrittenProbeBlockLoadsAsNoScript()
        {
            var file = Path.Combine(_root, "capture-status.json");
            File.WriteAllText(file, @"{ ""runId"": ""r"", ""kind"": ""probe"", ""items"": [],
                ""probe"": { ""shot"": ""S"", ""adapter"": ""A"", ""shotSha256"": ""aa"" } }");
            var back = CaptureProgress.Load(file)!;
            Assert.IsNull(back.Probe);
            Assert.AreEqual(ProbeRequest.MissingReason, ProbePlan.Prepare(_root, back.Probe).Refusal);
        }

        // ---- audit M3: the camera spec, the fourth point, gated during a try ------------------------------

        private const string CameraSpecAdapter =
            @"{ ""gameId"": ""g"", ""camera"": { ""viewType"": ""CameraView"", ""projection"": ""perspective"", ""setRotation"": ""DeleteSave"" } }";
        private const string CameraPoseCommand = "camera-pose 1 2 3 40.84 135 0 20";
        private const string CameraSpecLeverText = "camera-spec CameraView SetPosition DeleteSave SetFov";

        [Test]
        public void Gate_DuringATry_ACameraPoseNeedsTheCameraSpecLever_WhateverTheDiskSays()
        {
            // The studio's OWN files, no synced.json: the ordinary answer is "ungated".
            WriteOwnFiles();
            OnDisk(_root, CameraSpecAdapter);
            Assert.IsFalse(Levers.GateActive(_root), "control: this project's files are its own");
            Tick(CameraPoseCommand); // the framing is ticked; the METHODS it would call are not
            var log = new List<string>();
            var inner = new RecordingBridge();

            Assert.IsFalse(new LeverGateBridge(inner, _root, log.Add, cloudContent: true).Run(CameraPoseCommand),
                "a try posed the camera through methods adapter.json named, with the camera spec un-ticked");
            CollectionAssert.IsEmpty(inner.Run_, "the command reached the game");
            CollectionAssert.Contains(log, Levers.NotApprovedLog(CameraSpecLeverText));
            CollectionAssert.Contains(LeverGateBridge.RefusedThisSession.ToList(), CameraSpecLeverText,
                "the window must be able to list the spec to tick");

            // CONTROL: the same command on the same project, not cloud content — the studio's own run
            Assert.IsTrue(new LeverGateBridge(inner, _root, null).Run(CameraPoseCommand));
            // …and once the spec is ticked, the try poses exactly as it always did
            Tick(CameraSpecLeverText);
            Assert.IsTrue(new LeverGateBridge(inner, _root, null, cloudContent: true).Run(CameraPoseCommand));
            CollectionAssert.AreEqual(new[] { CameraPoseCommand, CameraPoseCommand }, inner.Run_);
        }

        // ---- the fresh-context audit's surviving kit mutants ---------------------------------------------

        /// <summary>Mutant AdDirector.cs:365 (<c>cloudContent: false</c> at overlay hiding): an overlay
        /// the adapter names is left visible during a try, on a project whose own files are ungated.</summary>
        [Test]
        public void Mandatory_DuringATry_AnOverlayNobodyTickedIsLeftVisible_OnAnUngatedProject()
        {
            WriteOwnFiles();
            Assert.IsFalse(Levers.GateActive(_root), "control");
            var plan = Loaded(_root, Shot("hud", @"{ ""kind"": ""click"", ""name"": ""Go"" }"));
            var (d, _, _) = RunDirector(plan, adapterTweak: a =>
            {
                a.IsDefaultAdapter = true; // every name came from a JSON file
                a.OverlayTypeNames = new[] { "FpsCounter" };
            });
            StringAssert.Contains(Levers.OverlayLeftVisibleLog("FpsCounter"), d!.Summary,
                "a try disabled a MonoBehaviour in the studio's game with nothing ticked");
        }

        /// <summary>Mutant Levers.cs:77 (<c>GateActive</c> for <c>GateActiveFor</c>): the overlay
        /// check itself answers "gated" for cloud content, whatever the disk says.</summary>
        [Test]
        public void OverlaysAllowed_CloudContentIsGatedOnAnUngatedProject()
        {
            WriteOwnFiles();
            var log = new List<string>();
            CollectionAssert.IsEmpty(Levers.OverlaysAllowed(_root, new[] { "FpsCounter" }, new[] { "FpsCounter" },
                log.Add, adapterIsJsonDriven: true, cloudContent: true));
            CollectionAssert.AreEqual(new[] { Levers.OverlayLeftVisibleLog("FpsCounter") }, log);
            // CONTROL: the studio's own run on the same project hides it
            CollectionAssert.AreEqual(new[] { "FpsCounter" }, Levers.OverlaysAllowed(_root, new[] { "FpsCounter" },
                new[] { "FpsCounter" }, null, adapterIsJsonDriven: true).ToArray());
        }

        /// <summary>Mutants ProbeFacts.cs (the two shas swapped; leversApproved sent as leversNeeded):
        /// every value lands in ITS field — the server compares each with a different thing.</summary>
        [Test]
        public void Facts_EachShaAndEachLeverListIsInItsOwnField()
        {
            var f = ProbeFacts.Build("s", true, null, null, null, null, null, 1, 1, "shot-sha", "adapter-sha",
                new[] { "needed" }, new[] { "approved" }, null, 1f, false, "no frame", "9.9.9",
                factsError: "could not list the names on screen: x");
            Assert.AreEqual("shot-sha", f["shotSha256"]!.Value<string>());
            Assert.AreEqual("adapter-sha", f["adapterSha256"]!.Value<string>());
            CollectionAssert.AreEqual(new[] { "needed" }, f["leversNeeded"]!.Values<string>().ToArray());
            CollectionAssert.AreEqual(new[] { "approved" }, f["leversApproved"]!.Values<string>().ToArray());
            Assert.AreEqual("no frame", f["frameNote"]!.Value<string>());
            Assert.AreEqual("could not list the names on screen: x", f["factsError"]!.Value<string>());
        }

        /// <summary>Mutant CaptureJob.cs (<c>Math.Max(0, …)</c> dropped on <c>probeRunStarts</c>): a
        /// hand-edited negative count must not buy the cloud's shot extra runs in the studio's game.</summary>
        [Test]
        public void Progress_ANegativeStartCountReadsAsZero()
        {
            var file = Path.Combine(_root, "capture-status.json");
            File.WriteAllText(file, @"{ ""runId"": ""r"", ""kind"": ""probe"", ""items"": [], ""probeRunStarts"": -5 }");
            Assert.AreEqual(0, CaptureProgress.Load(file)!.ProbeRunStarts);
        }

        // ---- audit M5: every wait of a try keeps its lease ---------------------------------------------

        [Test]
        public void WhileRefreshing_RefreshesTheLeaseOnEveryStepOfAWait()
        {
            var left = 4;
            var refreshed = 0;
            var lost = 0;
            var steps = 0;
            foreach (var _ in ProbeRun.WhileRefreshing(() => left-- > 0,
                         () => { refreshed++; return Array.Empty<object>(); }, () => false, () => lost++))
                steps++;
            Assert.AreEqual(4, refreshed, "a wait step went by without the lease being refreshed");
            Assert.AreEqual(4, steps);
            Assert.AreEqual(0, lost);
        }

        [Test]
        public void WhileRefreshing_StopsTheMomentTheRunIsNoLongerThisEditors()
        {
            var checks = 0;
            var lost = 0;
            var steps = 0;
            var bound = 0; // a wait that ignored the loss would run to this bound, not forever
            foreach (var _ in ProbeRun.WhileRefreshing(() => bound++ < 10, () => Array.Empty<object>(),
                         () => ++checks >= 3, () => lost++))
                steps++;
            Assert.AreEqual(1, lost, "the lost lease was not acted on exactly once");
            Assert.AreEqual(2, steps, "it kept waiting after the run stopped being this editor's");
        }

        // ---- the names on screen ---------------------------------------------------------------------

        [Test]
        public void OnScreenRows_AreExactlyTheRowsUiDumpPrints()
        {
            // "From the same source as the `ui-dump` command": the rows a probe reports are the rows a
            // person gets by typing ui-dump at the same moment — same walk, same format, same order.
            var a = new GameObject("ProbeTestBtnA");
            var b = new GameObject("ProbeTestBtnB");
            try
            {
                var rows = ReflectionCheatBridge.OnScreenRows();
                Assert.IsTrue(rows.Any(r => r.StartsWith("ProbeTestBtnA ", StringComparison.Ordinal)), string.Join("\n", rows));
                Assert.IsTrue(rows.Any(r => r.StartsWith("ProbeTestBtnB ", StringComparison.Ordinal)));
                CollectionAssert.DoesNotContain(rows, ReflectionCheatBridge.NothingOnScreen, "a sentence is not a name");

                Assert.IsTrue(new GenericCheatBridge(_root, new FakeUi()).Run("ui-dump"));
                var printed = File.ReadAllText(Path.Combine(RelayPaths.Root(_root), "probe", "ui-dump.txt")).Split('\n');
                CollectionAssert.AreEqual(printed, rows);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(a);
                UnityEngine.Object.DestroyImmediate(b);
            }
        }

        // ---- the second fresh-context audit of E.1 -------------------------------------------------------

        private const string BoardRigAdapter =
            @"{ ""gameId"": ""g"", ""camera"": { ""viewType"": ""BoardRig"", ""projection"": ""perspective"" } }";
        private const string PoseOtherType = "camera-pose SaveManager 1 2 3 4 5 6 60";
        private const string PoseSpecsType = "camera-pose BoardRig 1 2 3 4 5 6 60";
        private const string PoseNoType = "camera-pose 1 2 3 4 5 6 60";
        private const string BoardRigSpec = "camera-spec BoardRig SetPosition SetRotation SetFov";
        private static readonly string OtherTypeRefused = CameraPose.TypeIsNotTheSpecsViewType("SaveManager", "BoardRig");

        private static string PoseShot(string pose) =>
            Shot("cam", @"{ ""kind"": ""cheat"", ""command"": """ + pose + @""" }");

        /// <summary>synced.json for the two files on disk now: a project the cloud delivered.</summary>
        private void MarkDelivered() =>
            File.WriteAllText(SyncNova.SyncedFile(_root),
                "{\"$schemaVersion\":2,\"shotsSha256\":\"" + SyncNova.Sha256OfFile(RelayPaths.NovaShotsFile(_root)) +
                "\",\"adapterSha256\":\"" + SyncNova.Sha256OfFile(SyncNova.AdapterFile(_root)) + "\"}");

        /// <summary>Second audit M1: the rule itself — only a pose naming ANOTHER Type than a NAMED
        /// view type, split the way the bridge splits it.</summary>
        [Test]
        public void CameraTypeRefusal_OnlyAPoseNamingAnotherTypeThanTheNamedViewType()
        {
            var boardRig = AdapterJson.ParseText(BoardRigAdapter).Camera;
            Assert.IsNotNull(boardRig, "control: the camera block is read");
            Assert.AreEqual(OtherTypeRefused, ProbePlan.CameraTypeRefusal(PoseOtherType, boardRig));
            Assert.AreEqual(OtherTypeRefused, ProbePlan.CameraTypeRefusal("CAMERA-POSE SaveManager 1 2 3 4 5 6 60", boardRig),
                "the bridge matches its verbs case-insensitively");
            Assert.IsNull(ProbePlan.CameraTypeRefusal(PoseSpecsType, boardRig));
            Assert.IsNull(ProbePlan.CameraTypeRefusal(PoseNoType, boardRig));
            Assert.IsNull(ProbePlan.CameraTypeRefusal("camera-pose SaveManager 1 2", boardRig), "a pose that does not parse says why itself");
            Assert.IsNull(ProbePlan.CameraTypeRefusal("set Coins 5", boardRig));
            // a camera-spec that names no view type (`camera-spec * …`) leaves the command's Type standing
            var noViewType = AdapterJson.ParseText(
                @"{ ""gameId"": ""g"", ""camera"": { ""projection"": ""perspective"", ""setRotation"": ""DeleteSave"" } }").Camera;
            Assert.IsNotNull(noViewType, "control: a camera block with no view type");
            Assert.IsNull(ProbePlan.CameraTypeRefusal(PoseOtherType, noViewType));
            Assert.IsNull(ProbePlan.CameraTypeRefusal(PoseOtherType, null));
        }

        /// <summary>Second audit M1, the auditor's case (b): on a DELIVERED project the pre-check let
        /// a camera-pose of another Type through (MayRun) and the run then stopped at it. Refused at
        /// Prepare now, whole, by the pose's own sentence — before any lever is asked for.</summary>
        [Test]
        public void Prepare_ACameraPoseOfAnotherTypeThanTheSpecsIsRefusedUpFront_NotMidRun()
        {
            WriteOwnFiles();
            OnDisk(_root, BoardRigAdapter);
            MarkDelivered();
            Assert.IsTrue(Levers.GateActive(_root), "control: a delivered project");
            Tick(PoseOtherType, PoseSpecsType, PoseNoType, BoardRigSpec);

            var plan = ProbePlan.Prepare(_root, Sent(PoseShot(PoseOtherType), BoardRigAdapter));
            Assert.AreEqual(OtherTypeRefused, plan.Refusal);
            Assert.IsFalse(plan.MayRun);
            Assert.IsNull(plan.LeverRefused, "no lever is asked for: no tick could make this pose run");
            // it is refused before the levers: the same shot with nothing ticked says the same
            Levers.SetApproved(_root, PoseOtherType, false);
            Levers.SetApproved(_root, BoardRigSpec, false);
            Assert.AreEqual(OtherTypeRefused, ProbePlan.Prepare(_root, Sent(PoseShot(PoseOtherType), BoardRigAdapter)).Refusal);
            Tick(PoseOtherType, BoardRigSpec);

            // POSITIVE CONTROLS: the spec's own Type, and no Type, may run
            foreach (var pose in new[] { PoseSpecsType, PoseNoType })
            {
                var ok = ProbePlan.Prepare(_root, Sent(PoseShot(pose), BoardRigAdapter));
                Assert.IsNull(ok.Refusal, ok.Refusal);
                Assert.IsTrue(ok.MayRun, pose + ": " + ok.LeverRefused);
            }
        }

        /// <summary>Second audit M1, the auditor's case (a): synced.json absent, adapter.json
        /// byte-for-byte the sent one — the disk says "ungated", so CameraPose's own Type check
        /// stood down and a try posed SaveManager with the methods a BoardRig spec approved.
        /// Refused at Prepare, AND by the run's own bridge (the director a try builds), with the
        /// try's cloud flag.</summary>
        [Test]
        public void Run_DuringATryOnAnUngatedDisk_ACameraPoseOfAnotherTypeNeverReachesTheGame()
        {
            WriteOwnFiles();
            OnDisk(_root, BoardRigAdapter);
            Assert.IsFalse(Levers.GateActive(_root), "precondition: no synced.json — the disk says ungated");
            Tick(PoseOtherType, PoseSpecsType, BoardRigSpec);

            var plan = ProbePlan.Prepare(_root, Sent(PoseShot(PoseOtherType), BoardRigAdapter));
            Assert.AreEqual(OtherTypeRefused, plan.Refusal, "refused before the run");

            // the run's own half, past the pre-check: the director ProbeRun.DirectorOptions builds
            var (d, cheats, _) = RunDirector(plan, bypassPlan: true);
            CollectionAssert.DoesNotContain(cheats.Run_, PoseOtherType,
                "a try posed a Type the ticked camera-spec did not name");
            StringAssert.Contains(OtherTypeRefused, d!.Summary, "the refusal is in the run's log");
            Assert.AreEqual(0, d.FailedStepIndex, "the pose's step fails by name");

            // POSITIVE CONTROL: the spec's own Type, same project, same ticks — it reaches the game
            var ok = ProbePlan.Prepare(_root, Sent(PoseShot(PoseSpecsType), BoardRigAdapter));
            Assert.IsTrue(ok.MayRun, ok.Refusal ?? ok.LeverRefused);
            var (_, okCheats, _) = RunDirector(ok);
            CollectionAssert.Contains(okCheats.Run_, PoseSpecsType);
        }

        /// <summary>Second audit M1: the guard asks with the RUN's cloud flag, never the disk's
        /// answer, and asks before the gate — a pose no tick can help is not offered as a row.</summary>
        [Test]
        public void CameraTypeGuard_AsksWithTheRunsCloudFlag_NotTheDisks()
        {
            WriteOwnFiles();
            OnDisk(_root, BoardRigAdapter);
            Assert.IsFalse(Levers.GateActive(_root), "precondition: the disk says ungated");
            var log = new List<string>();
            var inner = new RecordingBridge();
            var gate = new LeverGateBridge(inner, _root, log.Add, cloudContent: true);

            Assert.IsFalse(new ProbeCameraTypeGuard(gate, _root, log.Add, cloudContent: true).Run(PoseOtherType));
            CollectionAssert.IsEmpty(inner.Run_, "the command reached the game");
            CollectionAssert.AreEqual(new[] { OtherTypeRefused }, log, "logged the way the gate logs its own, and only that");
            CollectionAssert.DoesNotContain(LeverGateBridge.RefusedThisSession.ToList(), PoseOtherType,
                "offered as a row to tick, though no tick can make it run");

            // CONTROL: the studio's own run (not cloud content) on the same ungated project — the
            // command's Type stands, as it always did there
            Assert.IsTrue(new ProbeCameraTypeGuard(new LeverGateBridge(inner, _root, null), _root, null, cloudContent: false)
                .Run(PoseOtherType));
            // …and during a try, anything else goes on to the gate
            Tick(PoseSpecsType, BoardRigSpec);
            Assert.IsTrue(new ProbeCameraTypeGuard(gate, _root, null, cloudContent: true).Run(PoseSpecsType));
            CollectionAssert.AreEqual(new[] { PoseOtherType, PoseSpecsType }, inner.Run_);
        }

        /// <summary>Second audit M6: a frame a LOST lease left behind is deleted at the next probe or
        /// claim; the run in flight keeps its own (a retried POST reads it from disk).</summary>
        [Test]
        public void SweepStrayFrames_DeletesAnotherRunsFrame_AndKeepsTheRunInFlight()
        {
            var dir = RelayPaths.Root(_root);
            Directory.CreateDirectory(dir);
            var mine = ProbeRun.FramePath(_root, "run-now");
            var lost = ProbeRun.FramePath(_root, "run-lost-lease");
            var selfTest = Path.Combine(dir, "self-test-frame-run-x.png");
            var notAFrame = Path.Combine(dir, ProbeRun.FramePrefix + "notes.txt");
            foreach (var f in new[] { mine, lost, selfTest, notAFrame }) File.WriteAllBytes(f, new byte[] { 1 });
            Assert.AreEqual(dir, Path.GetDirectoryName(mine), "control: a try's frame lives in the relay folder");

            var gone = ProbeRun.SweepStrayFrames(_root, "run-now");
            Assert.IsFalse(File.Exists(lost), "a frame a lost lease left behind stays on disk for good");
            Assert.IsTrue(File.Exists(mine), "the run in flight keeps its frame — a retried POST reads it");
            Assert.IsTrue(File.Exists(selfTest), "a self-test's frame is not a try's");
            Assert.IsTrue(File.Exists(notAFrame), "only probe-frame-*.png");
            CollectionAssert.AreEqual(new[] { Path.GetFileName(lost) }, gone.Select(Path.GetFileName).ToArray());

            // with no run in flight, every try's frame goes
            CollectionAssert.AreEqual(new[] { Path.GetFileName(mine) },
                ProbeRun.SweepStrayFrames(_root, null).Select(Path.GetFileName).ToArray());
            Assert.IsFalse(File.Exists(mine));
            Assert.IsTrue(File.Exists(selfTest));
            // a project with no relay folder at all: nothing to do, nothing thrown
            CollectionAssert.IsEmpty(ProbeRun.SweepStrayFrames(Path.Combine(_root, "nowhere"), null));
        }

        // ---- the grammar's words -----------------------------------------------------------------------

        [Test]
        public void ShotKind_EveryStepKindHasItsShotsJsonSpelling()
        {
            var expected = new Dictionary<AdStepKind, string>
            {
                [AdStepKind.Click] = "click", [AdStepKind.Hold] = "hold", [AdStepKind.Wait] = "wait",
                [AdStepKind.WaitFor] = "waitFor", [AdStepKind.Cheat] = "cheat",
                [AdStepKind.TimeScale] = "timeScale", [AdStepKind.Vision] = "vision",
                [AdStepKind.CheatUntil] = "cheatUntil",
            };
            foreach (AdStepKind kind in Enum.GetValues(typeof(AdStepKind)))
            {
                Assert.IsTrue(expected.ContainsKey(kind), $"a step kind this test does not know: {kind}");
                Assert.AreEqual(expected[kind], ShotKind.Name(kind));
            }
        }

        // ---- slice E.4: the tutorial gate runs FIRST ----------------------------------------------------

        /// <summary>The pack's `tutorial-gate` shot as a claim carries it: one setup write, one cheat,
        /// settles on Board. Its levers: "SkipIntro", "SkipTutorial".</summary>
        private const string GateShot = @"{ ""name"": ""tutorial-gate"", ""setup"": [""SkipIntro""],
  ""steps"": [ { ""kind"": ""cheat"", ""command"": ""SkipTutorial"" } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";

        private static ProbeGate GateOf(string text, string? sha = null) =>
            new(text, sha ?? SyncNova.Sha256OfText(text));

        private static ProbeRequest SentWithGate(string shot, string gate, string adapter = PlainAdapter,
            string? gateSha = null) =>
            new(shot, adapter, SyncNova.Sha256OfText(shot), SyncNova.Sha256OfText(adapter), GateOf(gate, gateSha));

        private ProbePlan LoadedWithGate(string shot, string gate)
        {
            var plan = ProbePlan.Prepare(_root, SentWithGate(shot, gate));
            Assert.IsNull(plan.Refusal, plan.Refusal);
            Assert.IsNotNull(plan.Gate);
            return plan;
        }

        /// <summary>A recorder that writes WHEN it started, and for what, into the same list the game's
        /// commands go to — so "the gate's writes, then the recording" is one list.</summary>
        private sealed class OrderedRecorder : IRecorderDriver
        {
            private readonly List<string> _events;
            public readonly List<string> Starts = new();
            public OrderedRecorder(List<string> events, string dir) { _events = events; OutputDir = dir; }
            public string OutputDir { get; }
            public bool IsRecording { get; private set; }
            public int OutputWidth => 0;
            public int OutputHeight => 0;
            public void Start(string clipName)
            {
                Starts.Add(clipName);
                _events.Add("REC " + clipName);
                IsRecording = true;
            }
            public void Stop() => IsRecording = false;
        }

        /// <summary>A ready gate that notes what it was told, EVERY call (first audit of E.4, M1: a gated
        /// try asks it twice) — and, given a list, writes "READY name…" into it, so its place among the
        /// game's commands is one list. <see cref="Opens"/> decides each call's answer.</summary>
        private sealed class SeeingReadyGate : IReadyGate
        {
            public List<string> Upcoming = new();
            public readonly List<List<string>> Calls = new();
            public List<string>? Events;
            public Func<IReadOnlyList<string>, bool> Opens = _ => true;
            public IEnumerable WaitUntilReady(DirectorContext ctx)
            {
                Upcoming = ctx.UpcomingShots.Select(u => u.Name).ToList();
                Calls.Add(Upcoming);
                Events?.Add("READY " + string.Join(",", Upcoming));
                ctx.LastOpSucceeded = Opens(Upcoming);
                yield break;
            }
        }

        private sealed class CountingVision : IVisionChannel
        {
            public int Requests;
            public string? Request(string prompt, int stepIndex) { Requests++; return "v"; }
            public bool TryGetResult(string id, out bool match) { match = true; return true; }
        }

        private static ProbeRequest? ParseWithGate(string gate)
        {
            CaptureJob.ParseClaim(
                @"{ ""job"": { ""runId"": ""r1"", ""kind"": ""probe"" }, ""probe"": { ""shot"": ""S"", ""adapter"": ""A"", ""shotSha256"": ""aa"", ""adapterSha256"": ""bb"""
                + gate + " } }",
                out var error, out _, out var probe);
            Assert.IsNull(error);
            return probe;
        }

        [Test]
        public void Gate_TheClaimCarriesIt_NullIsNone_AndAHalfDeliveredGateIsNoScriptAtAll()
        {
            var with = ParseWithGate(@", ""tutorialGate"": { ""shot"": ""G"", ""sha256"": ""gg"" }");
            Assert.AreEqual("G", with!.TutorialGate!.Shot);
            Assert.AreEqual("gg", with.TutorialGate.Sha256);
            Assert.IsNull(ParseWithGate(@", ""tutorialGate"": null")!.TutorialGate, "null is no gate");
            Assert.IsNull(ParseWithGate("")!.TutorialGate, "absent is no gate");
            foreach (var bad in new[]
                     {
                         @", ""tutorialGate"": { ""shot"": ""G"" }",
                         @", ""tutorialGate"": { ""shot"": """", ""sha256"": ""gg"" }",
                         @", ""tutorialGate"": { ""shot"": { ""name"": ""x"" }, ""sha256"": ""gg"" }",
                         @", ""tutorialGate"": ""G""",
                         @", ""tutorialGate"": []",
                     })
                Assert.IsNull(ParseWithGate(bad), "a try whose gate did not arrive whole must not run without it: " + bad);
        }

        [Test]
        public void Gate_SurvivesThePlayModeReload_OnTheProgressFile()
        {
            var request = SentWithGate(CheatShot, GateShot);
            var p = CaptureProgress.Start(new CaptureJob("r", "w", Array.Empty<CaptureJobItem>(), 60, "probe", "g"), request);
            var file = Path.Combine(_root, "capture-status.json");
            p.Save(file);
            var back = CaptureProgress.Load(file)!;
            Assert.IsNotNull(back.Probe!.TutorialGate, "the gate did not survive the reload — the resumed try would run without it");
            Assert.AreEqual(GateShot, back.Probe.TutorialGate!.Shot);
            Assert.AreEqual(request.TutorialGate!.Sha256, back.Probe.TutorialGate.Sha256);
            Assert.AreEqual("tutorial-gate", ProbePlan.Prepare(_root, back.Probe).Gate!.Name);
            // …and a try without one reloads without one
            var none = CaptureProgress.Start(new CaptureJob("r", "w", Array.Empty<CaptureJobItem>(), 60, "probe", "g"),
                Sent(CheatShot, PlainAdapter));
            none.Save(file);
            Assert.IsNull(CaptureProgress.Load(file)!.Probe!.TutorialGate);
        }

        [Test]
        public void Gate_Prepare_ChecksItsShaBeforeAnyUse_AndOnlyTheShotNamedTutorialGateIsOne()
        {
            var wrong = ProbePlan.Prepare(_root, SentWithGate(CheatShot, GateShot, gateSha: new string('0', 64)));
            StringAssert.StartsWith("the tutorial gate that arrived is not the one the website sent", wrong.Refusal);
            Assert.IsFalse(wrong.MayRun);
            var notJson = ProbePlan.Prepare(_root, SentWithGate(CheatShot, "not json"));
            StringAssert.StartsWith("the tutorial gate that arrived is not one JSON object", notJson.Refusal);
            var named = ProbePlan.Prepare(_root, SentWithGate(CheatShot, GateShot.Replace("tutorial-gate", "intro")));
            Assert.AreEqual("the tutorial gate that arrived is not the shot named 'tutorial-gate' — nothing was run", named.Refusal);
            var broken = ProbePlan.Prepare(_root,
                SentWithGate(CheatShot, @"{ ""name"": ""tutorial-gate"", ""steps"": [ { ""kind"": ""wait"" } ] }"));
            StringAssert.StartsWith("this shot and its tutorial gate do not load: ", broken.Refusal);
        }

        [Test]
        public void Gate_Prepare_LoadsTheGateAndTheShot_TheGateRunsFirst_AndTheLeversAreBoth()
        {
            var plan = ProbePlan.Prepare(_root, SentWithGate(CheatShot, GateShot));
            Assert.IsNull(plan.Refusal, plan.Refusal);
            Assert.AreEqual("tutorial-gate", plan.Gate!.Name);
            Assert.AreEqual("try-me", plan.Shot!.Name);
            Assert.AreSame(plan.Gate, plan.FirstToRun, "the try starts with the gate — the agent waits for ITS screen");
            Assert.AreEqual(SyncNova.Sha256OfText(GateShot), plan.GateSha256);
            // ruling 4: the shot's ∪ the adapter's ∪ the gate's — the list the site sends for such a try
            CollectionAssert.AreEqual(new[] { "SelectHero Knight", "SkipIntro", "SkipTutorial", "set Coins 5" },
                plan.LeversNeeded);
            // without a gate: the shot alone, and the shot comes first
            var none = ProbePlan.Prepare(_root, Sent(CheatShot, PlainAdapter));
            Assert.IsNull(none.Gate);
            Assert.AreSame(none.Shot, none.FirstToRun);
            CollectionAssert.AreEqual(new[] { "SelectHero Knight", "set Coins 5" }, none.LeversNeeded);
        }

        [Test]
        public void Gate_ALeverOnlyTheGateNeeds_RefusesTheWholeTry_BeforeADirectorExists()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro");
            var plan = ProbePlan.Prepare(_root, SentWithGate(CheatShot, GateShot));
            Assert.IsNull(plan.Refusal, plan.Refusal);
            Assert.AreEqual("SkipTutorial", plan.LeverRefused, "the shot's own levers are ticked — only the gate's is not");
            Assert.IsFalse(plan.MayRun);
            var (d, cheats, _) = RunDirector(plan);
            Assert.IsNull(d, "a director was built for a try whose gate needs an un-ticked lever");
            CollectionAssert.IsEmpty(cheats.Run_, "a command reached the game: " + string.Join(", ", cheats.Run_));
            Tick("SkipTutorial");
            Assert.IsTrue(ProbePlan.Prepare(_root, SentWithGate(CheatShot, GateShot)).MayRun);
        }

        [Test]
        public void Gate_RunsFirst_WithTheRecorderOff_ThenTheShotAsToday()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro", "SkipTutorial");
            var plan = LoadedWithGate(CheatShot, GateShot);
            var events = new RecordingBridge();
            var recorder = new OrderedRecorder(events.Run_, Path.Combine(_root, "never"));
            var releases = 0;
            var (d, _, _) = RunDirector(plan, tweak: o =>
                {
                    o.Recorder = recorder;
                    o.ReleaseCameraHold = () => releases++;
                },
                adapterTweak: a => a.CheatBridge = events);
            // the gate's setup and step, NOTHING recording; then the shot as today — its setup, the
            // recorder, its steps
            CollectionAssert.AreEqual(
                new[] { "SkipIntro", "SkipTutorial", "set Coins 5", "REC try-me", "SelectHero Knight" },
                Writes(events), d!.Summary);
            CollectionAssert.AreEqual(new[] { "try-me" }, recorder.Starts, "the gate was recorded, or the shot was not");
            Assert.IsTrue(d.PreambleRan);
            Assert.IsNull(d.PreambleFailedStepIndex);
            Assert.IsTrue(d.AllCaptured, d.Summary);
            Assert.IsNull(d.FailedStepKindName);
            Assert.IsTrue(ProbeFacts.Reached(d.AllCaptured, d.FailedStepIndex, plan.LeverRefused));
            // the camera hold is released after the gate, as after every shot — the shot must not
            // inherit a pose the gate left armed: the gate's release, the shot's, and Finish's
            Assert.AreEqual(3, releases);
        }

        [Test]
        public void Gate_AStopAtAGateStep_IsTheTrysStop_TheShotNeverRuns_AndTheScreenIsReadThere()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro");
            const string gate = @"{ ""name"": ""tutorial-gate"", ""setup"": [""SkipIntro""],
  ""steps"": [ { ""kind"": ""wait"", ""seconds"": 0.2 },
               { ""kind"": ""click"", ""name"": ""SkipBTN"", ""timeout"": 1 } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";
            var plan = LoadedWithGate(CheatShot, gate);
            var events = new RecordingBridge();
            var recorder = new OrderedRecorder(events.Run_, Path.Combine(_root, "never"));
            var hooked = 0;
            var hookDone = 0;
            var (d, _, _) = RunDirector(plan, tweak: o =>
                {
                    o.Recorder = recorder;
                    o.OnStopped = () => Steps(2, () => hooked++, () => hookDone++);
                },
                adapterTweak: a => a.CheatBridge = events);
            CollectionAssert.AreEqual(new[] { "SkipIntro" }, Writes(events), "the shot ran after its gate stopped: " + d!.Summary);
            CollectionAssert.IsEmpty(recorder.Starts);
            Assert.IsTrue(d.PreambleRan);
            Assert.AreEqual(1, d.PreambleFailedStepIndex, d.Summary);
            Assert.IsNull(d.FailedStepIndex, "no step of the SHOT stopped it");
            Assert.AreEqual(AdDirector.FailedKindPreamble, d.FailedStepKindName);
            Assert.AreEqual("tutorial-gate", AdDirector.FailedKindPreamble, "the wire's word");
            Assert.AreEqual("tutorial-gate: Click 'SkipBTN' did not resolve", d.FailedReason);
            CollectionAssert.Contains(d.LogLines, "  " + d.FailedReason);
            Assert.IsFalse(d.AllCaptured);
            Assert.AreEqual(1, hooked, "the screen was not read at the gate's stop");
            Assert.AreEqual(1, hookDone, "the stop hook was not PUMPED at the gate's stop (only Finish's fallback ran it)");

            var facts = ProbeFacts.Build(plan.Shot!.Name, d.AllCaptured, d.FailedStepIndex, d.FailedStepKindName,
                d.FailedReason, d.LogLines, null, 1, 1, plan.ShotSha256, plan.AdapterSha256, plan.LeversNeeded,
                plan.LeversApproved, plan.LeverRefused, 1f, false, "none", "0.0.0",
                gateRan: d.PreambleRan, gateFailedStep: d.PreambleFailedStepIndex);
            Assert.IsFalse(facts["reached"]!.Value<bool>());
            Assert.IsTrue(facts["gateRan"]!.Value<bool>());
            Assert.AreEqual(1, facts["gateFailedStep"]!.Value<int>());
            Assert.AreEqual(JTokenType.Null, facts["failedStep"]!.Type);
            Assert.AreEqual("tutorial-gate", facts["failedStepKind"]!.Value<string>());
        }

        [Test]
        public void Gate_ItsEndStateNotHolding_IsTheTrysStop_WithNoStepOfItsOwn()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro", "SkipTutorial");
            var plan = LoadedWithGate(CheatShot,
                GateShot.Replace(@"""name"": ""Board"" } }", @"""name"": ""Never"" }, ""settleTimeoutSec"": 1 }"));
            var events = new RecordingBridge();
            var (d, _, _) = RunDirector(plan, adapterTweak: a => a.CheatBridge = events);
            CollectionAssert.AreEqual(new[] { "SkipIntro", "SkipTutorial" }, Writes(events), d!.Summary);
            Assert.IsTrue(d.PreambleRan);
            Assert.IsNull(d.PreambleFailedStepIndex);
            Assert.AreEqual(AdDirector.FailedKindPreamble, d.FailedStepKindName);
            Assert.AreEqual("tutorial-gate: every step ran, but settle Present 'Never' did not hold within 1s", d.FailedReason);
            Assert.IsNull(d.FailedStepIndex);
            Assert.IsFalse(d.AllCaptured);
        }

        [Test]
        public void Gate_APlaceholderItCannotBind_StopsTheTryAtThatStepOfTheGate()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro");
            const string gate = @"{ ""name"": ""tutorial-gate"", ""parameters"": [""hero""], ""setup"": [""SkipIntro""],
  ""steps"": [ { ""kind"": ""wait"", ""seconds"": 0.2 }, { ""kind"": ""click"", ""name"": ""Pick{hero}"" } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";
            var plan = LoadedWithGate(CheatShot, gate);
            var events = new RecordingBridge();
            var (d, _, _) = RunDirector(plan, adapterTweak: a => a.CheatBridge = events);
            Assert.AreEqual(1, d!.PreambleFailedStepIndex, d.Summary);
            StringAssert.StartsWith("ABORT tutorial-gate: ", d.FailedReason);
            CollectionAssert.AreEqual(new[] { "SkipIntro" }, Writes(events), "the shot ran after its gate stopped");
        }

        /// <summary>First audit of E.4, S1: <see cref="ProbePlan.Prepare"/> refuses such a gate WHOLE now
        /// (<see cref="Gate_AScreenCheckInIt_RefusesTheWholeTry_BeforeAnythingRuns"/>), so no try reaches
        /// this; the director's own rule stands behind it — for a caller that hands it one anyway.</summary>
        [Test]
        public void Gate_AScreenCheckInIt_IsNeverAsked_AndStopsTheTryThere()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro");
            var plan = Loaded(_root, CheatShot);
            var vision = new CountingVision();
            var (d, _, _) = RunDirector(plan, bypassPlan: true, tweak: o =>
            {
                o.Vision = vision;
                o.Preamble = LoadOne(VisionGate);
            });
            Assert.AreEqual(0, vision.Requests, "a gate step's index was asked of the site as a step of the shot");
            Assert.AreEqual(0, d!.PreambleFailedStepIndex, d.Summary);
            Assert.AreEqual(AdDirector.FailedKindPreamble, d.FailedStepKindName);
            StringAssert.EndsWith(AdDirector.PreambleVisionNotAsked, d.FailedReason);
        }

        [Test]
        public void Gate_TheReadyGateStopsBeforeIt_AndOnlyTheClaimDecidesWhetherItRuns()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro", "SkipTutorial");
            var plan = LoadedWithGate(CheatShot, GateShot);
            var (d, _, _) = RunDirector(plan, gate: new NeverReadyGate());
            Assert.IsFalse(d!.PreambleRan, "the gate ran behind a ready gate that never opened");
            Assert.AreEqual(AdDirector.FailedKindReady, d.FailedStepKindName);
            AdDirector.Active?.Finish("next run");
            // the ready gate is told the WORK that is coming — the gate first, then the shot
            var seeing = new SeeingReadyGate();
            var (s, _, _) = RunDirector(plan, gate: seeing);
            // first audit of E.4, M1: the gate ALONE first, then — after it held — the shot
            Assert.AreEqual(2, seeing.Calls.Count, s!.Summary);
            CollectionAssert.AreEqual(new[] { "tutorial-gate" }, seeing.Calls[0], s.Summary);
            CollectionAssert.AreEqual(new[] { "try-me" }, seeing.Calls[1], s.Summary);
            AdDirector.Active?.Finish("next run");
            // a caller that set a preamble on a try whose claim carried NONE: the one door clears it
            var none = Loaded(_root, CheatShot);
            var events = new RecordingBridge();
            var (e, _, _) = RunDirector(none, tweak: o => o.Preamble = plan.Gate,
                adapterTweak: a => a.CheatBridge = events);
            Assert.IsFalse(e!.PreambleRan, "a gate the claim did not carry ran");
            CollectionAssert.AreEqual(new[] { "set Coins 5", "SelectHero Knight" }, Writes(events));
        }

        [Test]
        public void Gate_TheFactsAlwaysSayWhetherItRan()
        {
            var f = Facts();
            Assert.IsFalse(f["gateRan"]!.Value<bool>(), "a kit that knows the gate always says so — false here");
            Assert.AreEqual(JTokenType.Null, f["gateFailedStep"]!.Type);
            var g = ProbeFacts.Build("s", false, null, "tutorial-gate", "r", null, null, 1, 1, "aa", "bb",
                new[] { "x" }, new[] { "x" }, null, 1f, true, null, "9.9.9", gateRan: true, gateFailedStep: 0);
            Assert.IsTrue(g["gateRan"]!.Value<bool>());
            Assert.AreEqual(0, g["gateFailedStep"]!.Value<int>());
        }

        // ---- the first fresh-context audit of E.4 -------------------------------------------------------

        /// <summary>The auditor's gate: one setup write, then a SCREEN CHECK.</summary>
        private const string VisionGate = @"{ ""name"": ""tutorial-gate"", ""setup"": [""SkipIntro""],
  ""steps"": [ { ""kind"": ""vision"", ""prompt"": ""the tutorial is gone"", ""timeout"": 5 } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";

        /// <summary>One shot, read by the real loader — for a caller that hands the director a preamble
        /// itself (<c>bypassPlan</c>), past what <see cref="ProbePlan.Prepare"/> would refuse.</summary>
        private static AdShot LoadOne(string shot)
        {
            var loaded = JsonShotLoader.LoadFrom(@"{ ""$schemaVersion"": 1, ""shots"": [ " + shot + " ] }", remember: false);
            Assert.IsEmpty(loaded.Errors, string.Join(" · ", loaded.Errors));
            return loaded.Shots[0];
        }

        /// <summary>A recovery policy that writes "RECOVER" into the game's command list each time it runs.</summary>
        private sealed class EventRecovery : IRecoveryPolicy
        {
            private readonly List<string> _events;
            public EventRecovery(List<string> events) { _events = events; }
            public IEnumerable Recover(DirectorContext ctx)
            {
                _events.Add("RECOVER");
                ctx.LastOpSucceeded = true;
                yield break;
            }
        }

        /// <summary>A screen where some names appear only once the RUN's clock reaches a time.</summary>
        private sealed class TimedUi : IUiDriver
        {
            public Func<double> Now = () => 0;
            public readonly HashSet<string> Names = new() { "Board", "Go" };
            public readonly Dictionary<string, double> AppearsAt = new();
            public bool Exists(string name) => Names.Contains(name) || (AppearsAt.TryGetValue(name, out var t) && Now() >= t);
            public bool IsInteractable(string name) => Exists(name);
            public string? GetText(string name) => null;
            public bool Click(string name, int index = 0) => Exists(name);
            public void PointerDown(string name, int index = 0) { }
            public void PointerUp(string name, int index = 0) { }
        }

        /// <summary>S1 — a gate with a screen check is refused WHOLE, by the site's own fact, before any of
        /// its steps (or the shot's) could run: the site never makes one active, and this is the belt.</summary>
        [Test]
        public void Gate_AScreenCheckInIt_RefusesTheWholeTry_BeforeAnythingRuns()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro");
            var plan = ProbePlan.Prepare(_root, SentWithGate(CheatShot, VisionGate));
            Assert.AreEqual(ProbePlan.GateHasScreenCheck, plan.Refusal);
            StringAssert.Contains("your `tutorial-gate` shot has a screen check, which a gate cannot ask", plan.Refusal,
                "the site's fact, in the site's words");
            Assert.IsFalse(plan.MayRun);
            var (d, cheats, _) = RunDirector(plan);
            Assert.IsNull(d, "a director was built for a try whose gate holds a screen check");
            CollectionAssert.IsEmpty(cheats.Run_, "a command reached the game: " + string.Join(", ", cheats.Run_));
            // CONTROLS: the same gate without its screen check is a gate; a SHOT with one (no gate) is tried
            Assert.IsNull(ProbePlan.Prepare(_root, SentWithGate(CheatShot, GateShot)).Refusal);
            var visionShot = Shot("look", @"{ ""kind"": ""vision"", ""prompt"": ""the shop is open"" }");
            Assert.IsNull(ProbePlan.Prepare(_root, Sent(visionShot, PlainAdapter)).Refusal);
        }

        /// <summary>M1 — THE READY GATE MEETS WHAT RUNS NEXT: the gate ALONE first, then — once it held —
        /// the shot, each where it stands among the game's commands.</summary>
        [Test]
        public void Gate_TheReadyGateMeetsWhatRunsNext_TheGateAlone_ThenTheShot()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro", "SkipTutorial");
            var plan = LoadedWithGate(CheatShot, GateShot);
            var events = new RecordingBridge();
            var recorder = new OrderedRecorder(events.Run_, Path.Combine(_root, "never"));
            var seeing = new SeeingReadyGate { Events = events.Run_ };
            var (d, _, _) = RunDirector(plan, gate: seeing, tweak: o => o.Recorder = recorder,
                adapterTweak: a => a.CheatBridge = events);
            CollectionAssert.AreEqual(
                new[] { "READY tutorial-gate", "SkipIntro", "SkipTutorial", "READY try-me", "set Coins 5", "REC try-me", "SelectHero Knight" },
                Writes(events), d!.Summary);
            Assert.IsTrue(d.AllCaptured, d.Summary);
        }

        private const string ReadyAdapter =
            @"{ ""gameId"": ""g"", ""ready"": { ""mute"": ""set Audio.mute true"", ""resistOn"": ""set Player.resist true"" } }";

        /// <summary>M1, the auditor's case: a gate marked <c>baseline: lobby</c> (the tutorial screen, where
        /// the ready block's board writes cannot land) and a board shot — the FIRST ready gate asks nothing
        /// of the board, the gate runs, and only THEN the board writes, before the shot. CONTROL: a gate with
        /// no baseline (board) meets them before it, and the shot again after it (the writes are the ready
        /// block's own set-to-value commands, which every try already runs).</summary>
        [Test]
        public void Gate_ALobbyGate_TheFirstReadyGateAsksNothingOfTheBoard_TheBoardWritesComeAfterTheGate()
        {
            OnDisk(_root, ReadyAdapter);
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro", "SkipTutorial", "set Audio.mute true", "set Player.resist true");
            var lobbyGate = GateShot.Replace(@"""name"": ""tutorial-gate"",", @"""name"": ""tutorial-gate"", ""baseline"": ""lobby"",");
            Assert.AreNotEqual(GateShot, lobbyGate, "precondition: the gate is marked lobby");
            (AdDirector d, List<string> writes) Run(string gate)
            {
                var plan = ProbePlan.Prepare(_root, SentWithGate(CheatShot, gate, ReadyAdapter));
                Assert.IsTrue(plan.MayRun, plan.Refusal ?? plan.LeverRefused);
                var events = new RecordingBridge();
                var recorder = new OrderedRecorder(events.Run_, Path.Combine(_root, "never"));
                var hygiene = new HygieneReadyGate(() => HygieneSpec.Parse(ReadyAdapter), () => 1f, _ => { }, () => null);
                var (run, _, _) = RunDirector(plan, gate: hygiene, tweak: o => o.Recorder = recorder,
                    adapterTweak: a => a.CheatBridge = events);
                AdDirector.Active?.Finish("next run");
                return (run!, Writes(events).ToList());
            }
            var (d, writes) = Run(lobbyGate);
            CollectionAssert.AreEqual(
                new[] { "SkipIntro", "SkipTutorial", "set Audio.mute true", "set Player.resist true", "set Coins 5", "REC try-me", "SelectHero Knight" },
                writes, d.Summary);
            Assert.IsTrue(d.AllCaptured, d.Summary);
            var (b, boardWrites) = Run(GateShot);
            CollectionAssert.AreEqual(
                new[] { "set Audio.mute true", "set Player.resist true", "SkipIntro", "SkipTutorial",
                        "set Audio.mute true", "set Player.resist true", "set Coins 5", "REC try-me", "SelectHero Knight" },
                boardWrites, b.Summary);
        }

        /// <summary>M1 — a gate STOP ends the try: the second ready gate never runs.</summary>
        [Test]
        public void Gate_AGateStop_TheSecondReadyGateNeverRuns()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro");
            var plan = LoadedWithGate(CheatShot, @"{ ""name"": ""tutorial-gate"", ""setup"": [""SkipIntro""],
  ""steps"": [ { ""kind"": ""click"", ""name"": ""SkipBTN"", ""timeout"": 1 } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }");
            var seeing = new SeeingReadyGate();
            var (d, _, _) = RunDirector(plan, gate: seeing);
            Assert.AreEqual(AdDirector.FailedKindPreamble, d!.FailedStepKindName, d.Summary);
            Assert.AreEqual(1, seeing.Calls.Count, "the ready gate ran again after a gate that stopped: " + d.Summary);
            CollectionAssert.AreEqual(new[] { "tutorial-gate" }, seeing.Calls[0]);
        }

        /// <summary>M1, and the fact behind M2's ruling: the ready gate that runs AFTER the gate held can
        /// stop the try — a <c>ready</c> stop with the gate RUN (<c>gateRan: true</c>), which the site
        /// therefore accepts; the shot never starts, and the screen is read there, once.</summary>
        [Test]
        public void Gate_TheReadyGateAfterIt_CanStopTheTry_WithTheGateRun()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro", "SkipTutorial");
            var plan = LoadedWithGate(CheatShot, GateShot);
            var events = new RecordingBridge();
            var recorder = new OrderedRecorder(events.Run_, Path.Combine(_root, "never"));
            var seeing = new SeeingReadyGate { Events = events.Run_, Opens = up => !up.Contains("try-me") };
            var hooked = 0;
            var (d, _, _) = RunDirector(plan, gate: seeing, tweak: o =>
                {
                    o.Recorder = recorder;
                    o.OnStopped = () => Steps(1, () => hooked++, () => { });
                },
                adapterTweak: a => a.CheatBridge = events);
            Assert.IsTrue(d!.PreambleRan);
            Assert.IsNull(d.PreambleFailedStepIndex);
            Assert.AreEqual(AdDirector.FailedKindReady, d.FailedStepKindName, d.Summary);
            Assert.IsNull(d.FailedStepIndex);
            CollectionAssert.IsEmpty(recorder.Starts, "the shot started behind a ready gate that never opened");
            // the gate, then the ready gate for the shot — twice (its recovery retry) — and nothing of the shot
            CollectionAssert.AreEqual(new[] { "READY tutorial-gate", "SkipIntro", "SkipTutorial", "READY try-me", "READY try-me" },
                Writes(events), d.Summary);
            Assert.AreEqual(1, hooked, "the screen was not read at the stop");
        }

        /// <summary>Second audit of E.4, KM2 — the ready gate that runs AFTER the gate held stops the try, and the
        /// stop hook runs AT that stop, pumped to its end — not left to <c>Finish</c>'s fallback, which runs only
        /// its first step (the try's evidence would then be read after the run, not at the stop).</summary>
        [Test]
        public void Gate_TheSecondReadyGatesStop_PumpsTheStopHookToItsEnd_AtTheStop()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro", "SkipTutorial");
            var plan = LoadedWithGate(CheatShot, GateShot);
            var seeing = new SeeingReadyGate { Opens = up => !up.Contains("try-me") };
            var calls = 0;
            var completed = 0;
            var (d, _, _) = RunDirector(plan, gate: seeing,
                tweak: o => o.OnStopped = () => Steps(2, () => calls++, () => completed++));
            Assert.IsTrue(d!.PreambleRan, d.Summary);
            Assert.AreEqual(AdDirector.FailedKindReady, d.FailedStepKindName, d.Summary);
            Assert.Greater(seeing.Calls.Count, 1, "the ready gate did not run again for the shot");
            Assert.AreEqual(1, calls, "the stop hook did not run: " + d.Summary);
            Assert.AreEqual(1, completed,
                "the second ready gate's stop left the hook to Finish's fallback, which runs only its first step");
        }

        /// <summary>Second audit of E.4, KM4 + KM5 — A TRY'S FACTS come from the ONE function the agent calls
        /// (<see cref="ProbeFacts.OfTry"/>): they echo the sha of the gate text that ARRIVED (a dropped argument
        /// would say null, and every gated try would be refused as another gate), and say WHOSE UI driver read
        /// the screen from the adapter handed in (a dropped argument would say "kit" for a custom driver,
        /// silently). The document is the one the agent's inline call built, argument by argument.</summary>
        [Test]
        public void Facts_OfTry_EchoTheGateThatArrived_AndSayWhoseUiDriverRead_AsTheAgentsCallDid()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro", "SkipTutorial");
            var plan = LoadedWithGate(CheatShot, GateShot);
            var (d, _, ui) = RunDirector(plan);
            Assert.IsTrue(d!.AllCaptured, d.Summary);
            var names = new[] { "Board [Image]" };
            JObject Of(GameAdapter a, AdDirector? run, ProbePlan p) =>
                ProbeFacts.OfTry(p, run, a, names, 1280, 720, 0.5f, true, null, null);
            var custom = Of(new GameAdapter { Ui = ui }, d, plan);
            Assert.AreEqual(SyncNova.Sha256OfText(GateShot), (string?)custom["gateSha256"], "the gate that arrived is not echoed");
            Assert.AreEqual(ProbeFacts.UiDriverCustom, (string?)custom["uiDriver"], "a custom driver's try was said to be the kit's");
            Assert.AreEqual(ProbeFacts.UiDriverKit, (string?)Of(new GameAdapter { Ui = null }, d, plan)["uiDriver"]);
            Assert.AreEqual(ProbeFacts.UiDriverKit, (string?)Of(new GameAdapter { Ui = new UguiDriver() }, d, plan)["uiDriver"]);
            Assert.IsTrue((bool)custom["gateRan"]!);
            // the call the agent made inline before this seam, argument by argument: the same document
            var inline = ProbeFacts.Build(
                shot: plan.Shot!.Name, allCaptured: d.AllCaptured, failedStep: d.FailedStepIndex,
                failedStepKind: d.FailedStepKindName, reason: d.FailedReason, log: d.LogLines, uiDump: names,
                screenWidth: 1280, screenHeight: 720, shotSha256: plan.ShotSha256, adapterSha256: plan.AdapterSha256,
                leversNeeded: plan.LeversNeeded, leversApproved: plan.LeversApproved, leverRefused: plan.LeverRefused,
                timeScale: 0.5f, frameCaptured: true, frameNote: null, kitVersion: KitVersion.Current, factsError: null,
                gateRan: d.PreambleRan, gateFailedStep: d.PreambleFailedStepIndex, gateSha256: plan.GateSha256,
                uiDriver: ProbeFacts.UiDriverOf(new GameAdapter { Ui = ui }));
            Assert.IsTrue(JToken.DeepEquals(inline, custom), custom.ToString());
            // a lever refused before any director: the lever gate's sentence, nothing ran — the gate still echoed
            AdDirector.Active?.Finish("next");
            Assert.IsNull(Levers.SetApproved(_root, "SkipTutorial", false));
            var refused = LoadedWithGate(CheatShot, GateShot);
            Assert.AreEqual("SkipTutorial", refused.LeverRefused);
            var notRun = Of(new GameAdapter { Ui = ui }, null, refused);
            Assert.AreEqual(Levers.NotApprovedLog("SkipTutorial"), (string?)notRun["reason"]);
            Assert.IsFalse((bool)notRun["gateRan"]!);
            Assert.AreEqual(SyncNova.Sha256OfText(GateShot), (string?)notRun["gateSha256"]);
            Assert.AreEqual(ProbeFacts.UiDriverCustom, (string?)notRun["uiDriver"]);
        }

        /// <summary>M1 — WITH NO GATE THE RUN IS EXACTLY TODAY'S: one ready gate, told of the shot; the same
        /// commands, step marks and log, pinned before the change and after it.</summary>
        [Test]
        public void NoGate_TheRunIsExactlyTodays_OneReadyGate_TheSameWritesMarksAndLog()
        {
            Tick("SelectHero Knight", "set Coins 5");
            var plan = Loaded(_root, CheatShot);
            var events = new RecordingBridge();
            var recorder = new OrderedRecorder(events.Run_, Path.Combine(_root, "never"));
            var seeing = new SeeingReadyGate { Events = events.Run_ };
            var (d, _, _) = RunDirector(plan, gate: seeing, tweak: o => o.Recorder = recorder,
                adapterTweak: a => a.CheatBridge = events);
            CollectionAssert.AreEqual(new[] { "READY try-me", "set Coins 5", "REC try-me", "SelectHero Knight" },
                Writes(events), d!.Summary);
            Assert.AreEqual(1, seeing.Calls.Count);
            Assert.IsFalse(d.PreambleRan);
            Assert.IsTrue(d.AllCaptured, d.Summary);
            var marks = d.StepMarks.Select(m => $"{m.Index}|{m.Kind}|{m.Label}|{m.AtSec:0.0}").ToList();
            CollectionAssert.AreEqual(NoGateMarks, marks, string.Join("\n", marks));
            CollectionAssert.AreEqual(NoGateLog, d.LogLines, string.Join("\n", d.LogLines));
            CollectionAssert.IsEmpty(LeverGateBridge.RefusedThisSession.ToList());
        }

        /// <summary>Read off the director BEFORE the first audit of E.4's M1 change (46f80061's), and
        /// unchanged after it. STACK PASS 3 (the E.3 → E.4 merge): TODAY'S RUN MOVED UNDER IT, by A2′'s thirteenth
        /// fold, not by E.4 — this run never enters RunPreamble (PreambleRan is false above). One pump is 0.1 s here:
        /// a yield after each step puts the click at 0.6 (was 0.5, the cheat's 0.4 s wait then one pump), and the
        /// settle mark at 0.8 (was 0.5): a yield after the click, then WaitUntil's yield before its first
        /// evaluation. The writes, their order, the log and the one ready gate are unchanged.</summary>
        private static readonly string[] NoGateMarks =
            { "0|Cheat|Cheat 'SelectHero Knight'|0.0", "1|Click|Click 'Go'|0.6", "2|settled|settled|0.8" };
        private static readonly string[] NoGateLog = { "OK   try-me" };

        /// <summary>M2 — <c>gateRan</c> means the director STARTED the gate (its recovery, then its setup,
        /// then its steps): a gate stop at a setup placeholder it cannot bind is a stop WITH the gate run —
        /// the site refuses a gate stop with <c>gateRan: false</c> as a contradiction, so "the gate's steps
        /// started" would turn a real stop into a refused answer.</summary>
        [Test]
        public void Gate_ItRanFromTheMomentItStarted_ASetupPlaceholderItCannotBindIsAStopWithItRun()
        {
            Tick("SelectHero Knight", "set Coins 5");
            var plan = Loaded(_root, CheatShot);
            var gate = LoadOne(@"{ ""name"": ""tutorial-gate"", ""parameters"": [""hero""], ""setup"": [""Pick {hero}""],
  ""steps"": [ { ""kind"": ""cheat"", ""command"": ""SkipTutorial"" } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }");
            var events = new RecordingBridge();
            var (d, _, _) = RunDirector(plan, bypassPlan: true, tweak: o => o.Preamble = gate,
                adapterTweak: a => a.CheatBridge = events);
            Assert.AreEqual(AdDirector.FailedKindPreamble, d!.FailedStepKindName, d.Summary);
            StringAssert.StartsWith("ABORT tutorial-gate: ", d.FailedReason);
            Assert.IsNull(d.PreambleFailedStepIndex, "a setup command is not a step of the gate");
            Assert.IsTrue(d.PreambleRan, "a gate stop reported with the gate NOT run — the site refuses that as a contradiction");
            CollectionAssert.IsEmpty(Writes(events), "the gate's step or the shot ran after its setup could not be bound");
        }

        /// <summary>M5 — the facts echo the sha of the gate text that ARRIVED (null: none), always present.</summary>
        [Test]
        public void Gate_TheFactsEchoTheShaOfTheGateThatArrived()
        {
            var none = Facts();
            Assert.IsTrue(none.ContainsKey("gateSha256"), "absent reads, on the site, as a kit from before it");
            Assert.AreEqual(JTokenType.Null, none["gateSha256"]!.Type);
            var plan = ProbePlan.Prepare(_root, SentWithGate(CheatShot, GateShot));
            var g = ProbeFacts.Build("s", false, null, "tutorial-gate", "r", null, null, 1, 1, "aa", "bb",
                new[] { "x" }, new[] { "x" }, null, 1f, true, null, "9.9.9", gateRan: true, gateFailedStep: 0,
                gateSha256: plan.GateSha256);
            Assert.AreEqual(SyncNova.Sha256OfText(GateShot), g["gateSha256"]!.Value<string>());
            Assert.AreEqual(plan.ShotSha256, SyncNova.Sha256OfText(CheatShot), "control: the shot's sha is its own");
        }

        /// <summary>Survivor KA1 — the gate's RECOVERY runs before its setup, as its own try ran it; the shot's
        /// runs again before the shot's.</summary>
        [Test]
        public void Gate_ItsRecoveryRunsBeforeItsSetup_AndTheShotsBeforeTheShots()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro", "SkipTutorial");
            var plan = LoadedWithGate(CheatShot, GateShot);
            var events = new RecordingBridge();
            var recorder = new OrderedRecorder(events.Run_, Path.Combine(_root, "never"));
            var (d, _, _) = RunDirector(plan, tweak: o => o.Recorder = recorder, adapterTweak: a =>
            {
                a.CheatBridge = events;
                a.Recovery = new EventRecovery(events.Run_);
            });
            CollectionAssert.AreEqual(
                new[] { "RECOVER", "SkipIntro", "SkipTutorial", "RECOVER", "set Coins 5", "REC try-me", "SelectHero Knight" },
                Writes(events), d!.Summary);
        }

        /// <summary>Survivor KA2 — the gate's ARM wait: its first step waits for its arm condition (a
        /// tutorial that comes up late), as its own try waited.</summary>
        [Test]
        public void Gate_ItsFirstStepWaitsForItsArmCondition()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro", "SkipTutorial");
            var plan = LoadedWithGate(CheatShot, GateShot.Replace(@"""setup"": [""SkipIntro""],",
                @"""setup"": [""SkipIntro""], ""arm"": { ""kind"": ""present"", ""name"": ""TutorialUp"" }, ""armTimeoutSec"": 5,"));
            Assert.IsNotNull(plan.Gate!.ArmCondition, "precondition: the gate has an arm condition");
            var ui = new TimedUi();
            ui.AppearsAt["TutorialUp"] = 2.0;
            var seen = new List<string>();
            var events = new RecordingBridge();
            var (d, _, _) = RunDirector(plan, tweak: o => ui.Now = o.Now!, adapterTweak: a =>
            {
                a.Ui = ui;
                a.CheatBridge = new ObservingBridge(events, () => seen.Add(ui.Now().ToString("0.0") + (ui.Exists("TutorialUp") ? " SHOWN" : " ABSENT")));
            });
            var skip = events.Run_.IndexOf("SkipTutorial");
            Assert.GreaterOrEqual(skip, 0, d!.Summary);
            StringAssert.EndsWith(" SHOWN", seen[skip], "the gate's step ran before its arm condition held: " + string.Join(", ", seen));
            StringAssert.DoesNotContain("never held", d.Summary);
            Assert.IsTrue(d.AllCaptured, d.Summary);
        }

        /// <summary>Survivor KA3 — the gate's SETTLE wait: a tutorial that takes seconds to go away is waited
        /// for (up to its settle timeout), not read as a gate stop after the 0.8 s pause.</summary>
        [Test]
        public void Gate_ItsEndStateIsWaitedFor_NotReadOnce()
        {
            Tick("SelectHero Knight", "set Coins 5", "SkipIntro", "SkipTutorial");
            var plan = LoadedWithGate(CheatShot,
                GateShot.Replace(@"""name"": ""Board"" } }", @"""name"": ""GateDone"" }, ""settleTimeoutSec"": 10 }"));
            var ui = new TimedUi();
            ui.AppearsAt["GateDone"] = 4.0;
            var (d, _, _) = RunDirector(plan, tweak: o => ui.Now = o.Now!, adapterTweak: a => a.Ui = ui);
            Assert.IsNull(d!.FailedStepKindName, d.Summary);
            Assert.IsTrue(d.AllCaptured, d.Summary);
            // CONTROL: an end state that never comes within the timeout is still the gate's stop
            AdDirector.Active?.Finish("next run");
            var never = LoadedWithGate(CheatShot,
                GateShot.Replace(@"""name"": ""Board"" } }", @"""name"": ""GateDone"" }, ""settleTimeoutSec"": 2 }"));
            var late = new TimedUi();
            late.AppearsAt["GateDone"] = 4.0;
            var (n, _, _) = RunDirector(never, tweak: o => late.Now = o.Now!, adapterTweak: a => a.Ui = late);
            Assert.AreEqual(AdDirector.FailedKindPreamble, n!.FailedStepKindName, n.Summary);
        }

        /// <summary>Survivor KA4 (a security line) — the gate's SETUP goes through the lever gate and the
        /// camera-pose Type guard, never the raw bridge: past the pre-check (a caller that hands the director
        /// a gate itself), an un-ticked command and a pose of another Type never reach the game.</summary>
        [Test]
        public void Gate_ItsSetupGoesThroughTheLeverGateAndTheCameraGuard_NeverTheRawBridge()
        {
            OnDisk(_root, BoardRigAdapter);
            Tick("SelectHero Knight", "set Coins 5", PoseOtherType, BoardRigSpec, "SkipTutorial");
            var plan = Loaded(_root, CheatShot, BoardRigAdapter);
            var gate = LoadOne(@"{ ""name"": ""tutorial-gate"", ""setup"": [""SkipIntro"", """ + PoseOtherType + @"""],
  ""steps"": [ { ""kind"": ""cheat"", ""command"": ""SkipTutorial"" } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }");
            var (d, cheats, _) = RunDirector(plan, bypassPlan: true, tweak: o => o.Preamble = gate);
            CollectionAssert.DoesNotContain(cheats.Run_, "SkipIntro", "an un-ticked setup command of the gate reached the game");
            CollectionAssert.DoesNotContain(cheats.Run_, PoseOtherType, "the gate posed a Type the ticked camera-spec did not name");
            StringAssert.Contains(Levers.NotApprovedLog("SkipIntro"), d!.Summary);
            StringAssert.Contains(OtherTypeRefused, d.Summary);
            // CONTROL: the ticked step of the same gate reaches the game
            CollectionAssert.Contains(cheats.Run_, "SkipTutorial");
        }

        /// <summary>Survivor KA7 — the pre-check's camera-pose Type rule covers the GATE's commands too:
        /// refused whole, before any lever is asked for.</summary>
        [Test]
        public void Gate_Prepare_ACameraPoseOfAnotherTypeInTheGateIsRefusedUpFront()
        {
            OnDisk(_root, BoardRigAdapter);
            var gate = @"{ ""name"": ""tutorial-gate"", ""setup"": [""" + PoseOtherType + @"""],
  ""steps"": [ { ""kind"": ""cheat"", ""command"": ""SkipTutorial"" } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";
            var plan = ProbePlan.Prepare(_root, SentWithGate(CheatShot, gate, BoardRigAdapter));
            Assert.AreEqual(OtherTypeRefused, plan.Refusal);
            Assert.IsFalse(plan.MayRun);
            // CONTROL: the spec's own Type in the gate is not refused
            var ok = ProbePlan.Prepare(_root, SentWithGate(CheatShot, gate.Replace(PoseOtherType, PoseSpecsType), BoardRigAdapter));
            Assert.IsNull(ok.Refusal, ok.Refusal);
        }

        /// <summary>A bridge that runs the recording one and, before each command, notes what the screen showed.</summary>
        private sealed class ObservingBridge : ICheatBridge
        {
            private readonly RecordingBridge _inner;
            private readonly Action _observe;
            public ObservingBridge(RecordingBridge inner, Action observe) { _inner = inner; _observe = observe; }
            public bool Run(string command) { _observe(); return _inner.Run(command); }
        }
    }
}
