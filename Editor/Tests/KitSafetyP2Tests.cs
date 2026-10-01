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
    /// Learn-and-drive v3 P2 — kit safety, the rest: the per-project non-production tick (§3.1), the tick-list input
    /// (§3.3 step 1), and exact membership at the cheat bridge (§2.6, §4), press verbs included. Driven through the REAL
    /// gate (<see cref="LeverGateBridge.Run"/>) on a real temp project, with the gate forced on as a kit job forces it.
    /// </summary>
    public class KitSafetyP2Tests
    {
        private string _root = "";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "p2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
        }

        [TearDown]
        public void TearDown()
        {
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private sealed class RecordingBridge : ICheatBridge
        {
            public readonly List<string> Ran = new();
            public bool Run(string command) { Ran.Add(command); return true; }
        }

        /// <summary>The gate as a kit job builds it: cloud content, so live whatever the disk says.</summary>
        private (LeverGateBridge gate, RecordingBridge inner, List<string> log) Gate()
        {
            var inner = new RecordingBridge();
            var log = new List<string>();
            return (new LeverGateBridge(inner, _root, log.Add, cloudContent: true), inner, log);
        }

        private void Tick(string command) => Assert.IsNull(Levers.SetApproved(_root, command, true), "the tick was written");

        private void WriteTickList(string text) =>
            File.WriteAllBytes(TickList.FilePath(_root), new UTF8Encoding(false).GetBytes(text));

        // ---- 1. the non-production tick ---------------------------------------------------------------------------

        [Test]
        public void ARiskyTickedCheatIsRefusedBeforeTheNonProductionTickAndRunsAfterIt()
        {
            Tick("raw ResetUser");
            var (gate, inner, log) = Gate();

            Assert.IsFalse(gate.Run("raw ResetUser"), "ticked, but risky, and the project is not ticked non-production");
            CollectionAssert.IsEmpty(inner.Ran, "the risky cheat reached the game");
            CollectionAssert.Contains(log, CheatRisk.NotYetLog("raw ResetUser", "the word root 'reset'"));

            Assert.IsNull(Levers.SetNonProduction(_root, true));
            Assert.IsTrue(gate.Run("raw ResetUser"), "the person said this editor talks to a non-production server");
            CollectionAssert.AreEqual(new[] { "raw ResetUser" }, inner.Ran);

            // …and unticking it holds the cheat back again, on the very next command
            Assert.IsNull(Levers.SetNonProduction(_root, false));
            Assert.IsFalse(gate.Run("raw ResetUser"));
            Assert.AreEqual(1, inner.Ran.Count);
        }

        [Test]
        public void AQuietTickedCheatRunsWithoutTheNonProductionTick()
        {
            // CONTROL for the test above: the refusal is the RISK, not the non-production tick in general
            Tick("raw SkipLevels 10");
            var (gate, inner, _) = Gate();
            Assert.IsTrue(gate.Run("raw SkipLevels 10"));
            CollectionAssert.AreEqual(new[] { "raw SkipLevels 10" }, inner.Ran);
        }

        [TestCase("call DevConsoleActions.Invoke \"Give me 10k topaz\"")]
        [TestCase("raw GrantAll")]
        [TestCase("raw UseDevServer")]
        [TestCase("raw DeleteSave")]
        [TestCase("raw BuyGems")]
        [TestCase("set Player.accountLevel 1")]
        public void EveryRiskyWordIsHeldBack(string command)
        {
            Tick(command);
            var (gate, inner, _) = Gate();
            Assert.IsFalse(gate.Run(command));
            CollectionAssert.IsEmpty(inner.Ran);
        }

        [Test]
        public void TheLocalGateIsStillOpen_TheRiskRuleIsAGateRuleNotANewOne()
        {
            // A project the cloud never delivered to, run by a person (no cloud flag): ungated as before P2.
            var inner = new RecordingBridge();
            Assert.IsTrue(new LeverGateBridge(inner, _root, null).Run("raw ResetUser"));
        }

        [Test]
        public void EachWriterKeepsTheOther_ATickKeepsNonProductionAndNonProductionKeepsTheTicks()
        {
            Assert.IsNull(Levers.SetNonProduction(_root, true));
            Tick("raw A");
            Assert.IsTrue(Levers.NonProductionTicked(_root), "ticking a lever dropped the non-production tick");
            Assert.IsNull(Levers.SetNonProduction(_root, false));
            CollectionAssert.AreEqual(new[] { "raw A" }, Levers.Approved(_root), "the non-production writer dropped a tick");
            Assert.IsNull(Levers.SetNonProduction(_root, true));
            Assert.IsNull(Levers.SetApproved(_root, "raw A", false));
            Assert.IsTrue(Levers.NonProductionTicked(_root), "unticking a lever dropped the non-production tick");
        }

        [Test]
        public void ANonProductionFieldThatIsNotABooleanMakesTheWholeFileUnreadable_FailClosed()
        {
            File.WriteAllText(Levers.FilePath(_root),
                "{ \"$schemaVersion\": 1, \"approved\": [\"raw ResetUser\"], \"nonProductionServer\": \"yes\" }");
            Assert.IsFalse(Levers.NonProductionTicked(_root));
            CollectionAssert.IsEmpty(Levers.Approved(_root), "a half-understood file approves nothing");
            Assert.IsNotNull(Levers.SetNonProduction(_root, true), "a file this kit cannot read is not written over");
            var (gate, inner, _) = Gate();
            Assert.IsFalse(gate.Run("raw ResetUser"));
            CollectionAssert.IsEmpty(inner.Ran);
        }

        [Test]
        public void HintsAreShownAndNeverActedOn()
        {
            var hints = NonProductionHints.FromDefines(new[] { "DEV_BUILD", "USE_STAGING_SERVER", "RELEASE" }, developmentBuild: true);
            Assert.That(hints, Has.Some.Contains("DEV_BUILD"));
            Assert.That(hints, Has.Some.Contains("USE_STAGING_SERVER"));
            Assert.IsTrue(hints.Any(h => h.Contains("'RELEASE'") && h.Contains("PRODUCTION")), string.Join("\n", hints));
            Assert.IsTrue(hints.Any(h => h.Contains("Development Build")));
            Assert.IsFalse(hints.Any(h => h.Contains("no RELEASE / PROD")), "RELEASE is set");
            Assert.IsTrue(NonProductionHints.FromDefines(Array.Empty<string>(), false).Any(h => h.Contains("no RELEASE / PROD")));

            // every hint says "development", and the tick is still off: a risky cheat is still refused
            Tick("raw ResetUser");
            var (gate, inner, _) = Gate();
            Assert.IsFalse(gate.Run("raw ResetUser"), "a hint must never stand in for the person's tick");
            CollectionAssert.IsEmpty(inner.Ran);
        }

        [Test]
        public void ATryIsRefusedWholeWhenItsTickedLeverIsRiskyAndTheProjectIsNotTickedNonProduction()
        {
            Tick("raw ResetUser");
            var risky = Levers.FirstRiskyNotYet(new[] { "raw SkipLevels 10", "raw ResetUser" }, _root);
            Assert.IsNotNull(risky);
            Assert.AreEqual("raw ResetUser", risky!.Value.Lever);
            Levers.SetNonProduction(_root, true);
            Assert.IsNull(Levers.FirstRiskyNotYet(new[] { "raw ResetUser" }, _root));
            // levers that never reach the bridge are never held back by a word in them
            Levers.SetNonProduction(_root, false);
            Assert.IsNull(Levers.FirstRiskyNotYet(new[] { Levers.HideOverlayLever("PurchasePopup"), Levers.TimeScaleLever }, _root));
        }

        // ---- 2. the tick-list input -------------------------------------------------------------------------------

        private const string TwoCandidates =
            "{ \"$schemaVersion\": 1, \"candidates\": [ " +
            "{ \"command\": \"raw SkipLevels 10\", \"kind\": \"level\" }, " +
            "{ \"command\": \"raw TopUp 10000\", \"kind\": \"give\", \"risk\": [\"server\"], \"source\": \"code-line\" } ] }";

        [Test]
        public void ADeliveredCandidateFileNeverCreatesATick()
        {
            WriteTickList(TwoCandidates);
            CollectionAssert.IsEmpty(Levers.Approved(_root), "a candidate became a tick");
            Assert.IsFalse(File.Exists(Levers.FilePath(_root)), "reading the tick list wrote levers.json");
            var (gate, inner, log) = Gate();
            Assert.IsFalse(gate.Run("raw SkipLevels 10"), "a proposed command ran with no tick");
            Assert.IsFalse(gate.Run("raw TopUp 10000"));
            CollectionAssert.IsEmpty(inner.Ran);
            CollectionAssert.Contains(log, Levers.NotApprovedLog("raw SkipLevels 10"));
        }

        [Test]
        public void TheWindowListsEachCandidateUnticked_WithItsLabels_AndARiskyOneSaysSo()
        {
            WriteTickList(TwoCandidates);
            var rows = Levers.Rows(_root, null);
            var skip = rows.Single(r => r.Command == "raw SkipLevels 10");
            var top = rows.Single(r => r.Command == "raw TopUp 10000");
            Assert.AreEqual(Levers.LeverSource.Candidate, skip.Source);
            Assert.IsFalse(skip.Approved);
            Assert.IsTrue(skip.CanTick, "a person must be able to tick a candidate");
            StringAssert.Contains("proposed by the website (kind: level)", skip.Note);
            Assert.IsNull(skip.RiskyNotYet);
            Assert.IsFalse(top.Approved);
            StringAssert.Contains("kind: give; risk: server", top.Note);
            Assert.AreEqual("the website's kind 'give'", top.RiskyNotYet, "the file RAISED the risk of a quiet-sounding command");
            StringAssert.Contains("RISKY", top.Note);

            // the PERSON ticks it — through the window's one writer — and only then is it approved, and still risky
            Tick("raw TopUp 10000");
            var after = Levers.Rows(_root, null).Single(r => r.Command == "raw TopUp 10000");
            Assert.IsTrue(after.Approved);
            Assert.IsNotNull(after.RiskyNotYet);
            var (gate, inner, _) = Gate();
            Assert.IsFalse(gate.Run("raw TopUp 10000"), "the file's 'give' holds it back until non-production");
            Levers.SetNonProduction(_root, true);
            Assert.IsNull(Levers.Rows(_root, null).Single(r => r.Command == "raw TopUp 10000").RiskyNotYet,
                "the rows are computed again when the non-production tick changes (it is in levers.json's sha)");
            Assert.IsTrue(gate.Run("raw TopUp 10000"));
            CollectionAssert.AreEqual(new[] { "raw TopUp 10000" }, inner.Ran);
        }

        [Test]
        public void TheGateReadsTheTickListOncePerAttempt_NotOncePerWrite()
        {
            // the thirteenth audit's class: a per-write file read over a long delivered setup list froze the editor
            WriteTickList(TwoCandidates);
            var commands = Enumerable.Range(0, 40).Select(i => "raw SkipLevels " + i).ToList();
            foreach (var c in commands) Tick(c);
            var (gate, inner, _) = Gate();
            var before = TickList.ReadCallsForTests;
            using (gate.HoldLiveness())
                foreach (var c in commands) Assert.IsTrue(gate.Run(c), c);
            Assert.AreEqual(1, TickList.ReadCallsForTests - before, "one attempt read the tick list more than once");
            Assert.AreEqual(40, inner.Ran.Count);
            // CONTROL: outside a hold (the ready gate, before any attempt) it is read per command, as the liveness is
            before = TickList.ReadCallsForTests;
            gate.Run(commands[0]);
            gate.Run(commands[1]);
            Assert.AreEqual(2, TickList.ReadCallsForTests - before);
            // …and a held list still RAISES a risk: the file's 'give' holds its command back inside the attempt
            Tick("raw TopUp 10000");
            using (gate.HoldLiveness())
                Assert.IsFalse(gate.Run("raw TopUp 10000"));
        }

        [Test]
        public void ADeliveredFileCannotLowerARisk()
        {
            WriteTickList("{ \"$schemaVersion\": 1, \"candidates\": [ { \"command\": \"raw ResetUser\", \"kind\": \"level\", \"risk\": [] } ] }");
            Tick("raw ResetUser");
            var (gate, inner, _) = Gate();
            Assert.IsFalse(gate.Run("raw ResetUser"), "the file called a reset a level cheat, and the gate believed it");
            CollectionAssert.IsEmpty(inner.Ran);
        }

        [Test]
        public void AnUnreadableTickListListsNothing_AndProposesNothing()
        {
            WriteTickList("{ \"$schemaVersion\": 1, \"candidates\": [ { \"command\": \"SelectHero {hero}\" } ] }");
            Assert.IsEmpty(TickList.Read(_root, out var why));
            StringAssert.Contains("cannot be ticked", why);
            Assert.IsFalse(Levers.Rows(_root, null).Any(r => r.Source == Levers.LeverSource.Candidate));
        }

        private const string Shots =
            "{ \"$schemaVersion\": 1, \"shots\": [ { \"name\": \"a\", \"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], " +
            "\"settle\": { \"kind\": \"present\", \"name\": \"Board\" } } ] }";
        private const string Adapter = "{ \"gameId\": \"g\" }";

        private static SyncNovaFiles Sent(string? tickList, string? tickSha = null)
        {
            var claim = new JObject
            {
                ["shots"] = Shots, ["adapter"] = Adapter,
                ["shotsSha256"] = SyncNova.Sha256OfText(Shots), ["adapterSha256"] = SyncNova.Sha256OfText(Adapter),
            };
            if (tickList != null)
            {
                claim["tickList"] = tickList;
                claim["tickListSha256"] = tickSha ?? SyncNova.Sha256OfText(tickList);
            }
            return SyncNovaFiles.FromJson(claim)!;
        }

        [Test]
        public void ASendWritesTheTickList_AndNotATick_AndItDoesNotMakeTheGateLive()
        {
            var r = SyncNova.Run(_root, Sent(TwoCandidates), "run-1", "g");
            Assert.IsNull(r.Refusal);
            Assert.AreEqual(TwoCandidates, File.ReadAllText(TickList.FilePath(_root)));
            Assert.AreEqual(2, TickList.Read(_root).Count);
            CollectionAssert.IsEmpty(Levers.Approved(_root), "a delivered candidate became a tick");
            CollectionAssert.IsEmpty(r.LeversApproved);
            // the history names the pair only: the tick list is not a delivery the gate is derived from
            var synced = File.ReadAllText(SyncNova.SyncedFile(_root));
            StringAssert.DoesNotContain(SyncNova.Sha256OfText(TwoCandidates), synced);
        }

        [Test]
        public void P3_TheSendReportsTheShaOfTheTickListItHoldsReadBackOffTheDisk()
        {
            var r = SyncNova.Run(_root, Sent(TwoCandidates), "run-1", "g");
            Assert.IsNull(r.Refusal);
            Assert.AreEqual(SyncNova.Sha256OfText(TwoCandidates), r.TickListSha256);
            var facts = JObject.Parse(r.ToFactsJson("0.11.0"));
            Assert.AreEqual(SyncNova.Sha256OfText(TwoCandidates), facts["tickListSha256"]!.Value<string>());
            // no list on disk at all: said as null, never an echo of something sent
            var bare = Path.Combine(Path.GetTempPath(), "p3sync-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(RelayPaths.NovaDir(bare));
                var none = SyncNova.Run(bare, Sent(null), "run-2", "g");
                Assert.IsNull(none.Refusal);
                Assert.AreEqual(JTokenType.Null, JObject.Parse(none.ToFactsJson("0.11.0"))["tickListSha256"]!.Type);
            }
            finally
            {
                try { Directory.Delete(bare, true); } catch (IOException) { }
            }
        }

        [Test]
        public void ASendWithoutATickListLeavesTheOneOnDiskAlone()
        {
            WriteTickList(TwoCandidates);
            Assert.IsNull(SyncNova.Run(_root, Sent(null), "run-1", "g").Refusal);
            Assert.AreEqual(TwoCandidates, File.ReadAllText(TickList.FilePath(_root)));
        }

        [Test]
        public void HalfATickList_TheWrongBytes_OrAListTheWindowWouldRefuse_RefusesTheWholeSend()
        {
            var half = SyncNovaFiles.FromJson(new JObject
            {
                ["shots"] = Shots, ["adapter"] = Adapter,
                ["shotsSha256"] = SyncNova.Sha256OfText(Shots), ["adapterSha256"] = SyncNova.Sha256OfText(Adapter),
                ["tickList"] = TwoCandidates,
            });
            Assert.AreEqual(SyncNovaFiles.HalfATickListReason, SyncNova.Run(_root, half, "run-1", "g").Refusal);
            StringAssert.Contains("not the one the website sent", SyncNova.Run(_root, Sent(TwoCandidates, new string('0', 64)), "run-1", "g").Refusal);
            var template = "{ \"$schemaVersion\": 1, \"candidates\": [ { \"command\": \"tap-at {x} {y}\" } ] }";
            StringAssert.Contains("nothing was written", SyncNova.Run(_root, Sent(template), "run-1", "g").Refusal);
            Assert.IsFalse(File.Exists(RelayPaths.NovaShotsFile(_root)), "a refused send wrote shots.json");
            Assert.IsFalse(File.Exists(TickList.FilePath(_root)), "a refused send wrote the tick list");
        }

        // ---- 3. exact membership at the bridge, press verbs included ------------------------------------------------

        [Test]
        public void AnUntickedCloudCommandIsRefusedAtTheBridge_IncludingEveryPressVerb()
        {
            var (gate, inner, log) = Gate();
            foreach (var command in new[] { "raw SkipLevels 10", "tap-at 540 960", "click Close_Button", "invoke-button Close_Button",
                         "press-at 1 2", "tap-through 1 2", "drag 1 2 3 4" })
            {
                Assert.IsFalse(gate.Run(command), $"'{command}' ran with no tick");
                CollectionAssert.Contains(log, Levers.NotApprovedLog(command));
            }
            CollectionAssert.IsEmpty(inner.Ran);
        }

        [Test]
        public void ATickedPressVerbRunsOnlyAsWritten_TheCloudCannotChangeItsArguments()
        {
            Tick("tap-at 540 960");
            Tick("click Close_Button");
            var (gate, inner, _) = Gate();
            Assert.IsTrue(gate.Run("tap-at 540 960"));
            Assert.IsTrue(gate.Run("click Close_Button"));
            Assert.IsFalse(gate.Run("tap-at 10 10"), "other coordinates");
            Assert.IsFalse(gate.Run("click Close_Button 1"), "another index");
            Assert.IsFalse(gate.Run("click Close_Button@Close"), "a label the tick did not name");
            CollectionAssert.AreEqual(new[] { "tap-at 540 960", "click Close_Button" }, inner.Ran);
        }

        [Test]
        public void ATemplateCannotBeTicked_AndOneInTheFileByHandCoversNothing_NotEvenItself()
        {
            Assert.AreEqual(Levers.FixedCommandsOnlyReason, Levers.SetApproved(_root, "tap-at {x} {y}", true));
            Assert.IsFalse(File.Exists(Levers.FilePath(_root)), "a refused tick wrote the file");

            File.WriteAllText(Levers.FilePath(_root),
                "{ \"$schemaVersion\": 1, \"approved\": [\"tap-at {x} {y}\", \"SelectHero {hero}\"] }");
            var (gate, inner, _) = Gate();
            Assert.IsFalse(gate.Run("tap-at 5 5"), "the cloud filled in a ticked template's arguments");
            Assert.IsFalse(gate.Run("SelectHero Knight"));
            Assert.IsFalse(gate.Run("SelectHero {hero}"), "a template covers not even its own text");
            CollectionAssert.IsEmpty(inner.Ran);
            Assert.IsFalse(Levers.IsTicked(Levers.Approved(_root), "SelectHero Knight"), "a try's pre-check agrees with the gate");

            // …and what is in the file by hand can still be taken back
            Assert.IsNull(Levers.SetApproved(_root, "tap-at {x} {y}", false));
            CollectionAssert.AreEqual(new[] { "SelectHero {hero}" }, Levers.Approved(_root));
        }

        [Test]
        public void ATickedPressByNameWithARiskyWordIsNeverPressed_EvenOnANonProductionProject()
        {
            Tick("click BuyGems_Button");
            Levers.SetNonProduction(_root, true);
            var (gate, inner, log) = Gate();
            Assert.IsFalse(gate.Run("click BuyGems_Button"));
            CollectionAssert.IsEmpty(inner.Ran);
            Assert.That(log, Has.Some.StartsWith("PRESS REFUSED"));
        }

        [Test]
        public void TheWindowShowsATemplateRowUntickableWithTheReason()
        {
            File.WriteAllText(RelayPaths.NovaShotsFile(_root),
                "{ \"$schemaVersion\": 1, \"shots\": [ { \"name\": \"a\", \"parameters\": [\"h\"], \"setup\": [\"SelectHero {h}\"], " +
                "\"steps\": [ { \"kind\": \"wait\", \"seconds\": 1 } ], \"settle\": { \"kind\": \"present\", \"name\": \"B\" } } ] }");
            var row = Levers.Rows(_root, null).Single(r => r.Command == "SelectHero {h}");
            Assert.IsFalse(row.CanTick);
            Assert.IsFalse(row.Approved);
            Assert.AreEqual(Levers.FixedCommandsOnlyReason, row.Note);
        }
    }
}
