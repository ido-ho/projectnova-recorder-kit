using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// v3 P5 (§3.6, §3.7) — THE DECLARED START ON THE WIRE, the kit half: the start a Try or a capture carries is read
    /// whole (its sha, the ONE recipe reader, the ids it names, a real chain from the anchor) or refused by name, never
    /// beside a tutorial gate; a present-but-broken block is no script at all; the director is handed the chain from the
    /// checked plan; the facts echo what ARRIVED; a recording keeps its start, reset and edges across reloads and reports
    /// the edges once.
    /// </summary>
    public class StartClaimTests
    {
        private string _root = "";

        private const string PlainAdapter = @"{ ""gameId"": ""g"" }";
        private const string Shot = @"{ ""name"": ""try-me"", ""steps"": [ { ""kind"": ""click"", ""name"": ""Go"" } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";
        private const string Gate = @"{ ""name"": ""tutorial-gate"", ""steps"": [ { ""kind"": ""wait"", ""seconds"": 1 } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Board"" } }";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "start-claim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            File.WriteAllBytes(SyncNova.AdapterFile(_root), new UTF8Encoding(false).GetBytes(PlainAdapter));
        }

        [TearDown]
        public void TearDown()
        {
            AdDirector.Active?.Finish("test teardown");
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        private static JObject Recipe(string id, string? startsFrom, string click) => new()
        {
            ["id"] = id,
            ["ask"] = "show me " + id,
            ["learnedBy"] = "teach",
            ["runId"] = "run-" + id,
            ["taughtAt"] = "2026-09-27T10:00:00.000Z",
            ["declaredStart"] = startsFrom == null
                ? new JObject { ["kind"] = "lobby-after-boot" }
                : new JObject { ["kind"] = "recipe", ["recipeId"] = startsFrom },
            ["stamp"] = new JObject { ["gameCommit"] = null, ["kitVersion"] = "0.13.0" },
            ["steps"] = new JArray(new JObject { ["kind"] = "click", ["name"] = click }),
            ["dismiss"] = new JArray("Close"),
            ["arrival"] = new JArray("A1", "A2"),
            ["confirm"] = new JObject { ["state"] = "confirmed", ["why"] = null, ["replayRan"] = true },
            ["screens"] = new JArray(),
            ["thumbnailKey"] = null,
        };

        private const string A = "r000000000000000a";
        private const string B = "r000000000000000b";

        private static string Doc(params JObject[] recipes) => new JObject
        {
            ["schemaVersion"] = 1,
            ["gameId"] = "g",
            ["updatedAt"] = "t",
            ["recipes"] = new JArray(recipes.Cast<object>().ToArray()),
        }.ToString(Newtonsoft.Json.Formatting.None);

        private static StartClaim Claim(string doc, string[] ids, string? sha = null, string[]? watch = null) =>
            new(doc, sha ?? SyncNova.Sha256OfText(doc), ids, watch ?? new[] { "Save.Gems" });

        private static StartClaim GoodClaim() => Claim(Doc(Recipe(A, null, "Equip"), Recipe(B, A, "Sword")), new[] { A, B });

        [Test]
        public void AWholeStart_ReadsIntoTheChain_AnchorFirst()
        {
            var plan = StartPlan.Read(GoodClaim(), "again");
            Assert.IsNull(plan.Refusal, plan.Refusal);
            CollectionAssert.AreEqual(new[] { A, B }, plan.Chain!.Select(r => r.Id));
            CollectionAssert.AreEqual(new[] { "Save.Gems" }, plan.Watch);
            Assert.AreEqual(SyncNova.Sha256OfText(GoodClaim().Recipes), plan.Sha256);
        }

        [Test]
        public void AStartIsRefusedWhole_ByName_WhenItIsNotItsSha_DoesNotRead_NamesOtherRecipes_OrIsNotAChain()
        {
            var doc = Doc(Recipe(A, null, "Equip"), Recipe(B, A, "Sword"));
            StringAssert.Contains("is not the one the website sent", StartPlan.Read(Claim(doc, new[] { A, B }, sha: new string('0', 64)), "again").Refusal);
            StringAssert.Contains("does not read", StartPlan.Read(Claim("{\"schemaVersion\":9}", new[] { A }), "again").Refusal);
            StringAssert.Contains("not the ones it names", StartPlan.Read(Claim(doc, new[] { A }), "again").Refusal);
            // the right recipes, the wrong order: B does not start at the anchor
            var backwards = Doc(Recipe(B, A, "Sword"), Recipe(A, null, "Equip"));
            StringAssert.Contains("not a chain from", StartPlan.Read(Claim(backwards, new[] { B, A }), "again").Refusal);
            StringAssert.Contains("not the ones it names", StartPlan.Read(Claim(Doc(), new[] { A }), "again").Refusal);
        }

        [Test]
        public void TheClaimsStartBlock_AbsentOrNullIsNone_PresentButBrokenIsNoScriptAtAll_AndItSurvivesTheReload()
        {
            ProbeRequest? Parse(string start) =>
                ProbeRequest.FromJson(JObject.Parse(@"{ ""shot"": ""S"", ""adapter"": ""A"", ""shotSha256"": ""aa"", ""adapterSha256"": ""bb""" + start + " }"));
            Assert.IsNull(Parse("")!.Start);
            Assert.IsNull(Parse(@", ""start"": null")!.Start);
            Assert.IsNull(Parse(@", ""start"": { ""recipes"": ""x"" }"), "a start that did not arrive whole is no script");
            var good = Parse(", \"start\": " + GoodClaim().ToJson().ToString(Newtonsoft.Json.Formatting.None))!;
            Assert.AreEqual(GoodClaim().Sha256, good.Start!.Sha256);
            var back = ProbeRequest.FromJson(good.ToJson())!;
            Assert.AreEqual(good.Start.Recipes, back.Start!.Recipes);
            CollectionAssert.AreEqual(new[] { A, B }, back.Start.RecipeIds);
        }

        [Test]
        public void ATryWithADeclaredStart_PlaysTheCheckedChain_AndATryWithAGateAndAStartIsRefused()
        {
            var request = new ProbeRequest(Shot, PlainAdapter, SyncNova.Sha256OfText(Shot), SyncNova.Sha256OfText(PlainAdapter), start: GoodClaim());
            var plan = ProbePlan.Prepare(_root, request);
            Assert.IsNull(plan.Refusal, plan.Refusal);
            CollectionAssert.AreEqual(new[] { A, B }, plan.StartPath!.Select(r => r.Id));
            Assert.AreEqual(GoodClaim().Sha256, plan.StartSha256);
            var both = new ProbeRequest(Shot, PlainAdapter, SyncNova.Sha256OfText(Shot), SyncNova.Sha256OfText(PlainAdapter),
                new ProbeGate(Gate, SyncNova.Sha256OfText(Gate)), GoodClaim());
            Assert.AreEqual(StartPlan.BothRefused, ProbePlan.Prepare(_root, both).Refusal);
            var broken = new ProbeRequest(Shot, PlainAdapter, SyncNova.Sha256OfText(Shot), SyncNova.Sha256OfText(PlainAdapter),
                start: Claim(GoodClaim().Recipes, new[] { A, B }, sha: new string('1', 64)));
            StringAssert.Contains("declared start that arrived is not the one", ProbePlan.Prepare(_root, broken).Refusal);
        }

        [Test]
        public void TheTrysFacts_EchoTheStartThatArrived_AndCarryThePresses()
        {
            var presses = new PressLog();
            presses.Edges.Add("Ui/Lobby", "Equip", "Ui/Equipment|Ui/Lobby");
            presses.Flags.Add(new DismissFlag { Dismiss = "ClaimClose", Path = "Save.Gems", Before = "1", After = "2" });
            var facts = ProbeFacts.Build("try-me", true, null, null, null, null, null, 1, 1, "s", "a", null, null, null, 1f,
                false, "none", "0.13.0", startSha256: "abc", start: new JObject { ["arrived"] = true }, presses: presses);
            Assert.AreEqual("abc", facts["startSha256"]!.Value<string>());
            Assert.IsTrue(facts["start"]!["arrived"]!.Value<bool>());
            Assert.AreEqual("Equip", facts["edges"]![0]!["press"]!.Value<string>());
            Assert.AreEqual(0, facts["edgesDropped"]!.Value<int>());
            Assert.AreEqual("Save.Gems", facts["dismissFlags"]![0]!["path"]!.Value<string>());
            // a try with no start still SAYS so — every field present
            var plain = ProbeFacts.Build("try-me", true, null, null, null, null, null, 1, 1, "s", "a", null, null, null, 1f, false, "none", "0.13.0");
            Assert.AreEqual(JTokenType.Null, plain["startSha256"]!.Type);
            Assert.AreEqual(JTokenType.Null, plain["start"]!.Type);
            Assert.AreEqual(0, ((JArray)plain["edges"]!).Count);
        }

        [Test]
        public void ARecordingsStartAndReset_ArriveOnTheClaim_ABrokenOneIsSaid_AndAllOfItSurvivesTheReload()
        {
            var body = new JObject { ["job"] = new JObject(), ["start"] = GoodClaim().ToJson(), ["reset"] = new JObject { ["cheat"] = "Reset user" } }.ToString();
            var (start, reset) = CaptureStart.FromClaimBody(body, out var broken);
            Assert.IsNull(broken);
            Assert.AreEqual(GoodClaim().Sha256, start!.Sha256);
            Assert.AreEqual("Reset user", reset!.Cheat);
            var (none, _) = CaptureStart.FromClaimBody(@"{ ""start"": null }", out var noneBroken);
            Assert.IsNull(none);
            Assert.IsNull(noneBroken);
            CaptureStart.FromClaimBody(@"{ ""start"": { ""recipes"": 5 } }", out var isBroken);
            StringAssert.Contains("arrived incomplete", isBroken);

            var job = CaptureJob.FromJson(JObject.Parse(@"{ ""runId"": ""r1"", ""kind"": ""capture"", ""items"": [ { ""shot"": ""s"", ""take"": 1 } ] }"), out _)!;
            var p = CaptureProgress.Start(job);
            p.DeclaredStart = start;
            p.Reset = reset;
            p.PlayRestartPending = true;
            p.TakesSinceBoot = 2;
            p.BootRetries = 1;
            var log = new PressLog();
            log.Edges.Add("a", "Go", "b");
            p.AddEdges(log);
            var path = Path.Combine(_root, "progress.json");
            p.Save(path);
            var back = CaptureProgress.Load(path)!;
            Assert.AreEqual(GoodClaim().Sha256, back.DeclaredStart!.Sha256);
            Assert.AreEqual("Reset user", back.Reset!.Cheat);
            Assert.IsTrue(back.PlayRestartPending);
            Assert.AreEqual(2, back.TakesSinceBoot);
            Assert.AreEqual(1, back.BootRetries);
            Assert.AreEqual(1, back.Edges.Count);
            // the edges ride `done` once
            Assert.AreEqual("Go", JObject.Parse(back.DoneReportJson())["edges"]![0]!["press"]!.Value<string>());
            Assert.IsNull(JObject.Parse(CaptureProgress.Start(job).DoneReportJson())["edges"], "no presses, no field");
        }

        [Test]
        public void ARecordingsEdges_AreCappedAcrossTakes()
        {
            var job = CaptureJob.FromJson(JObject.Parse(@"{ ""runId"": ""r1"", ""kind"": ""capture"", ""items"": [ { ""shot"": ""s"", ""take"": 1 } ] }"), out _)!;
            var p = CaptureProgress.Start(job);
            for (var t = 0; t < 3; t++)
            {
                var log = new PressLog();
                for (var i = 0; i < 100; i++) log.Edges.Add("a", "P" + i, "b");
                p.AddEdges(log);
            }
            Assert.AreEqual(DirectorEdges.Max, p.Edges.Count);
            Assert.AreEqual(100, p.EdgesDropped);
        }

        [Test]
        public void ATakesDirectorSettings_CarryTheChainTheWatchTheResetAndTheForcedGate()
        {
            var plan = StartPlan.Read(GoodClaim(), "again");
            var o = CaptureStart.OptionsFor(_root, plan, "Reset user");
            CollectionAssert.AreEqual(new[] { A, B }, o.StartPath!.Select(r => r.Id));
            CollectionAssert.AreEqual(new[] { "Save.Gems" }, o.WatchGets);
            Assert.AreEqual("Reset user", o.ResetCheat);
            Assert.IsTrue(o.RecordEdges);
            Assert.IsTrue(o.CloudContent, "the start is cloud-delivered text: the lever gate is forced on");
            Assert.AreEqual(_root, o.ProjectRoot);
            Assert.IsNotNull(o.WrapCheats);
            Assert.IsNull(CaptureStart.OptionsFor(_root, plan, null).ResetCheat);
        }

        [Test]
        public void TheSettleWait_CountsFromTheLastPlayExit()
        {
            var exit = new ClockStamp(new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc), 100, "s");
            Assert.AreEqual(0, CaptureStart.SettleRemainingSec(null, exit, 30));
            Assert.AreEqual(20, CaptureStart.SettleRemainingSec(exit, exit.AddSeconds(10), 30), 1e-9);
            Assert.AreEqual(0, CaptureStart.SettleRemainingSec(exit, exit.AddSeconds(45), 30));
        }
    }
}
