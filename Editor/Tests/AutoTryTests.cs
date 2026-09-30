using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// v3 P5 (§3.5 step 2, §3.6a item 6) — AUTO-TRY and THE RECIPE TRY, the kit half, over a fake screen: only the candidates
    /// the claim carried are pressed, through the guard; arrival by NEW authored names; the stop rule (arrived, the screen
    /// changed without arriving, nothing changed → next); the caps; $0 measured; the claims read whole; the progress file
    /// keeps them across a reload.
    /// </summary>
    public class AutoTryTests
    {
        private double _clock;

        [SetUp]
        public void SetUp() => _clock = 0;

        private static AutoTryRequest Request(string[] candidates, string[]? authored = null, string[]? dismiss = null, int tries = 3, double seconds = 300) =>
            AutoTryRequest.FromJson(new JObject
            {
                ["need"] = new JObject { ["kind"] = "screen", ["name"] = "Equipment" },
                ["candidates"] = new JArray(candidates),
                ["authoredNames"] = new JArray(authored ?? new[] { "EqGrid", "Equipment" }),
                ["dismiss"] = new JArray(dismiss ?? Array.Empty<string>()),
                ["start"] = null,
                ["caps"] = new JObject { ["tries"] = tries, ["seconds"] = seconds },
            })!;

        private AutoTryRun Run(FakeReplayScreen screen, AutoTryRequest request)
        {
            var run = new AutoTryRun(request);
            var ticks = 0;
            foreach (var _ in run.Run(screen, () => _clock, waitForBoot: true))
            {
                _clock += 0.1;
                Assert.Less(++ticks, 100_000);
            }
            Assert.IsTrue(run.Finished);
            return run;
        }

        private static FakeReplayScreen Lobby()
        {
            var s = new FakeReplayScreen();
            s.Roots.Add("Ui/Lobby");
            s.Add("Icon");            // a widget wired in code: the click lands, nothing opens
            s.Add("EquipBtn");
            s.Add("ShopBtn");
            s.OnClick["EquipBtn"] = () => { s.Roots.Add("Ui/Equipment"); s.Add("Equipment"); s.Add("EqGrid"); };
            s.OnClick["ShopBtn"] = () => { s.Roots.Add("Ui/Shop"); s.Add("ShopGrid"); };
            return s;
        }

        [Test]
        public void APressThatOpensNothing_MovesToTheNextCandidate_AndArrivalByNewNamesStopsIt()
        {
            var s = Lobby();
            var run = Run(s, Request(new[] { "Icon", "EquipBtn", "ShopBtn" }));
            Assert.IsTrue(run.Found, run.Stopped);
            CollectionAssert.AreEqual(new[] { "Icon", "EquipBtn" }, s.Clicked);
            Assert.AreEqual(2, run.Presses.Edges.List.Count);
            var facts = JObject.Parse(run.ToFactsJson("0.13.0", "abc", null, null));
            Assert.AreEqual(0, facts["visionCalls"]!.Value<int>(), "auto-try asks no screen check");
            Assert.IsTrue(facts["attempts"]![1]!["after"]!["added"]!.Values<string>().Contains("EqGrid"));
        }

        [Test]
        public void K4_OnAGameWhosePanelsShareOneWrapper_NewNamesWithoutArrival_StopTheRun()
        {
            // every panel lives under Canvas/SafeArea, so the ROOTS never change — the shop opens and the roots stay the same
            var s = new FakeReplayScreen();
            s.Roots.Add("Canvas/SafeArea");
            s.Add("ShopBtn");
            s.Add("EquipBtn");
            s.OnClick["ShopBtn"] = () => { s.Add("ShopGrid"); s.Add("BuyGems"); };
            var run = Run(s, Request(new[] { "ShopBtn", "EquipBtn" }));
            Assert.IsFalse(run.Found);
            CollectionAssert.AreEqual(new[] { "ShopBtn" }, s.Clicked, "never pressing on from a screen nobody named");
            StringAssert.Contains("cannot tell which screen it is", run.Stopped);
        }

        [Test]
        public void APressThatChangesTheScreenWithoutArriving_StopsTheRun_NothingMoreIsPressed()
        {
            var s = Lobby();
            var run = Run(s, Request(new[] { "ShopBtn", "EquipBtn" }));
            Assert.IsFalse(run.Found);
            CollectionAssert.AreEqual(new[] { "ShopBtn" }, s.Clicked);
            StringAssert.Contains("changed the screen without arriving", run.Stopped);
        }

        [Test]
        public void OnlyTheCandidatesArePressed_ARiskyOneIsRefusedByTheGuard_AndTheCapsHold()
        {
            var s = Lobby();
            s.Add("BuyEquipment");
            s.OnClick["BuyEquipment"] = () => s.Add("Equipment");
            var run = Run(s, Request(new[] { "BuyEquipment", "Icon" }, tries: 1));
            CollectionAssert.AreEqual(new[] { "Icon" }, s.Clicked, "a BUY button is never pressed, even as a candidate");
            StringAssert.Contains("PRESS REFUSED", run.Attempts[0]!["refused"]!.Value<string>());
            // the one try was spent on Icon; the loop ends at its cap
            var capped = Run(Lobby(), Request(new[] { "Icon", "EquipBtn" }, tries: 1));
            Assert.IsFalse(capped.Found);
            StringAssert.Contains("used its 1 tries", capped.Stopped);
        }

        [Test]
        public void ANameAlreadyOnScreenIsNotArrival()
        {
            var s = Lobby();
            s.Add("Equipment");
            s.Add("EqGrid");
            s.OnClick["EquipBtn"] = () => { };
            Assert.IsFalse(Run(s, Request(new[] { "EquipBtn" })).Found);
        }

        [Test]
        public void ThePopupBeforeAPress_IsClosedByTheLoop()
        {
            var s = Lobby();
            s.Popup("Ui/Daily", "DailyClose");
            var run = Run(s, Request(new[] { "EquipBtn" }, dismiss: new[] { "DailyClose" }));
            Assert.IsTrue(run.Found);
            CollectionAssert.AreEqual(new[] { "DailyClose", "EquipBtn" }, s.Clicked);
        }

        [Test]
        public void TheClaimIsReadWhole_AndTheKitsCapsBoundWhateverItSays()
        {
            Assert.IsNull(AutoTryRequest.FromJson(new JObject { ["need"] = new JObject { ["kind"] = "screen", ["name"] = "x" }, ["candidates"] = new JArray() }));
            Assert.IsNull(AutoTryRequest.FromJson(new JObject
            {
                ["need"] = new JObject { ["kind"] = "screen", ["name"] = "x" },
                ["candidates"] = new JArray(Enumerable.Range(0, 7).Select(i => "B" + i)),
                ["authoredNames"] = new JArray(), ["dismiss"] = new JArray(),
            }), "more candidates than the kit presses is refused");
            var big = Request(new[] { "A" }, tries: 99, seconds: 99999);
            Assert.AreEqual(AutoTryRequest.MaxTries, big.Tries);
            Assert.AreEqual(AutoTryRequest.MaxSeconds, big.Seconds);
            var back = AutoTryRequest.FromJson(big.ToJson())!;
            CollectionAssert.AreEqual(big.Candidates, back.Candidates);
        }

        [Test]
        public void TheProgressFile_KeepsTheAutoTryAndTheRecipeTry_AcrossAReload()
        {
            var job = CaptureJob.FromJson(JObject.Parse(@"{ ""runId"": ""r1"", ""kind"": ""auto-try"", ""items"": [] }"), out _)!;
            var p = CaptureProgress.Start(job);
            p.AutoTry = Request(new[] { "EquipBtn" });
            var doc = @"{""schemaVersion"":1,""gameId"":""g"",""updatedAt"":""t"",""recipes"":[]}";
            p.RecipeTry = new StartClaim(doc, SyncNova.Sha256OfText(doc), new[] { "r000000000000000a" }, Array.Empty<string>());
            var path = Path.Combine(Path.GetTempPath(), "autotry-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                p.Save(path);
                var back = CaptureProgress.Load(path)!;
                CollectionAssert.AreEqual(new[] { "EquipBtn" }, back.AutoTry!.Candidates);
                Assert.AreEqual(p.RecipeTry.Sha256, back.RecipeTry!.Sha256);
                Assert.IsTrue(CaptureJob.IsHandled(CaptureJob.AutoTryKind));
                Assert.IsTrue(CaptureJob.IsHandled(CaptureJob.RecipeTryKind));
                Assert.IsFalse(back.Done, "a kit job is not done until its result is posted");
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void TheRecipeTrysClaimAndSettings()
        {
            var doc = @"{""schemaVersion"":1,""gameId"":""g"",""updatedAt"":""t"",""recipes"":[]}";
            var claim = new StartClaim(doc, SyncNova.Sha256OfText(doc), new[] { "r000000000000000a" }, new[] { "Save.Gems" });
            var body = new JObject { ["recipeTry"] = new JObject { ["start"] = claim.ToJson() } }.ToString();
            Assert.AreEqual(claim.Sha256, RecipeTryJob.FromClaim(body)!.Sha256);
            Assert.IsNull(RecipeTryJob.FromClaim(@"{ ""recipeTry"": { ""start"": 5 } }"));
            var plan = StartPlan.Read(claim, "again");
            var o = RecipeTryJob.OptionsFor(Path.GetTempPath(), plan);
            Assert.IsTrue(o.CloudContent);
            Assert.IsTrue(o.RecordEdges);
            Assert.AreEqual(1, o.MaxAttempts);
            Assert.IsInstanceOf<NoRecordingDriver>(o.Recorder);
            var facts = JObject.Parse(RecipeTryJob.FactsJson("0.13.0", "abc", plan, null));
            Assert.AreEqual(plan.Sha256, facts["startSha256"]!.Value<string>());
            Assert.AreEqual(JTokenType.Null, facts["start"]!.Type);
        }
    }
}
