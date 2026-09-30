using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Invariant 190 (kit 0.14.2) — "ALLOW THESE N" and TEACH FINISHES ITSELF. The needed set the website delivered is ticked
    /// by ONE press through the one writer (the per-row toggles' own <see cref="Levers.SetApproved"/> + companion reads),
    /// a risky one is ticked and still held by the gate until the non-production tick, an already-ticked one is not counted,
    /// and nothing the list does not name is written. A teach replays once by itself after Stop and uploads a confirmed
    /// recipe; one it cannot confirm waits for the person's choice.
    /// </summary>
    public class AllowNeededTests
    {
        private string _root = "";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "allow-needed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch (Exception) { /* best effort */ }
        }

        private void WriteTickList(JObject doc) => File.WriteAllText(TickList.FilePath(_root), doc.ToString());

        private static JObject Doc() => new()
        {
            ["$schemaVersion"] = 1,
            ["candidates"] = new JArray(
                new JObject { ["command"] = "cheat-registry", ["kind"] = "other", ["source"] = "kit" },
                new JObject
                {
                    ["command"] = "raw TopUpGems", ["kind"] = "give", ["source"] = "registry",
                    ["check"] = new JObject { ["get"] = "Game.Save.Gems", ["expect"] = "increase" },
                },
                new JObject { ["command"] = "raw SkipLevel", ["kind"] = "level", ["source"] = "registry" },
                // proposed, but no shot or story needs it — never written by the press
                new JObject { ["command"] = "raw MaxLevel", ["kind"] = "level", ["source"] = "registry" }),
            ["needed"] = new JArray(
                new JObject { ["command"] = "raw TopUpGems", ["label"] = "Give me gems" },
                new JObject { ["command"] = "raw SkipLevel", ["label"] = "Skip a level" },
                new JObject { ["command"] = "timeScale", ["label"] = "change the game's speed while recording" },
                new JObject { ["command"] = "raw WinFight" }),
        };

        [Test]
        public void OnePressTicksExactlyTheNeededSet_WithTheirReads_AndNothingElse()
        {
            WriteTickList(Doc());
            // already ticked by hand: not counted, not written again
            Assert.IsNull(Levers.SetApproved(_root, "raw WinFight", true));
            var before = AllowNeeded.For(_root);
            Assert.AreEqual(4, before.NeededTotal);
            CollectionAssert.AreEqual(new[] { "raw TopUpGems", "raw SkipLevel", "timeScale" }, before.ToAllow.Select(i => i.Command).ToArray());
            CollectionAssert.AreEqual(new[] { "Give me gems", "Skip a level", "change the game's speed while recording" }, before.ToAllow.Select(i => i.Name).ToArray(),
                "the plain names, never the raw commands");
            Assert.AreEqual("give", before.ToAllow[0].Kind);
            Assert.AreEqual("3 cheats your ads need", AllowNeeded.Heading(before));
            Assert.AreEqual("Allow these 3", AllowNeeded.ButtonLabel(before));

            Assert.IsNull(AllowNeeded.AllowAll(_root, AllowNeeded.For(_root).ToAllow, out var written));
            Assert.AreEqual(3, written);
            var approved = Levers.Approved(_root);
            CollectionAssert.AreEquivalent(new[] { "raw WinFight", "raw TopUpGems", "get Game.Save.Gems", "raw SkipLevel", "timeScale" }, approved,
                "the needed set and the check's read — the proposed-but-unneeded cheat is NOT ticked");
            CollectionAssert.DoesNotContain(approved, "raw MaxLevel");
            // the gate's own rule now covers every needed lever
            Assert.IsNull(Levers.FirstUnticked(TickList.ReadNeeded(_root).Select(n => n.Command), approved));
            var after = AllowNeeded.For(_root);
            Assert.AreEqual(0, after.ToAllow.Count);
            Assert.IsNull(AllowNeeded.Heading(after));
        }

        [Test]
        public void ARiskyNeededLeverIsTickedByThePress_AndStillHeldByTheGateUntilTheNonProductionTick_SaidInOneSentence()
        {
            WriteTickList(Doc());
            var before = AllowNeeded.For(_root);
            CollectionAssert.AreEqual(new[] { "raw TopUpGems" }, before.HeldAfterPress.Select(i => i.Command).ToArray(), "a give is risky by its kind");
            StringAssert.Contains("“Give me gems” is risky", AllowNeeded.HeldSentence(before));
            StringAssert.Contains(CheatRisk.NonProductionLabel, AllowNeeded.HeldSentence(before));
            Assert.IsNull(AllowNeeded.AllowAll(_root, AllowNeeded.For(_root).ToAllow, out _));
            // ticked, and the gate still holds it — the same rule a per-row tick of it meets (invariant 173)
            Assert.AreEqual("raw TopUpGems", Levers.FirstRiskyNotYet(new[] { "raw TopUpGems" }, _root)?.Lever);
            var ticked = AllowNeeded.For(_root);
            CollectionAssert.AreEqual(new[] { "raw TopUpGems" }, ticked.TickedButHeld.Select(i => i.Command).ToArray());
            StringAssert.Contains("will wait", AllowNeeded.HeldSentence(ticked));
            // the person's non-production tick releases it: nothing held, nothing said
            Assert.IsNull(Levers.SetNonProduction(_root, true));
            Assert.IsNull(Levers.FirstRiskyNotYet(new[] { "raw TopUpGems" }, _root));
            Assert.IsNull(AllowNeeded.HeldSentence(AllowNeeded.For(_root)));
        }

        [Test]
        public void ALeversFileTheKitCannotReadIsNotWrittenOver_ThePressSaysWhy()
        {
            WriteTickList(Doc());
            File.WriteAllText(Levers.FilePath(_root), "{ not json");
            var why = AllowNeeded.AllowAll(_root, AllowNeeded.For(_root).ToAllow, out var written);
            Assert.IsNotNull(why);
            Assert.AreEqual(0, written);
            Assert.AreEqual("{ not json", File.ReadAllText(Levers.FilePath(_root)), "byte for byte");
        }

        [Test]
        public void NoNeededSet_NothingToAllow_AndARefusedListNamesWhy()
        {
            var bare = Doc();
            bare.Remove("needed");
            WriteTickList(bare);
            var v = AllowNeeded.For(_root);
            Assert.AreEqual(0, v.NeededTotal);
            Assert.IsNull(AllowNeeded.Heading(v));
            Assert.IsNull(AllowNeeded.AllowAll(_root, AllowNeeded.For(_root).ToAllow, out var written));
            Assert.AreEqual(0, written);
            Assert.IsEmpty(Levers.Approved(_root), "nothing ticked without a needed set");
            // a needed template is refused whole (the cloud never fills arguments) — and so nothing is offered
            var bad = Doc();
            ((JArray)bad["needed"]!).Add(new JObject { ["command"] = "set Game.Board.NextRoll {v}" });
            WriteTickList(bad);
            var refused = AllowNeeded.For(_root);
            StringAssert.Contains("template", refused.Unreadable);
            Assert.AreEqual(0, refused.ToAllow.Count);
        }

        [Test]
        public void TheConnectionLineSaysWhereItStands_InOneLine()
        {
            StringAssert.StartsWith("Not connected", NovaCaptureWindow.ConnectionSummary(false, null, null, false, "0.14.1"));
            StringAssert.StartsWith("Not connected yet", NovaCaptureWindow.ConnectionSummary(true, "", null, true, "0.14.1"));
            Assert.AreEqual("Connected · Rogue · kit 0.14.1", NovaCaptureWindow.ConnectionSummary(true, "Acct", "Rogue", true, "0.14.1"));
            StringAssert.EndsWith("capture jobs are off", NovaCaptureWindow.ConnectionSummary(true, "Acct", "Rogue", false, "0.14.1"));
        }

        // ---- teach finishes itself ------------------------------------------------------------------------------------

        private static readonly DateTime T0 = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);
        /// <summary>The editor session the person pressed Stop in (audit F2).</summary>
        private const string Sess = "sess";

        private static TeachRequest Req(string start = "lobby-after-boot") =>
            TeachRequest.FromJson(JObject.Parse(
                "{\"ask\":\"show me the equipment screen\",\"declaredStart\":{\"kind\":\"" + start + "\"},\"authoredNames\":[\"Equip\",\"Equipment\",\"EqGrid\"]}"))!;

        private static JArray GoodPresses() => new(new JObject
        {
            ["fired"] = "click", ["selector"] = "Equip", ["pressRoot"] = "Ui/Lobby", ["shared"] = false,
            ["before"] = new JObject { ["roots"] = new JArray("Ui/Lobby") },
            ["after"] = new JObject { ["roots"] = new JArray("Ui/Equipment", "Ui/Lobby"), ["added"] = new JArray("Equipment", "EqGrid"), ["buttons"] = new JArray() },
        });

        private static JObject ConfirmingReplay() => new()
        {
            ["ran"] = true, ["why"] = JValue.CreateNull(), ["dismissed"] = new JArray(),
            ["steps"] = new JArray(new JObject
            {
                ["pressed"] = true, ["refused"] = JValue.CreateNull(),
                ["after"] = new JObject { ["roots"] = new JArray("Ui/Equipment", "Ui/Lobby"), ["added"] = new JArray("Equipment", "EqGrid") },
            }),
        };

        [Test]
        public void AfterStop_TheKitReplaysOnceByItself_ThenUploadsAConfirmedRecipe()
        {
            var s = new TeachState(Req()) { Phase = TeachState.Recording };
            TeachJob.AfterStop(s, GoodPresses(), new[] { "Ui/Lobby" }, 0, 0, null, T0, session: Sess);
            Assert.AreEqual(TeachState.Review, s.Phase);
            Assert.AreEqual(TeachAction.Replay, TeachJob.NextAutomatic(s, playing: false, recorderBusy: false, Sess), "no button: the replay starts by itself");
            Assert.IsTrue(s.AutoReplayed);
            Assert.IsTrue(TeachJob.StartFromPress(s, TeachAction.Replay, Sess, T0.AddSeconds(1)));
            Assert.AreEqual(TeachState.Restart, s.Phase, "the same path a person's Replay takes — Cancel still stops it");
            Assert.IsNull(TeachJob.Refusal(s, TeachAction.Cancel));
            // the replay ran and arrived the same way → confirmed → uploaded by itself
            s.Phase = TeachState.Review;
            s.Replay = ConfirmingReplay();
            Assert.IsTrue(s.Analysis().Confirmed, s.Analysis().Why);
            Assert.AreEqual(TeachAction.Upload, TeachJob.NextAutomatic(s, false, false, Sess));
        }

        [Test]
        public void ATeachOnlyAPictureCanConfirm_IsUploadedByItselfOnlyWhenTheBoxSaidItsKeyCanRunTheRead()
        {
            JObject Req2(bool? pictureRead)
            {
                var o = new JObject
                {
                    ["ask"] = "show me the fight", ["declaredStart"] = new JObject { ["kind"] = "lobby-after-boot" },
                    ["authoredNames"] = new JArray("Lobby", "PlayButton"),
                };
                if (pictureRead != null) o["pictureRead"] = pictureRead.Value;
                return o;
            }
            TeachState Stopped(bool? pictureRead)
            {
                var stop = new JObject
                {
                    ["roots"] = new JArray("Ui/Board"), ["added"] = new JArray("EnemyModel(Clone)"), ["buttons"] = new JArray(),
                    ["afterLastGoalSec"] = 4.2, ["timeLimit"] = false, ["thumbnail"] = TeachRecorder.StopThumbnail,
                };
                var press = new JObject
                {
                    ["fired"] = "click", ["selector"] = "PlayButton", ["pressRoot"] = "Ui/Lobby", ["shared"] = false,
                    ["before"] = new JObject { ["roots"] = new JArray("Ui/Lobby") },
                    ["after"] = new JObject { ["roots"] = new JArray("Ui/Board"), ["added"] = new JArray(), ["buttons"] = new JArray() },
                };
                var s = new TeachState(TeachRequest.FromJson(Req2(pictureRead))!) { Phase = TeachState.Recording };
                TeachJob.AfterStop(s, new JArray(press), new[] { "Ui/Lobby" }, 0, 0, null, T0, stop, session: Sess);
                Assert.AreEqual(TeachAction.Replay, TeachJob.NextAutomatic(s, false, false, Sess), "the replay runs for its picture");
                s.Phase = TeachState.Review;
                s.Replay = new JObject
                {
                    ["ran"] = true, ["why"] = JValue.CreateNull(), ["dismissed"] = new JArray(),
                    ["steps"] = new JArray(new JObject
                    {
                        ["pressed"] = true, ["refused"] = JValue.CreateNull(),
                        ["after"] = new JObject { ["roots"] = new JArray("Ui/Board"), ["added"] = new JArray() },
                    }),
                };
                Assert.IsTrue(s.Analysis().PictureCheck, s.Analysis().Why);
                Assert.IsFalse(s.Analysis().Confirmed, "names cannot confirm it");
                return s;
            }
            Assert.AreEqual(TeachAction.Upload, TeachJob.NextAutomatic(Stopped(true), false, false, Sess), "the box's picture read decides");
            Assert.AreEqual(TeachAction.Upload, TeachJob.NextAutomatic(Stopped(null), false, false, Sess), "an older box (no flag) reads as before");
            var noKey = Stopped(false);
            Assert.AreEqual(TeachAction.None, TeachJob.NextAutomatic(noKey, false, false, Sess), "no usable key: the person chooses");
            StringAssert.Contains("Claude key", TeachJob.NotFinishedWhy(noKey, false, false, Sess));
            Assert.IsFalse(TeachState.FromJson(noKey.ToJson())!.Request.PictureRead, "the flag survives a domain reload");
        }

        [Test]
        public void ARecipeTheReplayCannotConfirm_WaitsForThePersonsChoice_WithOneSentenceWhy()
        {
            var s = new TeachState(Req()) { Phase = TeachState.Review, Presses = GoodPresses(), Replay = RecipeReplay.NotRun("the replay was interrupted") , AutoReplayed = true };
            Assert.IsFalse(s.Analysis().Confirmed);
            Assert.AreEqual(TeachAction.None, TeachJob.NextAutomatic(s, false, false, Sess), "never uploaded unconfirmed by itself");
            StringAssert.StartsWith("Not confirmed: ", TeachJob.NotFinishedWhy(s, false, false, Sess));
            Assert.IsNull(TeachJob.Refusal(s, TeachAction.Upload), "Upload anyway is offered");
            Assert.IsNull(TeachJob.Refusal(s, TeachAction.Discard));
            Assert.IsNull(TeachJob.Refusal(s, TeachAction.TeachAgain));
            // nothing pressed: no replay, no upload by itself
            var empty = new TeachState(Req()) { Phase = TeachState.Review };
            Assert.AreEqual(TeachAction.None, TeachJob.NextAutomatic(empty, true, false, Sess));
            StringAssert.Contains("Nothing was pressed", TeachJob.NotFinishedWhy(empty, true, false, Sess));
        }

        [Test]
        public void TheAutomaticReplayRunsOnce_AReplayThatWentBackWaitsForAPress_AndTheFlagSurvivesADomainReload()
        {
            var s = new TeachState(Req()) { Phase = TeachState.Recording };
            TeachJob.AfterStop(s, GoodPresses(), new[] { "Ui/Lobby" }, 0, 0, null, T0, session: Sess);
            Assert.AreEqual(TeachAction.Replay, TeachJob.NextAutomatic(s, false, false, Sess));
            // the start went back to review before anything was pressed (cancelled, or stale)
            var back = TeachState.FromJson(s.ToJson())!;
            Assert.IsTrue(back.AutoReplayed, "remembered across a domain reload");
            Assert.AreEqual(TeachAction.None, TeachJob.NextAutomatic(back, false, false, Sess), "never started by itself twice");
            StringAssert.Contains("Replay to confirm", TeachJob.NotFinishedWhy(back, false, false, Sess));
            Assert.IsNull(TeachJob.Refusal(back, TeachAction.Replay), "…a person may still press it");
            // Teach again starts clean
            TeachJob.BackToWaiting(back, null, T0);
            Assert.IsFalse(back.AutoReplayed);
        }

        [Test]
        public void ATeachFromHereReplaysByItselfOnlyInPlayMode_AndSaysSoMeanwhile()
        {
            var s = new TeachState(Req()) { Phase = TeachState.Recording, StartHere = true };
            TeachJob.AfterStop(s, GoodPresses(), new[] { "Ui/Lobby" }, 0, 0, null, T0, session: Sess);
            Assert.AreEqual(TeachAction.None, TeachJob.NextAutomatic(s, playing: false, recorderBusy: false, Sess));
            Assert.IsFalse(s.AutoReplayed, "not spent while it could not run");
            StringAssert.Contains("replays by itself once it can", TeachJob.NotFinishedWhy(s, false, false, Sess));
            Assert.AreEqual(TeachAction.Replay, TeachJob.NextAutomatic(s, playing: true, recorderBusy: false, Sess));
            // a relay teach recording: never auto-started over it
            var busy = new TeachState(Req()) { Phase = TeachState.Recording };
            TeachJob.AfterStop(busy, GoodPresses(), new[] { "Ui/Lobby" }, 0, 0, null, T0, session: Sess);
            Assert.AreEqual(TeachAction.None, TeachJob.NextAutomatic(busy, false, recorderBusy: true, Sess));
        }

        // ---- audit F2: the automatic replay follows a Stop in THIS editor session only -----------------------------------

        [Test]
        public void AStopFromAnEarlierEditorSession_NeverStartsTheReplayByItself_ThroughTheAgentsReviewStep()
        {
            var s = new TeachState(Req()) { Phase = TeachState.Recording };
            TeachJob.AfterStop(s, GoodPresses(), new[] { "Ui/Lobby" }, 0, 0, null, T0, session: "session-A");
            // the editor restarted: the progress file is read back, and the agent's review step runs in a NEW session
            var back = TeachState.FromJson(s.ToJson())!;
            Assert.AreEqual("session-A", back.StopSession, "the Stop's session survives a domain reload");
            var later = TeachJob.ReviewDecision(back, TeachAction.None, playing: false, recorderBusy: false, session: "session-B", ask: "show me the equipment screen");
            Assert.AreEqual(TeachAction.None, later.Action, "no Play Mode entered without a press in this session");
            Assert.IsFalse(later.Automatic);
            Assert.IsFalse(back.AutoReplayed, "not spent — the person's Replay is still the first");
            StringAssert.Contains("the editor was restarted after Stop", later.Status);
            Assert.AreEqual("teach: “show me the equipment screen” — " + TeachJob.NotFinishedWhy(back, false, false, "session-B"), later.Status);
            Assert.IsNull(TeachJob.Refusal(back, TeachAction.Replay), "the Replay button still works");
            // a person's Replay press in the new session is honoured (the same path as before)
            Assert.AreEqual(TeachAction.Replay, TeachJob.ReviewDecision(back, TeachAction.Replay, false, false, "session-B", "x").Action);
            // positive control: in the session that pressed Stop, it starts by itself
            var same = TeachState.FromJson(s.ToJson())!;
            var now = TeachJob.ReviewDecision(same, TeachAction.None, false, false, "session-A", "x");
            Assert.AreEqual(TeachAction.Replay, now.Action);
            Assert.IsTrue(now.Automatic);
            // a Stop with no session known (an older progress file) is never replayed by itself
            var unknown = new TeachState(Req()) { Phase = TeachState.Recording };
            TeachJob.AfterStop(unknown, GoodPresses(), new[] { "Ui/Lobby" }, 0, 0, null, T0);
            Assert.AreEqual(TeachAction.None, TeachJob.ReviewDecision(unknown, TeachAction.None, false, false, "session-A", "x").Action);
        }

        [Test]
        public void TheAgentsReviewCase_DecidesThroughReviewDecision_WithItsSession_AndStampsTheStopsSession()
        {
            // RunTeach has no seam a pure test can drive (TeachJobTests reads its source the same way): pin that the loop's
            // Review case runs the ONE decision above with the loop's own session, and that Stop hands AfterStop that session
            var src = File.ReadAllText(Path.Combine(KitHygieneTests.PackageRoot(), "Editor", "Cloud", "NovaCaptureAgent.cs"));
            var start = src.IndexOf("case TeachState.Review:", StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0);
            var review = src.Substring(start, src.IndexOf("if (action == TeachAction.Upload || action == TeachAction.Discard)", start, StringComparison.Ordinal) - start);
            StringAssert.Contains("TeachJob.ReviewDecision(s, action, playing, TeachRecorder.IsTeaching, session, ask)", review);
            StringAssert.Contains("Status = review.Status;", review);
            StringAssert.DoesNotContain("NextAutomatic(", src, "the agent never asks NextAutomatic past the session rule");
            StringAssert.Contains("TeachRecorder.StopJson, TeachRecorder.StartNamesGone, session);", src);
            StringAssert.Contains("var session = EditorSession;", src);
        }

        // ---- audit F6: the status says why, in the window's own sentence ------------------------------------------------

        [Test]
        public void WhenNothingIsAutomatic_TheStatusIsTheWindowsSentence_NeverABlanketNotConfirmed()
        {
            var s = new TeachState(Req()) { Phase = TeachState.Recording, StartHere = true };
            TeachJob.AfterStop(s, GoodPresses(), new[] { "Ui/Lobby" }, 0, 0, null, T0, session: Sess);
            var d = TeachJob.ReviewDecision(s, TeachAction.None, playing: false, recorderBusy: false, session: Sess, ask: "a");
            Assert.AreEqual(TeachAction.None, d.Action);
            StringAssert.Contains("replays by itself once it can", d.Status, "waiting for Play — not 'was not confirmed'");
            StringAssert.DoesNotContain("was not confirmed", d.Status);
        }

        // ---- audits F4 + F5: the press approves exactly what the person saw ---------------------------------------------

        [Test]
        public void TheWindowListsEveryCommandThePressWrites_EachWithItsExactCommand()
        {
            var doc = Doc();
            var needed = (JArray)doc["needed"]!;
            // more than any cap, and commands that have no candidate row (a filled set-value, the go-home lever)
            for (var i = 0; i < 20; i++) needed.Add(new JObject { ["command"] = $"set Game.Board.NextRoll {i}", ["label"] = "Next roll" });
            needed.Add(new JObject { ["command"] = "call Dev.GoLobby", ["label"] = "Go to lobby" });
            WriteTickList(doc);
            var v = AllowNeeded.For(_root);
            var rows = AllowNeeded.RowsOf(v);
            Assert.AreEqual(v.ToAllow.Count + v.ToAllow.Sum(i => i.Companions.Count), rows.Count, "every command the press writes is listed");
            Assert.AreEqual(26, rows.Count, "25 cheats and the one check read a cheat brings along");
            CollectionAssert.AreEqual(v.ToAllow.SelectMany(i => new[] { i.Command }.Concat(i.Companions)).ToArray(), rows.Select(r => r.Command).ToArray(),
                "each row carries its EXACT command — two 'Next roll' rows are told apart");
            StringAssert.StartsWith("• Give me gems (give) — risky", rows[0].Title);
            Assert.AreEqual("set Game.Board.NextRoll 7", rows.Single(r => r.Command.EndsWith(" 7")).Command);
            Assert.AreEqual("Allow these 25", AllowNeeded.ButtonLabel(v));
        }

        [Test]
        public void APressOnAListThatChangedOnDiskWritesNothing_AndSaysLookAgain()
        {
            WriteTickList(Doc());
            var drawn = AllowNeeded.For(_root).ToAllow;
            // a new send lands between the drawing and the press — it names one more command
            var next = Doc();
            ((JArray)next["needed"]!).Add(new JObject { ["command"] = "raw ResetSave", ["label"] = "Reset the save" });
            WriteTickList(next);
            Assert.AreEqual(AllowNeeded.ListChanged, AllowNeeded.AllowAll(_root, drawn, out var written));
            Assert.AreEqual(0, written);
            Assert.IsFalse(File.Exists(Levers.FilePath(_root)), "nothing was written");
            Assert.AreEqual("The list changed — look again.", AllowNeeded.ListChanged);
            // one command fewer is a change too
            var fewer = Doc();
            ((JArray)fewer["needed"]!).RemoveAt(0);
            WriteTickList(fewer);
            Assert.AreEqual(AllowNeeded.ListChanged, AllowNeeded.AllowAll(_root, drawn, out _));
            // positive control: looked at again, the press writes exactly that list
            var again = AllowNeeded.For(_root).ToAllow;
            Assert.IsNull(AllowNeeded.AllowAll(_root, again, out var n));
            Assert.AreEqual(again.Count, n);
            CollectionAssert.DoesNotContain(Levers.Approved(_root), "raw TopUpGems");
        }

        // ---- GAP-KIT-1 + LOW-1 (kit 0.14.3): the press shows every check read it ticks; a refused command is counted ----

        [Test]
        public void ThePressShowsEveryCheckReadItTicks_AndACheckSwappedOnDiskIsAChange()
        {
            WriteTickList(Doc());
            var v = AllowNeeded.For(_root);
            var rows = AllowNeeded.RowsOf(v);
            var gems = rows.Select(r => r.Command).ToList().IndexOf("raw TopUpGems");
            Assert.GreaterOrEqual(gems, 0);
            Assert.AreEqual(("    ↳ and the read that checks it", "get Game.Save.Gems"), rows[gems + 1],
                "the check read the press ticks is drawn right under its cheat, with its exact command");
            CollectionAssert.AreEquivalent(rows.Select(r => r.Command).ToArray(),
                new[] { "raw TopUpGems", "get Game.Save.Gems", "raw SkipLevel", "timeScale", "raw WinFight" },
                "the window draws exactly what the press writes");

            // a re-send keeps the cheat but swaps its check for a credential: the drawn list no longer matches
            var drawn = v.ToAllow;
            var swapped = Doc();
            ((JObject)((JArray)swapped["candidates"]!)[1])["check"] = new JObject { ["get"] = "Game.Session.AuthToken", ["expect"] = "change" };
            WriteTickList(swapped);
            Assert.AreEqual(AllowNeeded.ListChanged, AllowNeeded.AllowAll(_root, drawn, out var written));
            Assert.AreEqual(0, written);
            Assert.IsFalse(File.Exists(Levers.FilePath(_root)), "nothing was written — the unseen read above all");

            // positive control: looked at again, the new read is SHOWN, and the press writes exactly what was shown
            var again = AllowNeeded.For(_root);
            CollectionAssert.Contains(AllowNeeded.RowsOf(again).Select(r => r.Command).ToArray(), "get Game.Session.AuthToken");
            Assert.IsNull(AllowNeeded.AllowAll(_root, again.ToAllow, out _));
            CollectionAssert.AreEquivalent(AllowNeeded.RowsOf(again).Select(r => r.Command).ToArray(), Levers.Approved(_root));
            // a read already ticked is not drawn again: un-tick only the cheat — it comes back alone
            Assert.IsNull(Levers.SetApproved(_root, "raw TopUpGems", false));
            var back = AllowNeeded.For(_root).ToAllow.Single();
            Assert.AreEqual("raw TopUpGems", back.Command);
            CollectionAssert.IsEmpty(back.Companions);
        }

        [Test]
        public void ANeededCommandOutsideTheGamesCodeIsCountedInTheList_NotSilentlyLeftOut()
        {
            var doc = Doc();
            // passes the NAME rule (so the file is read), refused by the RESOLVED rule: Process is the runtime's
            ((JArray)doc["needed"]!).Add(new JObject { ["command"] = "call Process.Start calc", ["label"] = "Open a calculator" });
            WriteTickList(doc);
            Assert.IsNull(Levers.NotTickableReason("call Process.Start calc"), "control: the name half lets it through");
            var v = AllowNeeded.For(_root);
            Assert.IsNull(v.Unreadable);
            Assert.AreEqual(5, v.NeededTotal);
            CollectionAssert.AreEqual(new[] { "call Process.Start calc" }, v.Refused.Select(i => i.Command).ToArray());
            CollectionAssert.DoesNotContain(v.ToAllow.Select(i => i.Command).ToArray(), "call Process.Start calc");
            var line = AllowNeeded.RefusedSentence(v);
            Assert.IsNotNull(line, "the list says one is left out");
            StringAssert.StartsWith("1 more is not allowed here — outside your game's code", line);
            StringAssert.Contains("call Process.Start calc", line);
            // control: nothing refused, nothing said
            WriteTickList(Doc());
            Assert.IsNull(AllowNeeded.RefusedSentence(AllowNeeded.For(_root)));
        }
    }
}
