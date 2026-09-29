using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ProjectNova.RecorderKit.Tests.CheatFixtures;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// v3 P3 — the <c>cheat-search</c> JOB at its seam: <see cref="CheatSearchRun.Run"/>, the coroutine the agent pumps,
    /// driven here with a fake clock on a real temp project, over the fixture consoles. What it must hold: the registry
    /// is read ONLY through the kit job's gate (the cloud flag on — so an undelivered project with no tick reads nothing),
    /// it reads until two reads agree, and the facts say what happened.
    /// </summary>
    public class CheatSearchJobTests
    {
        private static readonly Assembly Fixtures = typeof(Tripwire).Assembly;
        private string _root = "";
        private double _clock;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "p3job-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
            NovaFixtures.CustomConsole.FixtureDevActions.ClearForTests();
            IngameDebugConsole.DebugLogConsole.ClearForTests();
            _clock = 0;
        }

        [TearDown]
        public void TearDown()
        {
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private static bool OnlyTheDevActions(RegistryRef r) => r.Type == typeof(NovaFixtures.CustomConsole.FixtureDevActions);

        /// <summary>Pump the job's coroutine as the editor does, a frame at a time, the clock moving 0.5 s per frame;
        /// <paramref name="onFrame"/> runs between frames (a boot that registers late, a tick taken away).</summary>
        private CheatSearchRun Pump(Action<int>? onFrame = null)
        {
            var run = new CheatSearchRun();
            var frame = 0;
            foreach (var _ in CheatSearchRun.Run(run, _root, new[] { Fixtures }, () => _clock, OnlyTheDevActions))
            {
                _clock += 0.5;
                onFrame?.Invoke(frame++);
                Assert.Less(frame, 1000, "the search never ended");
            }
            return run;
        }

        [Test]
        public void AnUntickedRegistryReadOnAnUndeliveredProjectReadsNothingAndBecomesARowToTick()
        {
            // no synced.json, no shots.json: a project that has had no Send — the gate is live only because it is a kit job
            NovaFixtures.CustomConsole.FixtureDevBoot.Boot();
            Assert.IsFalse(Levers.GateActive(_root), "control: the files on disk alone would leave the gate OFF");

            var run = Pump();
            Assert.IsTrue(run.RegistryAsked);
            Assert.IsNotNull(run.RegistryRefusal);
            StringAssert.Contains(CheatRegistry.Lever, run.RegistryRefusal!);
            CollectionAssert.IsEmpty(run.Stable.Counts, "not one read of the running game");
            Assert.IsFalse(run.Findings().Any(f => f.Source == CheatFinding.RegistrySource));
            Assert.IsTrue(run.Findings().Any(f => f.Source == CheatFinding.TagScanSource),
                "the static search still ran: it needs no tick");
            CollectionAssert.Contains(LeverGateBridge.RefusedThisSession, CheatRegistry.Lever,
                "the refused read is a row the studio can tick in the Nova Capture window");
            Assert.AreEqual(run.RegistryRefusal, CheatSearchRun.RegistryRefusalNow(_root),
                "the pre-Play check asks the same gate and says the same sentence");
        }

        [Test]
        public void ATickedRegistryReadRunsUntilTwoReadsAgreeAndFindsTheLateNames()
        {
            Assert.IsNull(Levers.SetApproved(_root, CheatRegistry.Lever, true));
            Assert.IsNull(CheatSearchRun.RegistryRefusalNow(_root));
            NovaFixtures.CustomConsole.FixtureDevBoot.Boot();
            // the game registers one more cheat a moment after the first read (spike A: 40, then 63)
            var run = Pump(frame => { if (frame == 1) NovaFixtures.CustomConsole.FixtureDevBoot.LateBoot(); });

            Assert.IsNull(run.RegistryRefusal);
            Assert.IsTrue(run.Stable.Stable);
            CollectionAssert.AreEqual(new[] { 4, 5, 5 }, run.Stable.Counts);
            var late = run.Findings().Single(f => f.Source == CheatFinding.RegistrySource && f.Name == "Late cheat");
            Assert.AreEqual("call NovaFixtures.CustomConsole.FixtureDevActions.Invoke \"Late cheat\"", late.Command);
        }

        [Test]
        public void ATickTakenAwayMidSearchStopsTheNextRead()
        {
            Assert.IsNull(Levers.SetApproved(_root, CheatRegistry.Lever, true));
            NovaFixtures.CustomConsole.FixtureDevBoot.Boot();
            var run = Pump(frame =>
            {
                if (frame == 0) NovaFixtures.CustomConsole.FixtureDevBoot.LateBoot(); // keep it from agreeing at once
                if (frame == 1) Assert.IsNull(Levers.SetApproved(_root, CheatRegistry.Lever, false));
            });
            Assert.AreEqual(1, run.Stable.Counts.Count, "one read before the untick, none after");
            Assert.IsNotNull(run.RegistryRefusal);
        }

        [Test]
        public void TheFactsSayWhatWasReadAndWhatWasNot()
        {
            Assert.IsNull(Levers.SetApproved(_root, CheatRegistry.Lever, true));
            NovaFixtures.CustomConsole.FixtureDevBoot.Boot();
            var facts = JObject.Parse(Pump().ToFactsJson("9.9.9"));
            Assert.AreEqual("9.9.9", facts["kitVersion"]!.Value<string>());
            var registry = (JObject)facts["registry"]!;
            Assert.IsTrue(registry["asked"]!.Value<bool>());
            Assert.IsTrue(registry["read"]!.Value<bool>());
            Assert.IsTrue(registry["stable"]!.Value<bool>());
            Assert.AreEqual(JTokenType.Null, registry["refusal"]!.Type);
            var dev = ((JArray)registry["consoles"]!).Single(c => c["where"]!.Value<string>() == "NovaFixtures.CustomConsole.FixtureDevActions.Entries");
            Assert.AreEqual("custom", dev["console"]!.Value<string>());
            Assert.AreEqual(4, ((JArray)dev["names"]!).Count);
            var unread = ((JArray)registry["consoles"]!).First(c => (c["where"]!.Value<string>() ?? "").Contains("FixtureCommandTable"));
            Assert.AreEqual(JTokenType.Null, unread["names"]!.Type, "a console that was not read says null, never an empty list");
            var findings = (JArray)facts["findings"]!;
            Assert.IsTrue(findings.Any(f => f["source"]!.Value<string>() == "registry" && f["name"]!.Value<string>() == "Skip 5 Levels"));
            Assert.IsTrue(findings.Any(f => f["source"]!.Value<string>() == "debug-method"));
            Assert.AreEqual(0, facts["findingsCut"]!.Value<int>());
        }

        [Test]
        public void AStaticOnlySearchSaysTheRefusalAndReadNothing()
        {
            var refusal = CheatSearchRun.RegistryRefusalNow(_root)!;
            var facts = JObject.Parse(CheatSearchRun.StaticOnly(new[] { Fixtures }, refusal, null).ToFactsJson("1.0.0"));
            Assert.IsFalse(facts["registry"]!["read"]!.Value<bool>());
            Assert.AreEqual(refusal, facts["registry"]!["refusal"]!.Value<string>());
            CollectionAssert.IsEmpty((JArray)facts["registry"]!["counts"]!);
        }

        // ---- the wire: the kind, the identity rule, the persisted facts ----------------------------------------------

        [Test]
        public void TheKindIsHandledAndIsAdmittedByTheBindingNotByAnAdapterJsonThatMayNotExistYet()
        {
            Assert.IsTrue(CaptureJob.IsHandled(CaptureJob.CheatSearchKind));
            Assert.AreEqual("cheat-search", CaptureJob.CheatSearchKind);
            Assert.IsNull(JobGuard.RefusalReason(CaptureJob.CheatSearchKind, "some-game", null),
                "run before the first Send: no adapter.json identity to compare");
            Assert.IsNotNull(JobGuard.RefusalReason(CaptureJob.ProbeKind, "some-game", null),
                "control: a try is still refused on identity");
        }

        [Test]
        public void GatheredFactsSurviveTheProgressFileSoARetryPostsAndNeverReadsAgain()
        {
            var claim = new JObject
            {
                ["job"] = new JObject { ["runId"] = "run-cs", ["workspaceId"] = "ws12345678", ["kind"] = "cheat-search", ["items"] = new JArray(), ["leaseSec"] = 300 },
                ["leaseUntil"] = "2026-09-26T00:00:00Z",
            }.ToString();
            var job = CaptureJob.ParseClaim(claim, out var error);
            Assert.IsNull(error);
            Assert.AreEqual(CaptureJob.CheatSearchKind, job!.Kind);
            var path = Path.Combine(_root, "progress.json");
            var progress = CaptureProgress.Start(job);
            progress.KitJobFactsJson = "{\"kitVersion\":\"x\"}";
            progress.Save(path);
            var back = CaptureProgress.Load(path)!;
            Assert.AreEqual(CaptureJob.CheatSearchKind, back.Kind);
            Assert.AreEqual("{\"kitVersion\":\"x\"}", back.KitJobFactsJson);
            Assert.IsFalse(back.Done, "facts gathered is not done: the result is still owed");
        }
    }
}
