using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// v3 P4 — THE TEACH JOB'S PURE HALF: the claim's request, the persisted state across domain reloads, which button is
    /// allowed when, what Stop leads to (a replay only when the teach can be confirmed), the caps, the facts that go up —
    /// held to the server's schema by <c>recipe.cases.json</c>'s <c>facts</c> — and the game commit stamp.
    /// </summary>
    public class TeachJobTests
    {
        private static readonly DateTime T0 = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);

        private static TeachRequest Req(string start = "lobby-after-boot") =>
            TeachRequest.FromJson(JObject.Parse(
                "{\"ask\":\"show me the equipment screen\",\"declaredStart\":{\"kind\":\"" + start + "\"" +
                (start == "recipe" ? ",\"recipeId\":\"r0123456789abcdef\"" : "") + "},\"authoredNames\":[\"Equip\",\"Equipment\",\"EqGrid\"]}"))!;

        private static JObject Press(string selector, string root, string[] before, string[] after, string[] added, bool shared = false, string fired = "click") => new()
        {
            ["fired"] = fired, ["selector"] = selector, ["pressRoot"] = root, ["shared"] = shared,
            ["before"] = new JObject { ["roots"] = new JArray(before) },
            ["after"] = new JObject { ["roots"] = new JArray(after), ["added"] = new JArray(added), ["buttons"] = new JArray() },
        };

        // ---- the request ----------------------------------------------------------------------------------------------

        [Test]
        public void TheClaimsRequest_IsReadByOneFunction_AndAnythingIncompleteIsNone()
        {
            var ok = TeachRequest.FromClaim("{\"job\":{},\"teach\":{\"ask\":\" show me the shop \",\"declaredStart\":{\"kind\":\"lobby-after-boot\"},\"authoredNames\":[\"Shop\"]}}");
            Assert.AreEqual("show me the shop", ok!.Ask);
            Assert.AreEqual(TeachAnalysis.StartLobby, ok.StartKind);
            CollectionAssert.AreEqual(new[] { "Shop" }, ok.AuthoredNames);
            Assert.AreEqual(TeachAnalysis.StartRecipe, Req("recipe").StartKind);
            foreach (var bad in new[]
            {
                "{\"teach\":{\"declaredStart\":{\"kind\":\"lobby-after-boot\"},\"authoredNames\":[]}}",
                "{\"teach\":{\"ask\":\"x\",\"declaredStart\":{\"kind\":\"board\"},\"authoredNames\":[]}}",
                "{\"teach\":{\"ask\":\"x\",\"declaredStart\":{\"kind\":\"recipe\"},\"authoredNames\":[]}}",
                "{\"teach\":{\"ask\":\"x\",\"declaredStart\":{\"kind\":\"lobby-after-boot\"},\"authoredNames\":[1]}}",
                "{\"teach\":{\"ask\":\"x\",\"declaredStart\":{\"kind\":\"lobby-after-boot\"}}}",
                "{}", "not json",
            })
                Assert.IsNull(TeachRequest.FromClaim(bad), bad);
        }

        [Test]
        public void TheStateSurvivesTheProgressFile_ADomainReloadResumesTheSameStep()
        {
            var s = new TeachState(Req())
            {
                Phase = TeachState.Restart,
                NextAfterBoot = TeachState.AfterBootReplay,
                WaitStartedUtc = T0,
                SettleUntil = new ClockStamp(T0.AddSeconds(30), 130.5, "session-a"),
                StartedAt = new ClockStamp(T0, 100.25, "session-a"),
                Presses = new JArray(Press("Equip", "Ui/Lobby", new[] { "Ui/Lobby" }, new[] { "Ui/Equipment", "Ui/Lobby" }, new[] { "Equipment", "EqGrid" })),
                GameCommit = new string('a', 40),
                Note = "a note",
                Session = "session-a",
                LastPlayExit = new ClockStamp(T0.AddSeconds(5), 105, "session-a"),
                EnteredPlay = true,
                EnteredPlayAt = new ClockStamp(T0.AddSeconds(6), 106, "session-a"),
            };
            var job = new CaptureJob("run-1", "ws-1", new List<CaptureJobItem>(), 600, CaptureJob.TeachKind);
            var progress = CaptureProgress.Start(job);
            progress.Teach = s;
            var path = Path.Combine(Path.GetTempPath(), "teach-progress-" + Guid.NewGuid() + ".json");
            try
            {
                progress.Save(path);
                var back = CaptureProgress.Load(path)!.Teach!;
                Assert.AreEqual(TeachState.Restart, back.Phase);
                Assert.AreEqual(TeachState.AfterBootReplay, back.NextAfterBoot);
                Assert.AreEqual(T0.AddSeconds(30), back.SettleUntil!.Utc);
                Assert.AreEqual(130.5, back.SettleUntil.Mono);
                Assert.AreEqual("session-a", back.SettleUntil.Session);
                Assert.AreEqual(100.25, back.StartedAt!.Mono);
                Assert.AreEqual("show me the equipment screen", back.Request.Ask);
                Assert.IsTrue(JToken.DeepEquals(s.Presses, back.Presses));
                Assert.AreEqual(new string('a', 40), back.GameCommit);
                Assert.AreEqual("session-a", back.Session);
                Assert.AreEqual(T0.AddSeconds(5), back.LastPlayExit!.Utc);
                Assert.AreEqual(105, back.LastPlayExit.Mono);
                Assert.IsTrue(back.EnteredPlay);
                Assert.AreEqual(106, back.EnteredPlayAt!.Mono);
                Assert.AreEqual("session-a", back.EnteredPlayAt.Session);
            }
            finally { File.Delete(path); }
            Assert.IsNull(TeachState.FromJson(JObject.Parse("{\"request\":{},\"phase\":\"waiting\"}")), "no request, no teach");
            Assert.IsNull(TeachState.FromJson(new JObject { ["request"] = Req().ToJson(), ["phase"] = "flying" }), "an unknown phase is none");
        }

        // ---- the buttons ----------------------------------------------------------------------------------------------

        [Test]
        public void EachButtonIsAllowedOnlyInItsPhase()
        {
            var allowed = new Dictionary<string, TeachAction[]>
            {
                [TeachState.Waiting] = new[] { TeachAction.Teach, TeachAction.NotNow },
                [TeachState.Restart] = new[] { TeachAction.Cancel },
                [TeachState.Boot] = new[] { TeachAction.Cancel },
                [TeachState.Recording] = new[] { TeachAction.Stop },
                [TeachState.Review] = new[] { TeachAction.Upload, TeachAction.Discard, TeachAction.TeachAgain },
                [TeachState.Posting] = new TeachAction[0],
            };
            foreach (var phase in allowed.Keys)
            {
                var s = new TeachState(Req()) { Phase = phase, Presses = new JArray(new JObject()) };
                foreach (TeachAction a in Enum.GetValues(typeof(TeachAction)))
                    Assert.AreEqual(allowed[phase].Contains(a), TeachJob.Refusal(s, a) == null, $"{a} in {phase}");
            }
            var empty = new TeachState(Req()) { Phase = TeachState.Review };
            StringAssert.Contains("nothing was pressed", TeachJob.Refusal(empty, TeachAction.Upload));
        }

        [Test]
        public void ARecipeThatStartsWhereAnotherEnds_IsTaughtFromPlayMode_TheLobbyRestartNeverStandsInForIt()
        {
            var s = new TeachState(Req("recipe")) { Phase = TeachState.Waiting };
            StringAssert.Contains("enter Play Mode and play to that point first", TeachJob.Refusal(s, TeachAction.Teach, playing: false));
            Assert.IsNull(TeachJob.Refusal(s, TeachAction.Teach, playing: true));
            Assert.IsNull(TeachJob.Refusal(new TeachState(Req()) { Phase = TeachState.Waiting }, TeachAction.Teach, playing: false),
                "a lobby start restarts Play Mode itself");
        }

        [Test]
        public void AProjectThatNeverComesUpInPlayMode_EndsTheJobAfterThreeEntries_NotAnEndlessRestart()
        {
            var s = new TeachState(Req());
            for (var i = 0; i < TeachJob.MaxPlayStarts; i++)
            {
                Assert.IsNull(TeachJob.PlayStartRefusal(s));
                s.PlayStarts++;
            }
            StringAssert.Contains("never came up", TeachJob.PlayStartRefusal(s));
            TeachJob.Booted(s);
            Assert.IsNull(TeachJob.PlayStartRefusal(s), "the count is of entries since the game last came up");
            s.PlayStarts = 2;
            Assert.AreEqual(2, TeachState.FromJson(s.ToJson())!.PlayStarts, "it survives a domain reload");
        }

        // ---- what Stop leads to ---------------------------------------------------------------------------------------

        private static JArray GoodPresses() =>
            new(Press("Equip", "Ui/Lobby", new[] { "Ui/Lobby" }, new[] { "Ui/Equipment", "Ui/Lobby" }, new[] { "Equipment", "EqGrid" }));

        [Test]
        public void AfterStop_AConfirmableTeachWaitsInReview_TheReplayRunsOnlyOnThePersonsPress_OrIsDiscardedWithoutOne()
        {
            var s = new TeachState(Req()) { Phase = TeachState.Recording };
            TeachJob.AfterStop(s, GoodPresses(), new[] { "Ui/Lobby" }, 1, 0, "abc", T0);
            Assert.AreEqual(TeachState.Review, s.Phase, "no replay presses the game until the person says so");
            Assert.AreEqual(TeachState.AfterBootReplay, s.NextAfterBoot);
            Assert.IsNull(s.Replay, "the replay is pending, not run");
            Assert.AreEqual(1, s.Cancelled);
            Assert.AreEqual("abc", s.GameCommit);
            Assert.IsNull(TeachJob.Refusal(s, TeachAction.Replay), "Replay is offered");
            Assert.IsNull(TeachJob.Refusal(s, TeachAction.Discard), "Discard without replay is offered");
            StringAssert.Contains("did not run", s.Analysis().Why, "shown as not confirmed until it is");

            // the person's OK: a fresh Play session for the replay, stamped with this editor session
            Assert.IsTrue(TeachJob.StartFromPress(s, TeachAction.Replay, "sess", T0.AddSeconds(3)));
            Assert.AreEqual(TeachState.Restart, s.Phase);
            Assert.AreEqual(TeachState.AfterBootReplay, s.NextAfterBoot);
            Assert.AreEqual("sess", s.Session);
            Assert.AreEqual(T0.AddSeconds(3), s.PhaseStartedUtc);

            // a replay that began is never run twice
            var ran = new TeachState(Req()) { Phase = TeachState.Review, Presses = GoodPresses(), Replay = RecipeReplay.NotRun("interrupted") };
            Assert.IsNotNull(TeachJob.Refusal(ran, TeachAction.Replay));
            Assert.IsFalse(TeachJob.StartFromPress(ran, TeachAction.Replay, "sess", T0));
            Assert.IsFalse(TeachJob.StartFromPress(new TeachState(Req()) { Phase = TeachState.Recording }, TeachAction.Teach, "sess", T0),
                "a press only starts from waiting (Teach) or review (Replay)");
        }

        // ---- S1: an editor restart never starts a recording or a replay by itself ------------------------------------

        [Test]
        public void ACrashResume_InRestartOrBoot_ForARecording_GoesBackToWaiting_NeverIntoPlayMode()
        {
            foreach (var phase in new[] { TeachState.Restart, TeachState.Boot })
            {
                var s = new TeachState(Req()) { Phase = TeachState.Waiting, WaitStartedUtc = T0 };
                Assert.IsTrue(TeachJob.StartFromPress(s, TeachAction.Teach, "editor-1", T0));
                s.Phase = phase;
                s.PhaseStartedUtc = T0;
                s.PlayStarts = 1;
                // positive control: the same session within the bound goes on (Play Mode's own domain reloads keep it)
                Assert.IsNull(TeachJob.StaleStart(s, "editor-1", T0.AddSeconds(5), 30), phase);
                Assert.AreEqual(phase, s.Phase);

                var said = TeachJob.StaleStart(s, "editor-2", T0.AddSeconds(5), 30);
                StringAssert.Contains("restarted", said, phase);
                Assert.AreEqual(TeachState.Waiting, s.Phase, phase + ": the person must press Teach again");
                Assert.IsNull(s.Session);
                Assert.AreEqual(0, s.PlayStarts);
                Assert.IsNull(TeachJob.StaleStart(s, "editor-2", T0.AddSeconds(6), 30), "waiting is left alone");
            }
            // a progress file written before sessions were stamped is not trusted either
            var old = new TeachState(Req()) { Phase = TeachState.Boot, PhaseStartedUtc = T0 };
            Assert.IsNotNull(TeachJob.StaleStart(old, "editor-1", T0.AddSeconds(1), 30));
            Assert.AreEqual(TeachState.Waiting, old.Phase);
        }

        [Test]
        public void ACrashResume_InRestartOrBoot_ForAReplay_GoesBackToReview_WhereReplayMustBePressedAgain()
        {
            foreach (var phase in new[] { TeachState.Restart, TeachState.Boot })
            {
                var s = new TeachState(Req()) { Phase = TeachState.Recording };
                TeachJob.AfterStop(s, GoodPresses(), new[] { "Ui/Lobby" }, 0, 0, null, T0);
                Assert.IsTrue(TeachJob.StartFromPress(s, TeachAction.Replay, "editor-1", T0));
                s.Phase = phase;
                var said = TeachJob.StaleStart(s, "editor-2", T0.AddSeconds(2), 30);
                StringAssert.Contains("nothing was pressed", said, phase);
                Assert.AreEqual(TeachState.Review, s.Phase, phase);
                Assert.IsNull(s.Replay, "not run — so it can still be run, on a press");
                Assert.AreEqual(1, s.Presses.Count, "the recording is kept");
                Assert.IsNull(TeachJob.Refusal(s, TeachAction.Replay));
                Assert.IsNull(TeachJob.Refusal(s, TeachAction.Upload));
            }

            // a replay the domain went down under (it began: NotRun is on the state) is not run again — the note must not
            // offer the Replay press the button then refuses
            var began = new TeachState(Req()) { Phase = TeachState.Recording };
            TeachJob.AfterStop(began, GoodPresses(), new[] { "Ui/Lobby" }, 0, 0, null, T0);
            Assert.IsTrue(TeachJob.StartFromPress(began, TeachAction.Replay, "editor-1", T0));
            began.Phase = TeachState.Boot;
            began.Replay = RecipeReplay.NotRun("the replay was interrupted before it finished");
            var note = TeachJob.StaleStart(began, "editor-2", T0.AddSeconds(2), 30);
            Assert.AreEqual(TeachState.Review, began.Phase);
            StringAssert.Contains("the replay was interrupted", note);
            StringAssert.DoesNotContain("press Replay", note);
            Assert.IsNotNull(TeachJob.Refusal(began, TeachAction.Replay), "Replay is refused, as the note says");
            Assert.IsNull(TeachJob.Refusal(began, TeachAction.Upload));
            Assert.IsNull(TeachJob.Refusal(began, TeachAction.TeachAgain));
        }

        [Test]
        public void ARestartOrBootThatLastsTooLong_GoesBack_TheBoundClearsTheSettleWaitAndTheBootsOwnWaits()
        {
            var s = new TeachState(Req()) { Phase = TeachState.Waiting };
            TeachJob.StartFromPress(s, TeachAction.Teach, "e", T0);
            var restartBound = TeachJob.StartBoundSec(s, 45);
            Assert.Greater(restartBound, 45, "a restart may wait the whole settle time");
            Assert.IsNull(TeachJob.StaleStart(s, "e", T0.AddSeconds(restartBound - 1), 45));
            Assert.IsNotNull(TeachJob.StaleStart(s, "e", T0.AddSeconds(restartBound + 1), 45));
            Assert.AreEqual(TeachState.Waiting, s.Phase);

            var b = new TeachState(Req()) { Phase = TeachState.Boot, Session = "e", PhaseStartedUtc = T0 };
            Assert.Greater(TeachJob.StartBoundSec(b, 0), TeachJob.BootSettleSec + TeachJob.BootAdapterWaitSec);
            Assert.IsNull(TeachJob.StaleStart(b, "e", T0.AddSeconds(TeachJob.BootSettleSec + TeachJob.BootAdapterWaitSec), 0));
            Assert.IsNotNull(TeachJob.StaleStart(b, "e", T0.AddSeconds(TeachJob.StartBoundSec(b, 0) + 1), 0));
        }

        // ---- B1: the settle wait follows EVERY Play Mode exit --------------------------------------------------------

        private static ClockStamp Wall(DateTime utc) => new(utc);

        [Test]
        public void TheSettleWait_CountsFromTheLastPlayModeExit_WhoeverEndedIt()
        {
            var s = new TeachState(Req()) { Phase = TeachState.Restart };
            Assert.IsNull(TeachJob.SettleRemainingSec(s, null, 30, Wall(T0)), "no exit seen, nothing to wait for");
            // the person left Play Mode 5 s before pressing Teach: 25 s still to wait, though the teach left nothing
            Assert.AreEqual(25, TeachJob.SettleRemainingSec(s, Wall(T0), 30, Wall(T0.AddSeconds(5))));
            // Play Mode ended by itself at boot (Boot → Restart keeps an old SettleUntil): the later exit wins
            s.SettleUntil = Wall(T0.AddSeconds(10));
            Assert.AreEqual(70, TeachJob.SettleRemainingSec(s, Wall(T0.AddSeconds(40)), 30, Wall(T0)));
            Assert.AreEqual(10, TeachJob.SettleRemainingSec(s, null, 30, Wall(T0)));
            Assert.LessOrEqual(TeachJob.SettleRemainingSec(s, null, 30, Wall(T0.AddSeconds(11)))!.Value, 0, "past it: go");
            // the exit rides the state (the progress file) once the teach has seen it
            Assert.IsTrue(TeachJob.NotePlayExit(s, Wall(T0.AddSeconds(40))));
            Assert.IsFalse(TeachJob.NotePlayExit(s, Wall(T0)), "an older exit changes nothing");
            Assert.IsFalse(TeachJob.NotePlayExit(s, Wall(T0.AddSeconds(40))), "the same exit read again changes nothing");
            Assert.AreEqual(70, TeachJob.SettleRemainingSec(s, null, 30, Wall(T0)));
            Assert.AreEqual(T0.AddSeconds(40), TeachState.FromJson(s.ToJson())!.LastPlayExit!.Utc);
        }

        [Test]
        public void TheSettleWait_InOneEditorSession_RunsOnTheMonotonicClock_AWallClockJumpCannotSkipOrStretchIt()
        {
            // the teach left Play Mode at mono 100 (wall T0); the exit was recorded on both clocks
            var s = new TeachState(Req()) { Phase = TeachState.Restart, Session = "e" };
            var left = new ClockStamp(T0, 100, "e");
            TeachJob.TeachLeftPlay(s, left, 30);
            var exit = new ClockStamp(T0, 100, "e");
            // 10 s later on the monotonic clock, the wall clock jumped FORWARD an hour: still 20 s to wait
            Assert.AreEqual(20, TeachJob.SettleRemainingSec(s, exit, 30, new ClockStamp(T0.AddHours(1), 110, "e")));
            // …or BACKWARD an hour: still 20 s, not 3620
            Assert.AreEqual(20, TeachJob.SettleRemainingSec(s, exit, 30, new ClockStamp(T0.AddHours(-1), 110, "e")));
            // 31 s on, whatever the wall clock says: go
            Assert.LessOrEqual(TeachJob.SettleRemainingSec(s, exit, 30, new ClockStamp(T0.AddHours(-1), 131, "e"))!.Value, 0);
            // another editor session (timeSinceStartup restarted at 0): only the wall clock counts
            Assert.AreEqual(25, TeachJob.SettleRemainingSec(s, exit, 30, new ClockStamp(T0.AddSeconds(5), 3, "other")));

            // the stale bound too: a forward wall jump in the same session is not a stall, a backward one does not hide one
            var b = new TeachState(Req()) { Phase = TeachState.Waiting };
            Assert.IsTrue(TeachJob.StartFromPress(b, TeachAction.Teach, "e", T0, 500));
            Assert.IsNull(TeachJob.StaleStart(b, "e", T0.AddHours(2), 30, 505), "5 s on the monotonic clock is not stale");
            Assert.IsNotNull(TeachJob.StaleStart(b, "e", T0.AddHours(-2), 30, 500 + TeachJob.StartBoundSec(b, 30) + 1),
                "past the bound on the monotonic clock, though the wall clock went back");
            // the boot's own waits count the same way
            var boot = new TeachState(Req()) { Phase = TeachState.Restart, Session = "e" };
            TeachJob.EnteringPlay(boot, new ClockStamp(T0, 200, "e"));
            Assert.AreEqual(3, TeachJob.SinceStartSec(boot, new ClockStamp(T0.AddHours(1), 203, "e")));
        }

        // ---- M1: Cancel before anything is pressed -----------------------------------------------------------------

        [Test]
        public void Cancel_DuringRestartOrBoot_GoesBackWithNothingRecordedOrPressed_ButNotOnceAReplayBegan()
        {
            var rec = new TeachState(Req()) { Phase = TeachState.Waiting };
            TeachJob.StartFromPress(rec, TeachAction.Teach, "e", T0);
            Assert.IsNull(TeachJob.Refusal(rec, TeachAction.Cancel));
            TeachJob.GoBack(rec, "cancelled", T0);
            Assert.AreEqual(TeachState.Waiting, rec.Phase);

            var rep = new TeachState(Req()) { Phase = TeachState.Recording };
            TeachJob.AfterStop(rep, GoodPresses(), new[] { "Ui/Lobby" }, 0, 0, null, T0);
            TeachJob.StartFromPress(rep, TeachAction.Replay, "e", T0);
            rep.Phase = TeachState.Boot;
            Assert.IsNull(TeachJob.Refusal(rep, TeachAction.Cancel));
            TeachJob.GoBack(rep, "cancelled", T0);
            Assert.AreEqual(TeachState.Review, rep.Phase);

            var began = new TeachState(Req()) { Phase = TeachState.Boot, Replay = RecipeReplay.NotRun("interrupted") };
            Assert.IsNotNull(TeachJob.Refusal(began, TeachAction.Cancel), "a replay that began runs to its end");
        }

        // ---- M2: the teach's own Play session ends with it --------------------------------------------------------

        [Test]
        public void WhenTheTeachEnds_ItLeavesThePlaySessionItEntered_NeverThePersonsOwn()
        {
            var s = new TeachState(Req()) { Phase = TeachState.Restart, Session = "e" };
            TeachJob.EnteringPlay(s, new ClockStamp(T0, 10, "e"));
            Assert.IsTrue(TeachJob.LeavePlayOnEnd(s, playing: true, "e", null));
            Assert.IsFalse(TeachJob.LeavePlayOnEnd(s, playing: false, "e", null));
            Assert.IsFalse(TeachJob.LeavePlayOnEnd(new TeachState(Req()), playing: true, "e", null), "Not now while the person plays");
            Assert.IsFalse(TeachJob.LeavePlayOnEnd(null, playing: true, "e", null));
            // Play Mode ending at boot means the teach's session is over
            s.Phase = TeachState.Boot;
            TeachJob.PlayEndedAtBoot(s, T0);
            Assert.IsFalse(s.EnteredPlay);
        }

        [Test]
        public void EnteredPlay_FromAnotherEditorSession_OrBeforeANewerPlayExit_IsForgotten_ThePersonsPlayIsNeverLeft()
        {
            TeachState Entered()
            {
                var s = new TeachState(Req()) { Phase = TeachState.Restart, Session = "e1" };
                TeachJob.EnteringPlay(s, new ClockStamp(T0, 50, "e1"));
                return s;
            }

            // the same editor session, no exit since: the Play session is the teach's own
            var ours = Entered();
            Assert.IsFalse(TeachJob.ForgetForeignPlay(ours, "e1"));
            Assert.IsTrue(ours.EnteredPlay);
            // an exit from BEFORE the teach entered (its own restart's) changes nothing
            Assert.IsTrue(TeachJob.LeavePlayOnEnd(ours, true, "e1", new ClockStamp(T0.AddSeconds(-5), 45, "e1")));

            // a crash and reopen: the file's stamp is another editor session's — the Play session on is the person's
            var reopened = TeachState.FromJson(Entered().ToJson())!;
            Assert.IsTrue(reopened.EnteredPlay, "the file still says so");
            Assert.IsTrue(TeachJob.ForgetForeignPlay(reopened, "e2"));
            Assert.IsFalse(reopened.EnteredPlay);
            Assert.IsFalse(TeachJob.LeavePlayOnEnd(TeachState.FromJson(Entered().ToJson()), true, "e2", null),
                "an end after a reopen does not leave the person's Play session");
            // …which also keeps a stale start from leaving it: the loop forgets first, then StaleStart runs
            var stale = TeachState.FromJson(Entered().ToJson())!;
            TeachJob.ForgetForeignPlay(stale, "e2");
            Assert.IsNotNull(TeachJob.StaleStart(stale, "e2", T0.AddSeconds(1), 30));
            Assert.IsFalse(stale.EnteredPlay);

            // the person pressed Stop then Play between two polls: an exit newer than the entry was noted
            var quick = Entered();
            Assert.IsTrue(TeachJob.NotePlayExit(quick, new ClockStamp(T0.AddSeconds(20), 70, "e1")));
            Assert.IsTrue(TeachJob.ForgetForeignPlay(quick, "e1"));
            Assert.IsFalse(quick.EnteredPlay);
            Assert.IsFalse(TeachJob.LeavePlayOnEnd(Entered(), true, "e1", new ClockStamp(T0.AddSeconds(20), 70, "e1")),
                "an end that never passed the loop still sees the newer exit");
            // the newer exit is judged on the monotonic clock: a wall clock that jumped back does not hide it
            var jumped = Entered();
            TeachJob.NotePlayExit(jumped, new ClockStamp(T0.AddHours(-1), 70, "e1"));
            Assert.IsTrue(TeachJob.ForgetForeignPlay(jumped, "e1"));

            // a file with the flag and no stamp (an older kit) is doubted, not trusted
            Assert.IsTrue(TeachJob.ForgetForeignPlay(new TeachState(Req()) { EnteredPlay = true }, "e1"));
        }

        // ---- M3: one recorder, one owner ------------------------------------------------------------------------------

        [Test]
        public void ARelayTeachAndAWindowTeach_NeverShareTheRecorder()
        {
            var w = new TeachState(Req()) { Phase = TeachState.Waiting };
            StringAssert.Contains("teach-stop", TeachJob.Refusal(w, TeachAction.Teach, true, recorderBusy: true));
            Assert.IsNull(TeachJob.Refusal(w, TeachAction.Teach, true, recorderBusy: false));
            var r = new TeachState(Req()) { Phase = TeachState.Recording };
            TeachJob.AfterStop(r, GoodPresses(), new[] { "Ui/Lobby" }, 0, 0, null, T0);
            StringAssert.Contains("teach-stop", TeachJob.Refusal(r, TeachAction.Replay, true, recorderBusy: true));
            Assert.IsNull(TeachJob.Refusal(r, TeachAction.Replay, true, recorderBusy: false));
            StringAssert.Contains("Nova Capture window", TeachJob.RelayTeachRefusal(windowTeachActive: true));
            Assert.IsNull(TeachJob.RelayTeachRefusal(windowTeachActive: false));
            StringAssert.Contains("press Stop there", TeachJob.RelayTeachStopRefusal(windowRecording: true));
            Assert.IsNull(TeachJob.RelayTeachStopRefusal(windowRecording: false), "a relay teach stays stoppable from the relay");
        }

        // ---- M5: a declared start that cannot be honoured fails the teach ---------------------------------------

        [Test]
        public void PlayModeOffAtBoot_ForARecipeStart_FailsTheTeach_TheLobbyNeverStandsInForIt()
        {
            var s = new TeachState(Req("recipe")) { Phase = TeachState.Waiting };
            Assert.IsTrue(TeachJob.StartFromPress(s, TeachAction.Teach, "e", T0));
            Assert.AreEqual(TeachState.Boot, s.Phase, "a recipe start records in the person's own Play session");
            StringAssert.Contains("the lobby cannot stand in", TeachJob.PlayEndedAtBoot(s, T0));

            // positive control: a lobby start restarts; a replay that began goes to review
            var lobby = new TeachState(Req()) { Phase = TeachState.Boot };
            Assert.IsNull(TeachJob.PlayEndedAtBoot(lobby, T0));
            Assert.AreEqual(TeachState.Restart, lobby.Phase);
            var began = new TeachState(Req()) { Phase = TeachState.Boot, NextAfterBoot = TeachState.AfterBootReplay, Replay = RecipeReplay.NotRun("x") };
            Assert.IsNull(TeachJob.PlayEndedAtBoot(began, T0));
            Assert.AreEqual(TeachState.Review, began.Phase);
        }

        [Test]
        public void AfterStop_ATeachThatCannotBeConfirmedIsNeverReplayed_ItGoesToReviewWithTheReason()
        {
            var s = new TeachState(Req()) { Phase = TeachState.Recording };
            // a shared name with no telling label: refused as unstable before any replay presses the game
            TeachJob.AfterStop(s, new JArray(Press("Button", "Ui/Lobby", new[] { "Ui/Lobby" }, new[] { "Ui/Equipment", "Ui/Lobby" }, new[] { "Equipment", "EqGrid" }, shared: true)),
                new[] { "Ui/Lobby" }, 0, 0, null, T0);
            Assert.AreEqual(TeachState.Review, s.Phase);
            Assert.IsFalse(s.Replay!["ran"]!.Value<bool>());
            StringAssert.Contains("refused as unstable", s.Replay["why"]!.Value<string>());
            var a = s.Analysis();
            Assert.IsFalse(a.Confirmed);
            StringAssert.Contains("unstable", a.Why);
        }

        [Test]
        public void TeachAgain_StartsOverFromWaiting_WithNothingKept()
        {
            var s = new TeachState(Req()) { Phase = TeachState.Review, Presses = new JArray(new JObject()), Replay = new JObject(), Outcome = "taught" };
            TeachJob.BackToWaiting(s, null, T0);
            Assert.AreEqual(TeachState.Waiting, s.Phase);
            Assert.AreEqual(0, s.Presses.Count);
            Assert.IsNull(s.Replay);
            Assert.AreEqual(T0, s.WaitStartedUtc);
        }

        [Test]
        public void ARequestNobodyAnswers_AndARecordingNobodyStops_EachHaveACap()
        {
            var s = new TeachState(Req()) { Phase = TeachState.Waiting, WaitStartedUtc = T0 };
            Assert.IsFalse(TeachJob.WaitExpired(s, T0.AddSeconds(TeachJob.MaxWaitSec - 1)));
            Assert.IsTrue(TeachJob.WaitExpired(s, T0.AddSeconds(TeachJob.MaxWaitSec)));
            s.Phase = TeachState.Recording;
            s.PhaseStartedUtc = T0;
            Assert.IsFalse(TeachJob.WaitExpired(s, T0.AddDays(1)), "only a waiting request expires by the wait cap");
            Assert.IsTrue(TeachJob.RecordingExpired(s, T0.AddSeconds(TeachJob.MaxRecordingSec)));
        }

        // ---- the facts, held to the server --------------------------------------------------------------------------

        [Test]
        public void TheFactsAKitPosts_AreExactlyTheSharedFixturesDocument_WhichTheServersSchemaReads()
        {
            var f = (JObject)RecipeSharedCasesTests.Fixture()["facts"]!;
            var st = (JObject)f["state"]!;
            var req = new TeachRequest(st["ask"]!.Value<string>()!, (JObject)st["declaredStart"]!, st["authoredNames"]!.Values<string>().Select(x => x!));
            var s = new TeachState(req)
            {
                Presses = (JArray)st["presses"]!.DeepClone(),
                Replay = (JObject)st["replay"]!.DeepClone(),
                // kit 0.14.0 (fix 5a): the screen at Stop rides the facts
                Stop = (JObject)st["stop"]!.DeepClone(),
                Cancelled = st["cancelled"]!.Value<int>(),
                Dropped = st["dropped"]!.Value<int>(),
                GameCommit = st["gameCommit"]!.Value<string>(),
            };
            var facts = JObject.Parse(TeachJob.FactsJson(s, "taught", null, "0.12.0", (JObject)st["thumbnails"]!));
            Assert.IsTrue(JToken.DeepEquals(f["doc"], facts), facts.ToString());

            var discarded = JObject.Parse(TeachJob.FactsJson(s, "discarded", "discarded in the editor", "0.12.0", (JObject)st["thumbnails"]!));
            Assert.AreEqual(0, ((JArray)discarded["presses"]!).Count, "a discarded teach sends no press");
            Assert.AreEqual(JTokenType.Null, discarded["replay"]!.Type);
            Assert.AreEqual(0, ((JObject)discarded["thumbnails"]!).Count);

            // a press whose thumbnail file did not make it goes up with none, so no key points at nothing
            var noPictures = JObject.Parse(TeachJob.FactsJson(s, "taught", null, "0.12.0", new JObject()));
            Assert.IsTrue(noPictures["presses"]!.All(p => p["thumbnail"]!.Type == JTokenType.Null));
            Assert.AreEqual("step-1.jpg", s.Presses[1]!["thumbnail"]!.Value<string>(), "the state itself is not changed");
            // kit 0.14.0: the Stop picture too — named only when it goes up
            Assert.AreEqual(JTokenType.Null, noPictures["stop"]!["thumbnail"]!.Type);
            Assert.AreEqual("stop.jpg", s.Stop!["thumbnail"]!.Value<string>());
            Assert.AreEqual(JTokenType.Null, discarded["stop"]!.Type, "a discarded teach sends no Stop screen");
            // a teach with no Stop screen (recorded by a kit before 0.14.0, resumed) says so
            s.Stop = null;
            Assert.AreEqual(JTokenType.Null, JObject.Parse(TeachJob.FactsJson(s, "taught", null, "0.12.0", new JObject()))["stop"]!.Type);
        }

        [Test]
        public void TheStopScreen_SurvivesTheReload_IsReadByTheAnalysis_AndGoesBackWithTeachAgain()
        {
            var s = new TeachState(Req());
            var fightStop = new JObject { ["roots"] = new JArray("Ui/Lobby"), ["added"] = new JArray("Equipment", "EqGrid"), ["buttons"] = new JArray(), ["thumbnail"] = "stop.jpg" };
            // a teach whose last press shows nothing new one beat later — only the Stop screen has the names
            var presses = new JArray(Press("Equip", "Ui/Lobby", new[] { "Ui/Lobby" }, new[] { "Ui/Lobby" }, new string[0]));
            TeachJob.AfterStop(s, presses, new[] { "Ui/Lobby" }, 0, 0, null, T0, fightStop);
            var back = TeachState.FromJson(s.ToJson())!;
            Assert.IsTrue(JToken.DeepEquals(fightStop, back.Stop), "the Stop screen did not survive the reload");
            CollectionAssert.AreEqual(new[] { "EqGrid", "Equipment" }, back.Analysis().TeachArrival);
            Assert.IsNull(back.Analysis().ReplayBlocked, back.Analysis().ReplayBlocked);
            // control: the same teach without it is blocked as before (no names one beat after the press)
            TeachJob.AfterStop(s, presses, new[] { "Ui/Lobby" }, 0, 0, null, T0);
            StringAssert.Contains("only 0 name(s)", s.Analysis().ReplayBlocked);
            TeachJob.AfterStop(s, presses, new[] { "Ui/Lobby" }, 0, 0, null, T0, fightStop);
            TeachJob.BackToWaiting(s, null, T0);
            Assert.IsNull(s.Stop, "Teach again kept the last attempt's Stop screen");
        }

        [Test]
        public void EveryKeyTheRecorderAndTheReplayWrite_IsOneTheServersSchemaKnows()
        {
            var f = (JObject)RecipeSharedCasesTests.Fixture()["facts"]!;
            var pressKeys = f["pressKeys"]!.Values<string>().ToList();
            var stepKeys = f["replayStepKeys"]!.Values<string>().ToList();
            // a press of every shape the recorder writes
            var presses = new[]
            {
                new TeachRecorder.Press { Fired = "click", Selector = "A", Element = new ElementIdentity { Name = "A" }, PressRoot = "Ui/L", After = new ScreenSignature.Observation(), Thumbnail = "step-0.jpg" },
                new TeachRecorder.Press { Fired = "drag", Down = Vector2.zero, Up = Vector2.one * 100, ScreenSize = Vector2.one * 200, Seconds = 0.3 },
                new TeachRecorder.Press { Fired = "hold", ScreenSize = Vector2.one, Seconds = 1 },
                new TeachRecorder.Press { Fired = "cheat", Command = "x" },
            };
            foreach (var p in presses)
                foreach (var prop in TeachRecorder.PressJson(p).Properties())
                    CollectionAssert.Contains(pressKeys, prop.Name, "the server's schema does not know the press key " + prop.Name);
            // a replay step as the replay writes it, pressed and refused
            var replay = new RecipeReplay(new JArray(new JObject { ["kind"] = "click", ["name"] = "A" }, new JObject { ["kind"] = "click", ["name"] = "Gone" }), new string[0]);
            var clock = 0.0;
            foreach (var _ in replay.Run(new OneButton(), _ => true, () => clock)) clock += 0.5;
            Assert.AreEqual(2, replay.Steps.Count);
            foreach (var step in replay.Steps.OfType<JObject>())
                foreach (var prop in step.Properties())
                    CollectionAssert.Contains(stepKeys, prop.Name, "the server's schema does not know the replay key " + prop.Name);
            // kit 0.14.0 (fix 5a): the Stop screen as the recorder writes it
            var stopKeys = f["stopKeys"]!.Values<string>().ToList();
            var stop = TeachRecorder.StopJsonOf(presses, null, new ScreenSignature.Observation(), TeachRecorder.StopThumbnail);
            CollectionAssert.AreEquivalent(stopKeys, stop.Properties().Select(p => p.Name).ToList(), "the Stop screen's keys drifted from the fixture's");
        }

        /// <summary>A screen with one button, "A".</summary>
        private sealed class OneButton : IReplayScreen
        {
            public ScreenSignature.Observation Observe() => new();
            public IReadOnlyList<ElementIdentity> Clickables() => new[] { new ElementIdentity { Name = "A" } };
            public bool Exists(string selector) => selector == "A";
            public bool Click(string selector) => selector == "A";
            public void PointerDown(string selector) { }
            public void PointerUp(string selector) { }
        }

        [Test]
        public void Thumbnails_AreReadAsBase64ByTheNamesThePressesCarry_AndAnythingElseIsLeftOut()
        {
            var dir = Path.Combine(Path.GetTempPath(), "teach-thumbs-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllBytes(Path.Combine(dir, "step-0.jpg"), new byte[] { 0xff, 0xd8, 0xff, 1 });
                File.WriteAllBytes(Path.Combine(dir, "step-2.jpg"), new byte[70 * 1024]);
                File.WriteAllBytes(Path.Combine(dir, "other.jpg"), new byte[] { 1 });
                var presses = new JArray(
                    new JObject { ["thumbnail"] = "step-0.jpg" }, new JObject { ["thumbnail"] = "step-1.jpg" },
                    new JObject { ["thumbnail"] = "step-2.jpg" }, new JObject { ["thumbnail"] = "../other.jpg" }, new JObject());
                var t = TeachJob.Thumbnails(presses, dir);
                CollectionAssert.AreEqual(new[] { "step-0.jpg" }, t.Properties().Select(p => p.Name).ToList());
                Assert.AreEqual(Convert.ToBase64String(new byte[] { 0xff, 0xd8, 0xff, 1 }), t["step-0.jpg"]!.Value<string>());
                Assert.AreEqual(0, TeachJob.Thumbnails(presses, null).Count);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void Thumbnails_CarryTheStopAndReplayPictures_AndUnderTheBudgetKeepThemAndTheLastStepsFirst()
        {
            var dir = Path.Combine(Path.GetTempPath(), "teach-thumbs-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                byte[] Jpeg(int size) { var b = new byte[size]; b[0] = 0xff; b[1] = 0xd8; b[2] = 0xff; return b; }
                for (var i = 0; i < 4; i++) File.WriteAllBytes(Path.Combine(dir, $"step-{i}.jpg"), Jpeg(40 * 1024));
                File.WriteAllBytes(Path.Combine(dir, TeachRecorder.StopThumbnail), Jpeg(40 * 1024));
                File.WriteAllBytes(Path.Combine(dir, TeachRecorder.ReplayStopThumbnail), Jpeg(40 * 1024));
                var presses = new JArray(Enumerable.Range(0, 4).Select(i => (JToken)new JObject { ["thumbnail"] = $"step-{i}.jpg" }));
                var stop = new JObject { ["thumbnail"] = TeachRecorder.StopThumbnail };

                var all = TeachJob.Thumbnails(presses, dir, stop: stop, replayRan: true);
                CollectionAssert.AreEqual(new[] { "step-0.jpg", "step-1.jpg", "step-2.jpg", "step-3.jpg", "stop.jpg", "replay-stop.jpg" },
                    all.Properties().Select(p => p.Name).ToList());
                // no replay ran: its picture (a stale file) is not sent; no Stop screen: nor is the Stop picture
                CollectionAssert.DoesNotContain(TeachJob.Thumbnails(presses, dir, stop: stop).Properties().Select(p => p.Name).ToList(), "replay-stop.jpg");
                CollectionAssert.DoesNotContain(TeachJob.Thumbnails(presses, dir, replayRan: true).Properties().Select(p => p.Name).ToList(), "stop.jpg");

                // a budget for four pictures: the Stop picture, the replay's, then the LAST steps
                var four = TeachJob.CharsOf("step-0.jpg", 40 * 1024) + TeachJob.CharsOf("replay-stop.jpg", 40 * 1024)
                           + TeachJob.CharsOf("stop.jpg", 40 * 1024) + TeachJob.CharsOf("step-9.jpg", 40 * 1024);
                var tight = TeachJob.Thumbnails(presses, dir, stop: stop, replayRan: true, budgetChars: four);
                CollectionAssert.AreEqual(new[] { "step-2.jpg", "step-3.jpg", "stop.jpg", "replay-stop.jpg" },
                    tight.Properties().Select(p => p.Name).ToList());
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void AFullTeach_WithEveryPictureAtTheCap_StillFitsTheBoxsDoor_KeepingTheStopPictureFirst()
        {
            // audit M6: the budget is the door minus the facts as they serialise without pictures — measured, not assumed
            var dir = Path.Combine(Path.GetTempPath(), "teach-door-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                byte[] Jpeg(int size) { var b = new byte[size]; b[0] = 0xff; b[1] = 0xd8; b[2] = 0xff; return b; }
                var many = Enumerable.Range(0, 300).Select(i => "Name_" + i.ToString("D3") + "_Long_Enough").ToArray();
                var presses = new JArray();
                for (var i = 0; i < 40; i++)
                {
                    var p = Press("B" + i, "Ui/Lobby", new[] { "Ui/Lobby" }, new[] { "Ui/Lobby", "Ui/P" + i }, many);
                    p["thumbnail"] = $"step-{i}.jpg";
                    presses.Add(p);
                    File.WriteAllBytes(Path.Combine(dir, $"step-{i}.jpg"), Jpeg(64 * 1024));
                }
                File.WriteAllBytes(Path.Combine(dir, TeachRecorder.StopThumbnail), Jpeg(64 * 1024));
                var stop = new JObject { ["roots"] = new JArray(), ["added"] = new JArray(many), ["buttons"] = new JArray(), ["afterLastGoalSec"] = 2.0, ["thumbnail"] = TeachRecorder.StopThumbnail };
                var s = new TeachState(Req()) { Presses = presses, Stop = stop };
                var bare = TeachJob.FactsJson(s, "taught", null, KitVersion.Current, new JObject()).Length;
                var thumbs = TeachJob.Thumbnails(presses, dir, stop: stop, budgetChars: TeachJob.PictureBudgetChars(bare));
                var facts = TeachJob.FactsJson(s, "taught", null, KitVersion.Current, thumbs);
                Assert.LessOrEqual(facts.Length, TeachJob.FactsDoorChars, "the teach would be refused whole at the door");
                Assert.IsNotNull(thumbs["stop.jpg"], "the Stop picture was not kept first");
                Assert.IsNotNull(thumbs["step-39.jpg"], "the last step's picture was not kept next");
                Assert.IsNull(thumbs["step-0.jpg"], "precondition: the budget was tight enough to leave some out");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void APictureOverTheBoxsCap_IsReencodedAtLowerQuality_NotDropped()
        {
            // a fake encoder whose size falls with quality: 60 → 90 KB, 50 → 70 KB, 40 → 60 KB
            var asked = new List<int>();
            byte[] Encode(int q) { asked.Add(q); return new byte[q >= 60 ? 90 * 1024 : q >= 50 ? 70 * 1024 : 60 * 1024]; }
            var bytes = TeachThumbnail.EncodeUnderCap(Encode);
            Assert.AreEqual(60 * 1024, bytes.Length);
            CollectionAssert.AreEqual(new[] { 60, 50, 40 }, asked, "it did not step down, or kept going past the first fit");
            // a small one is taken at the first quality
            asked.Clear();
            Assert.AreEqual(10, TeachThumbnail.EncodeUnderCap(q => { asked.Add(q); return new byte[10]; }).Length);
            CollectionAssert.AreEqual(new[] { TeachThumbnail.Quality }, asked);
        }

        // ---- the stamp ----------------------------------------------------------------------------------------------

        [Test]
        public void TheGameCommit_IsReadAtTheProjectOrAParent_ThroughTheKitsOwnReader_ElseNull()
        {
            var root = Path.Combine(Path.GetTempPath(), "teach-git-" + Guid.NewGuid());
            var sha1 = new string('1', 40);
            var sha2 = new string('2', 40);
            try
            {
                var git = Path.Combine(root, "game", ".git");
                Directory.CreateDirectory(Path.Combine(git, "refs", "heads"));
                File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
                File.WriteAllText(Path.Combine(git, "refs", "heads", "main"), sha1 + "\n");
                var project = Path.Combine(root, "game", "Unity");
                Directory.CreateDirectory(project);
                Assert.AreEqual(sha1, TeachJob.GameCommit(project), "a Unity project inside the repo walks up to its .git");

                File.Delete(Path.Combine(git, "refs", "heads", "main"));
                File.WriteAllText(Path.Combine(git, "packed-refs"), "# pack-refs\n" + sha2 + " refs/heads/main\n");
                Assert.AreEqual(sha2, TeachJob.GameCommit(project));

                File.WriteAllText(Path.Combine(git, "HEAD"), sha1);
                Assert.AreEqual(sha1, TeachJob.GameCommit(project), "detached");

                var wt = Path.Combine(root, "wt");
                var wtGit = Path.Combine(git, "worktrees", "wt");
                Directory.CreateDirectory(wtGit);
                Directory.CreateDirectory(wt);
                File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: " + wtGit);
                File.WriteAllText(Path.Combine(wtGit, "HEAD"), "ref: refs/heads/main");
                File.WriteAllText(Path.Combine(wtGit, "commondir"), "../..");
                Assert.AreEqual(sha2, TeachJob.GameCommit(wt), "a worktree reads its commondir's packed refs");

                File.WriteAllText(Path.Combine(git, "HEAD"), "garbage");
                Assert.IsNull(TeachJob.GameCommit(project));
                Assert.IsNull(TeachJob.GameCommit(Path.Combine(root, "nowhere")));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        // ---- M2: every end of a teach goes through EndTeach (a structural guard over the agent's source) -------------

        private static string Between(string src, string from, string to)
        {
            var a = src.IndexOf(from, StringComparison.Ordinal);
            Assert.GreaterOrEqual(a, 0, "not found: " + from);
            var b = src.IndexOf(to, a + from.Length, StringComparison.Ordinal);
            Assert.Greater(b, a, "not found after it: " + to);
            return src.Substring(a, b - a);
        }

        /// <summary>
        /// The Unity half (NovaCaptureAgent) has no seam a pure test can drive, so this pins its routing by reading its
        /// source: a teach refused before it runs, or whose run is no longer this editor's on resume, ends through
        /// <c>EndTeach</c> — the one place Play Mode the teach entered is left — never through <c>ReportDone</c> or a
        /// bare <c>DropProgress</c>. A structural guard, not a behaviour test.
        /// </summary>
        [Test]
        public void EveryPathThatEndsATeach_GoesThroughEndTeach_SoPlayModeItEnteredIsNeverLeftOn()
        {
            var src = File.ReadAllText(Path.Combine(KitHygieneTests.PackageRoot(), "Editor", "Cloud", "NovaCaptureAgent.cs"));

            // RunJob's refusals (kind, workspace) before the teach dispatch
            var refusals = Between(src, "private static IEnumerable RunJob(", "if (progress.Kind == CaptureJob.TeachKind)");
            StringAssert.DoesNotContain("ReportDone(", refusals, "a refusal must not report done around EndTeach");
            Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(refusals, @"EndUnrunJob\(progress, progressFile\)").Count);
            var endUnrun = Between(src, "private static IEnumerable EndUnrunJob(", "private static IEnumerable RunJob(");
            StringAssert.Contains("progress.Kind == CaptureJob.TeachKind", endUnrun);
            StringAssert.Contains("? EndTeach(progress, progressFile, report: true)", endUnrun);

            // the resume's re-claim refused: the progress file is dropped, THEN the teach ends (leaving Play reloads the domain)
            var gone = Between(src, "if (!JobGuard.ClaimRefusalEndsJob(claim.Status", "foreach (var _ in RunJob(progress, progressFile))");
            var drop = gone.IndexOf("DropProgress(progressFile);", StringComparison.Ordinal);
            var end = gone.IndexOf("if (progress.Kind == CaptureJob.TeachKind)", StringComparison.Ordinal);
            Assert.GreaterOrEqual(drop, 0);
            Assert.Greater(end, drop, "the teach ends after its progress file is dropped");
            StringAssert.Contains("EndTeach(progress, progressFile, report: false)", gone.Substring(end));
        }

        // ---- the kind -----------------------------------------------------------------------------------------------

        [Test]
        public void TheKindIsHandled_AndAdmittedByTheBindingNotByTheGameIdRule()
        {
            Assert.IsTrue(CaptureJob.IsHandled(CaptureJob.TeachKind));
            Assert.IsNull(JobGuard.RefusalReason(CaptureJob.TeachKind, "game-a", "generic"),
                "a teach before the first Send has no adapter.json; the workspace binding admits it, as for a cheat search");
            Assert.IsNotNull(JobGuard.RefusalReason(CaptureJob.ProbeKind, "game-a", "generic"), "positive control: a probe is checked");
        }
    }
}
