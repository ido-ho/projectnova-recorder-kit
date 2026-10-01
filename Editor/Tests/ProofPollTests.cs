using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace NovaFixtures.PollGame
{
    /// <summary>Shaped like Rogue Legend's player data (its real field names): a login clock, hashed LiveOps entries,
    /// a currency dictionary, a counter and a list of levels.</summary>
    public sealed class PgStamp { public long Seconds; public int Nanoseconds; }
    public sealed class PgCurrency { public int Amount; }
    public sealed class PgLevel { public bool DidClear; public int BestRecord; }
    public sealed class PgUser { public int MatchesPlayed = 1; public PgStamp LastLoginTime = new(); }
    public sealed class PgData
    {
        public static PgData? Data;
        public PgUser User = new();
        public Dictionary<string, PgCurrency> Currencies = new();
        /// <summary>keyed by what look like ids — the kit sends the key as its salted short hash</summary>
        public Dictionary<string, int> LiveOps = new();
        public List<PgLevel> Levels = new();

        public static void Reset()
        {
            var d = new PgData();
            d.Currencies["topaz"] = new PgCurrency { Amount = 3500 };
            d.LiveOps["0123456789abcdef0123456789abcdef"] = 1;
            for (var i = 0; i < 3; i++) d.Levels.Add(new PgLevel());
            d.User.LastLoginTime.Seconds = 1790000000;
            Data = d;
        }

        /// <summary>What ticks on every frame whatever a cheat did: the clock, and a LiveOps counter.</summary>
        public static void Noise()
        {
            Data!.User.LastLoginTime.Seconds++;
            Data.User.LastLoginTime.Nanoseconds += 7;
            Data.LiveOps["0123456789abcdef0123456789abcdef"]++;
        }
    }

    public static class PgCheats
    {
        public static int Calls;
        public static bool Pending;
        /// <summary>"Give me 10k topaz pls": the server answers later and SETS topaz to 10000.</summary>
        public static void GiveTopazLater() { Calls++; Pending = true; }
        public static void Deliver() { if (Pending) { Pending = false; PgData.Data!.Currencies["topaz"].Amount = 10000; } }
        /// <summary>"Skip 1 Level": at once, locally.</summary>
        public static void SkipLevel()
        {
            Calls++;
            var d = PgData.Data!;
            d.User.MatchesPlayed++;
            var next = d.Levels.First(l => !l.DidClear);
            next.DidClear = true;
            next.BestRecord = 30;
        }
        public static void DoNothing() => Calls++;
        /// <summary>"Reset user": a local value at once, the server's answer later.</summary>
        public static void ResetUserLater() { Calls++; Pending = true; PgData.Data!.User.MatchesPlayed = 0; }
        public static void DeliverReset() { if (Pending) { Pending = false; PgData.Data!.Currencies["topaz"].Amount = 0; } }
        /// <summary>A cheat that ADDS a dictionary entry the data did not hold.</summary>
        public static void AddRuby() { Calls++; PgData.Data!.Currencies["ruby"] = new PgCurrency { Amount = 500 }; }
    }
}

namespace ProjectNova.RecorderKit.Tests
{
    using NovaFixtures.PollGame;

    /// <summary>
    /// Kit 0.13.4 — THE PROOF POLLS (Rogue Legend 2026-09-27: "Give me 10k topaz pls" and "Reset user" answered from the
    /// game's server after the kit's single after-read, so a working cheat read as "nothing changed"). After the cheat
    /// the kit reads again every <see cref="CheatProofRun.PollEverySec"/> until the check is met, or the settle wait runs
    /// out — a PLAIN snapshot always waits it out (the fresh audit: a local change must not end the wait before the
    /// server's half) — and the facts say how long it actually waited. A watched entry the cheat ADDS is graded, and
    /// <c>becomes</c> compares by the shared <c>proof-becomes.cases.json</c> (run by the website too).
    /// </summary>
    public class ProofPollTests
    {
        private const string Root = "NovaFixtures.PollGame.PgData.Data";
        private const string Topaz = Root + ".Currencies[topaz].Amount";
        private const string Give = "call NovaFixtures.PollGame.PgCheats.GiveTopazLater";
        private const string Skip = "call NovaFixtures.PollGame.PgCheats.SkipLevel";
        private const string Nothing = "call NovaFixtures.PollGame.PgCheats.DoNothing";
        private const string Reset = "call NovaFixtures.PollGame.PgCheats.ResetUserLater";
        private const string AddRuby = "call NovaFixtures.PollGame.PgCheats.AddRuby";
        private const string Ruby = Root + ".Currencies[ruby].Amount";
        private string _root = "";
        private double _clock;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "proofpoll-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
            PgData.Reset();
            PgCheats.Calls = 0;
            PgCheats.Pending = false;
            _clock = 0;
        }

        [TearDown]
        public void TearDown()
        {
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private void Tick(params string[] levers)
        {
            foreach (var l in levers) Assert.IsNull(Levers.SetApproved(_root, l, true));
            Assert.IsNull(Levers.SetNonProduction(_root, true));
        }

        /// <summary>Pumped at 0.25 s a frame. <paramref name="onSecond"/> runs with the seconds since the cheat ran; the
        /// game's noise ticks every frame after it.</summary>
        private CheatProofRun Pump(string command, JObject check, double settle, Action<double>? onSecond = null)
        {
            var request = CheatProofRequest.FromJson(new JObject { ["command"] = command, ["check"] = check, ["settleSec"] = settle })!;
            var run = new CheatProofRun(request);
            double? ranAt = null;
            var frame = 0;
            foreach (var _ in CheatProofRun.Run(run, _root, new GenericCheatBridge(_root, new UguiDriver()), () => _clock))
            {
                _clock += 0.25;
                if (PgCheats.Calls > 0)
                {
                    ranAt ??= _clock;
                    PgData.Noise();
                    onSecond?.Invoke(_clock - ranAt.Value);
                }
                Assert.Less(frame++, 2000);
            }
            return run;
        }

        private const string Relative = "Editor/Tests/Fixtures/proof-becomes.cases.json";

        private static string? FixturePath()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Levers).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
            {
                var resolved = Path.Combine(info.resolvedPath, Relative);
                if (File.Exists(resolved)) return resolved;
            }
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                var guess = Path.Combine(dir.FullName, "com.projectnova.recorder-kit", Relative);
                if (File.Exists(guess)) return guess;
            }
            return null;
        }

        private static JObject Snapshot() => new() { ["snapshot"] = Root };
        private static JObject Watch(string path, string expect, double? value = null)
        {
            var o = new JObject { ["snapshot"] = Root, ["watch"] = path, ["expect"] = expect };
            if (value != null) o["value"] = value.Value;
            return o;
        }

        [Test]
        public void TheSharedBecomesFixtureIsThisKitsSameValue()
        {
            var path = FixturePath();
            Assert.IsNotNull(path, "the shared proof-becomes fixture was not found");
            var root = NovaJson.ParseObject(File.ReadAllText(path!));
            Assert.AreEqual(CheatCheck.RelTolerance, root["relTolerance"]!.Value<double>());
            var cases = ((JArray)root["cases"]!).Cast<JObject>().ToList();
            Assert.GreaterOrEqual(cases.Count(c => !c["same"]!.Value<bool>()), 3, "the controls shrank");
            var failures = new List<string>();
            foreach (var c in cases)
            {
                double a = c["a"]!.Value<double>(), b = c["b"]!.Value<double>();
                if (CheatCheck.SameValue(a, b) != c["same"]!.Value<bool>() || CheatCheck.SameValue(b, a) != c["same"]!.Value<bool>())
                    failures.Add($"{a} vs {b}: the fixture says {c["same"]}");
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        [Test]
        public void AServerAnswerAfterTheOldThreeSecondsIsSeenAndTheWaitStopsOnceTheWatchedValueMoved()
        {
            Tick(Give, CheatCheck.SnapshotLever(Root));
            var run = Pump(Give, Watch(Topaz, "increase"), settle: 12, onSecond: s => { if (s >= 4.5) PgCheats.Deliver(); });
            Assert.IsTrue(run.Ran, run.LeverRefused);
            Assert.AreEqual(10000, run.After!.Watched);
            Assert.AreEqual("moved", run.StoppedBy);
            Assert.That(run.SettledSec, Is.InRange(4.5, 6.5), "stopped at the first read that saw it, not at 12 s");
            Assert.GreaterOrEqual(run.Reads, 4);
            var facts = JObject.Parse(run.ToFactsJson("0.13.4"));
            Assert.AreEqual(run.Reads, facts["reads"]!.Value<int>());
            Assert.AreEqual("moved", facts["stoppedBy"]!.Value<string>());
            Assert.AreEqual(Math.Round(run.SettledSec, 2), facts["settleSec"]!.Value<double>(), "the time it actually waited");
        }

        [Test]
        public void APlainSnapshotNeverStopsEarly_ItWaitsTheWholeTimeThenReadsOnce()
        {
            // a plain snapshot is a one-off DISCOVERY run: completeness beats speed. The audit's case — "Reset user": a
            // local value changes at once, the server's answer lands at 4 s. A rule that stopped once any change held
            // would stop at ~2 s and the proposals would miss the server's half
            Tick(Reset, CheatCheck.SnapshotLever(Root));
            var run = Pump(Reset, Snapshot(), settle: 6, onSecond: s => { if (s >= 4) PgCheats.DeliverReset(); });
            Assert.IsTrue(run.Ran, run.LeverRefused);
            Assert.AreEqual("timeout", run.StoppedBy);
            Assert.GreaterOrEqual(run.SettledSec, 6);
            var facts = JObject.Parse(run.ToFactsJson("0.13.4"));
            Assert.AreEqual("timeout", facts["stoppedBy"]!.Value<string>());
            var changed = ((JArray)facts["changed"]!).Select(c => c["path"]!.Value<string>()!).ToList();
            CollectionAssert.Contains(changed, Root + ".User.MatchesPlayed", "the local half");
            CollectionAssert.Contains(changed, Topaz, "the server's half, at 4 s");
        }

        [Test]
        public void ALocalCheatsPlainSnapshotAlsoWaitsItOut_AndHoldsTheCounterAndTheLevelEntry()
        {
            Tick(Skip, CheatCheck.SnapshotLever(Root));
            var run = Pump(Skip, Snapshot(), settle: 3);
            Assert.AreEqual("timeout", run.StoppedBy);
            Assert.GreaterOrEqual(run.SettledSec, 3);
            var changed = ((JArray)JObject.Parse(run.ToFactsJson("0.13.4"))["changed"]!).Select(c => c["path"]!.Value<string>()!).ToList();
            CollectionAssert.IsSupersetOf(changed, new[] { Root + ".User.MatchesPlayed", Root + ".Levels[0].DidClear", Root + ".Levels[0].BestRecord" });
        }

        [Test]
        public void AWatchedEntryTheCheatADDSIsMovedWhenItsExpectationHolds()
        {
            // the audit's case: the entry did not exist before the cheat — absent is a state, not a failed read
            Tick(AddRuby, CheatCheck.SnapshotLever(Root));
            var run = Pump(AddRuby, Watch(Ruby, "increase"), settle: 12);
            Assert.IsTrue(run.Ran, run.LeverRefused + " | " + run.Before?.Error);
            Assert.IsTrue(run.Before!.WatchedAbsent);
            Assert.AreEqual("moved", run.StoppedBy);
            Assert.AreEqual(500, run.After!.Watched);
            var w = JObject.Parse(run.ToFactsJson("0.13.4"))["watched"]!;
            Assert.AreEqual(JTokenType.Null, w["before"]!.Type);
            Assert.IsTrue(w["absentBefore"]!.Value<bool>());
            Assert.AreEqual(500, w["after"]!.Value<double>());
            // becomes: moved when the new entry IS the value
            PgData.Reset();
            PgCheats.Calls = 0;
            Assert.AreEqual("moved", Pump(AddRuby, Watch(Ruby, "becomes", 500), settle: 12).StoppedBy);
            // an expectation the new entry does not meet waits it out
            PgData.Reset();
            PgCheats.Calls = 0;
            Assert.AreEqual("timeout", Pump(AddRuby, Watch(Ruby, "decrease"), settle: 2).StoppedBy);
            PgData.Reset();
            PgCheats.Calls = 0;
            Assert.AreEqual("timeout", Pump(AddRuby, Watch(Ruby, "becomes", 7), settle: 2).StoppedBy);
            // a path that is BROKEN (no such field) is still no state: the proof stops before the cheat
            PgData.Reset();
            PgCheats.Calls = 0;
            var broken = Pump(AddRuby, Watch(Root + ".Nope.Amount", "increase"), settle: 2);
            Assert.IsFalse(broken.Ran);
            Assert.AreEqual(0, PgCheats.Calls);
        }

        [Test]
        public void ABecomesCheckStopsWhenTheValueIsTheTarget_AtOnceWhenItAlreadyIs()
        {
            var check = CheatCheck.FromJson(Watch(Topaz, "becomes", 10000), out var why);
            Assert.IsNotNull(check, why);
            Assert.AreEqual(JTokenType.Integer, check!.ToJson()["value"]!.Type, "a whole value is echoed as the site sent it (10000, not 10000.0)");
            StringAssert.Contains("should become 10000", check.Describe());
            Tick(Give, CheatCheck.SnapshotLever(Root));
            var run = Pump(Give, Watch(Topaz, "becomes", 10000), settle: 12, onSecond: s => { if (s >= 2.5) PgCheats.Deliver(); });
            Assert.AreEqual("moved", run.StoppedBy);
            Assert.AreEqual(10000, run.After!.Watched);
            Assert.That(run.SettledSec, Is.InRange(2.5, 4.5));
            // the second run: topaz is already 10000 — the first read sees the target and the wait ends (the website then
            // grades it NOT yet: a value already at the target proves nothing, invariant 177 — waiting longer would not help)
            PgCheats.Calls = 0;
            var again = Pump(Give, Watch(Topaz, "becomes", 10000), settle: 12, onSecond: _ => PgCheats.Deliver());
            Assert.AreEqual("moved", again.StoppedBy);
            Assert.That(again.SettledSec, Is.LessThanOrEqualTo(1.5));
            // …while an `increase` on the same value waits it out: it cannot go up again
            PgCheats.Calls = 0;
            var up = Pump(Give, Watch(Topaz, "increase"), settle: 3, onSecond: _ => PgCheats.Deliver());
            Assert.AreEqual("timeout", up.StoppedBy);
            Assert.AreEqual(10000, up.Before!.Watched);
            Assert.AreEqual(10000, up.After!.Watched);
        }

        [Test]
        public void AGetCheckPollsToo_AndASettleOfZeroReadsOnceAtOnce()
        {
            const string gold = "NovaFixtures.PollGame.PgData.Data.User.MatchesPlayed";
            Tick(Skip, CheatCheck.GetLever(gold));
            var run = Pump(Skip, new JObject { ["get"] = gold, ["expect"] = "increase" }, settle: 12);
            Assert.AreEqual("moved", run.StoppedBy);
            Assert.AreEqual(2, run.After!.Number);
            Assert.That(run.SettledSec, Is.LessThanOrEqualTo(1.5));
            PgData.Reset();
            PgCheats.Calls = 0;
            var zero = Pump(Skip, new JObject { ["get"] = gold, ["expect"] = "increase" }, settle: 0);
            Assert.AreEqual(1, zero.Reads);
            Assert.AreEqual(2, zero.After!.Number);
        }

        [Test]
        public void ATickTakenAwayMidWaitStopsThePollAndSaysTheReadWasRefused()
        {
            Tick(Give, CheatCheck.SnapshotLever(Root));
            var run = Pump(Give, Watch(Topaz, "increase"), settle: 12, onSecond: s =>
            {
                if (s >= 1.5) Levers.SetApproved(_root, CheatCheck.SnapshotLever(Root), false);
            });
            Assert.AreEqual("refused", run.StoppedBy);
            Assert.IsNotNull(run.After!.Error);
            Assert.Less(run.SettledSec, 12);
        }

        [Test]
        public void TheDefaultWaitIsTwelveSecondsLikeTheWebsites()
        {
            var r = CheatProofRequest.FromJson(new JObject { ["command"] = Give, ["check"] = Snapshot() })!;
            Assert.AreEqual(12, r.SettleSec);
            Assert.AreEqual(1.0, CheatProofRun.PollEverySec);
        }
    }
}
