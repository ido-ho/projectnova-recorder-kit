using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>A6 10c — the wire contract and the resume rules, with no editor or server.</summary>
    public class CaptureJobTests
    {
        private string _root = "";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "novakit-capturejob-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private const string ListJson = @"{ ""jobs"": [
            { ""runId"": ""r1"", ""workspaceId"": ""ws1"", ""leaseSec"": 1800, ""createdAt"": ""2026-09-04T10:00:00.000Z"",
              ""items"": [ { ""shot"": ""board-roll-large-coins"", ""take"": 1 }, { ""shot"": ""board-roll-large-coins"", ""take"": 2 } ] }
        ] }";

        [Test]
        public void ParseList_ReadsTheWireShapeTheApiSends()
        {
            var jobs = CaptureJob.ParseList(ListJson, out var error);
            Assert.IsNull(error);
            Assert.AreEqual(1, jobs.Count);
            Assert.AreEqual("r1", jobs[0].RunId);
            Assert.AreEqual("ws1", jobs[0].WorkspaceId);
            Assert.AreEqual(1800, jobs[0].LeaseSec);
            Assert.AreEqual(2, jobs[0].Items.Count);
            Assert.AreEqual("board-roll-large-coins", jobs[0].Items[1].Shot);
            Assert.AreEqual(2, jobs[0].Items[1].Take);
        }

        [Test]
        public void ParseList_EmptyIsNotAnError_MalformedItemFailsTheWholeList()
        {
            Assert.AreEqual(0, CaptureJob.ParseList(@"{ ""jobs"": [] }", out var e1).Count);
            Assert.IsNull(e1);

            var bad = CaptureJob.ParseList(@"{ ""jobs"": [ { ""runId"": ""r1"", ""items"": [ { ""shot"": """", ""take"": 1 } ] } ] }", out var e2);
            Assert.AreEqual(0, bad.Count);
            StringAssert.Contains("item 0 is malformed", e2);

            Assert.AreEqual(0, CaptureJob.ParseList("not json", out var e3).Count);
            StringAssert.Contains("not JSON", e3);
        }

        [Test]
        public void CaptureJobItem_TargetStage_IsOptional_RoundTrips_AndIgnoresWrongTypes()
        {
            // plan step 6: the footage door may name a stage per shot; an owner capture names none.
            var withStage = CaptureJobItem.FromJson(
                JObject.Parse(@"{ ""shot"": ""board-attack-toast"", ""take"": 1, ""targetStage"": ""raided"" }"));
            Assert.AreEqual("raided", withStage!.TargetStage);
            Assert.AreEqual("raided", withStage.ToJson()["targetStage"]!.Value<string>());

            // absent → null, and the item still parses (an older API sends no such field)
            var noStage = CaptureJobItem.FromJson(JObject.Parse(@"{ ""shot"": ""s"", ""take"": 1 }"));
            Assert.IsNull(noStage!.TargetStage);
            Assert.IsFalse(noStage.ToJson().ContainsKey("targetStage"));

            // a wrong-typed or blank value is treated as absent, never a parse failure of the item
            var wrong = CaptureJobItem.FromJson(JObject.Parse(@"{ ""shot"": ""s"", ""take"": 1, ""targetStage"": 7 }"));
            Assert.IsNull(wrong!.TargetStage);
            var blank = CaptureJobItem.FromJson(JObject.Parse(@"{ ""shot"": ""s"", ""take"": 1, ""targetStage"": ""  "" }"));
            Assert.IsNull(blank!.TargetStage);
        }

        [Test]
        public void ParseClaim_UnwrapsTheJobEnvelope()
        {
            var job = CaptureJob.ParseClaim(@"{ ""job"": { ""runId"": ""r9"", ""workspaceId"": ""w"", ""leaseSec"": 60, ""items"": [ { ""shot"": ""s"", ""take"": 1 } ] }, ""leaseUntil"": ""x"" }", out var error);
            Assert.IsNull(error);
            Assert.AreEqual("r9", job!.RunId);
            Assert.IsNull(CaptureJob.ParseClaim(@"{ ""leaseUntil"": ""x"" }", out var e2));
            StringAssert.Contains("not an object", e2);
        }

        [Test]
        public void Progress_RoundTripsThroughDiskAndResumesAtTheNextItem()
        {
            var job = CaptureJob.ParseList(ListJson, out _)[0];
            var p = CaptureProgress.Start(job);
            var file = Path.Combine(_root, "Library", "AdRelay", CapturePaths.ProgressFileName);
            Assert.AreEqual(file, CapturePaths.ProgressFile(_root));

            Assert.IsFalse(p.Done);
            Assert.AreEqual(1, p.Current!.Take);
            p.RecordCaptured();
            p.PlayModeStartSceneSet = true;
            p.Save(file);

            var back = CaptureProgress.Load(file)!;
            Assert.AreEqual("r1", back.RunId);
            Assert.IsTrue(back.PlayModeStartSceneSet, "the pinned-start-scene flag must survive the reload");
            Assert.AreEqual(1, back.NextIndex);
            Assert.AreEqual(1, back.Captured);
            Assert.AreEqual(2, back.Current!.Take);

            back.RecordFailed("upload failed: HTTP 500");
            Assert.IsTrue(back.Done);
            Assert.IsNull(back.Current);
            Assert.AreEqual(1, back.Failed);
            StringAssert.Contains("board-roll-large-coins take 2: upload failed", back.Errors[0]);

            // A job never re-runs an item: recording past the end is a no-op.
            back.RecordCaptured();
            Assert.AreEqual(1, back.Captured);

            var report = JObject.Parse(back.DoneReportJson());
            Assert.AreEqual(1, report["captured"]!.Value<int>());
            Assert.AreEqual(1, report["failed"]!.Value<int>());
            Assert.AreEqual(1, ((JArray)report["errors"]!).Count);
        }

        [Test]
        public void Progress_Load_IsNullForAbsentOrMalformed_NeverThrows()
        {
            Assert.IsNull(CaptureProgress.Load(Path.Combine(_root, "nope.json")));
            var file = Path.Combine(_root, "bad.json");
            File.WriteAllText(file, "{ not json");
            Assert.IsNull(CaptureProgress.Load(file));
            File.WriteAllText(file, @"{ ""runId"": ""r"", ""items"": [ { ""shot"": ""s"" } ] }");
            Assert.IsNull(CaptureProgress.Load(file), "an item without a take is malformed");
        }

        [Test]
        public void ClipFinder_PicksTheNewestLiveTakeWrittenAfterTheShotStarted()
        {
            var dir = Path.Combine(_root, "Recordings");
            Directory.CreateDirectory(dir);
            var old = Path.Combine(dir, "board-roll_001.mp4");
            File.WriteAllText(old, "old");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-10));
            var started = DateTime.UtcNow.AddSeconds(-1);
            var failed = Path.Combine(dir, "board-roll_FAILED_002.mp4");
            File.WriteAllText(failed, "bad");
            var other = Path.Combine(dir, "board-roll-large-coins_003.mp4");
            File.WriteAllText(other, "different shot");
            var take = Path.Combine(dir, "board-roll_003.mp4");
            File.WriteAllText(take, "new");
            Thread.Sleep(20);
            var newest = Path.Combine(dir, "board-roll_004.mp4");
            File.WriteAllText(newest, "newest");

            Assert.AreEqual(newest, ClipFinder.Newest(dir, "board-roll", started));
            Assert.IsNull(ClipFinder.Newest(dir, "board-roll", DateTime.UtcNow.AddMinutes(1)), "nothing written after a future start");
            Assert.IsNull(ClipFinder.Newest(Path.Combine(_root, "missing"), "board-roll", started));
        }

        [Test]
        public void Endpoints_JoinTolerateTrailingSlashesAndNameTheFiveRoutes()
        {
            Assert.AreEqual("https://x.test/api/studio/me", StudioEndpoints.Me("https://x.test/api/"));
            Assert.AreEqual("https://x.test/api/studio/capture-jobs", StudioEndpoints.Jobs("https://x.test/api"));
            Assert.AreEqual("https://x.test/api/studio/capture-jobs/r1/claim", StudioEndpoints.Claim("https://x.test/api", "r1"));
            Assert.AreEqual("https://x.test/api/studio/capture-jobs/r1/clips", StudioEndpoints.Clips("https://x.test/api", "r1"));
            Assert.AreEqual("https://x.test/api/studio/capture-jobs/r1/done", StudioEndpoints.Done("https://x.test/api", "r1"));
        }

        [Test]
        public void ProjectKey_IsStableAndShort()
        {
            var a = NovaCaptureAgent.ProjectKeyOf("/Users/x/Project");
            Assert.AreEqual(a, NovaCaptureAgent.ProjectKeyOf("/Users/x/Project"));
            Assert.AreNotEqual(a, NovaCaptureAgent.ProjectKeyOf("/Users/x/Other"));
            Assert.AreEqual(12, a.Length);
        }

        // ---- Slice B/C: kind on the wire, gameId identity (inv 100), job-level refusals ----

        [Test]
        public void Kind_AbsentIsCapture_ExplicitIsKept_AndNonCaptureMayOmitItems()
        {
            // absent kind on the wire = capture (an older API that predates the field, or a capture)
            var cap = CaptureJob.ParseClaim(
                @"{ ""job"": { ""runId"": ""r"", ""workspaceId"": ""w"", ""leaseSec"": 60, ""items"": [ { ""shot"": ""s"", ""take"": 1 } ] } }",
                out var e1);
            Assert.IsNull(e1);
            Assert.AreEqual(CaptureJob.CaptureKind, cap!.Kind);

            // a non-capture kind legitimately carries no items (a self-test job is {kind}, no items)
            var selfTest = CaptureJob.ParseClaim(
                @"{ ""job"": { ""runId"": ""r"", ""workspaceId"": ""w"", ""leaseSec"": 60, ""kind"": ""self-test"" } }",
                out var e2);
            Assert.IsNull(e2, "a non-capture kind may omit the items array");
            Assert.AreEqual("self-test", selfTest!.Kind);
            Assert.AreEqual(0, selfTest.Items.Count);
        }

        [Test]
        public void Kind_CaptureJobStillRequiresItems()
        {
            // inv-40 negative: a capture job with a dropped items array is refused, not run empty
            var bad = CaptureJob.ParseClaim(
                @"{ ""job"": { ""runId"": ""r7"", ""workspaceId"": ""w"", ""leaseSec"": 60 } }",
                out var err);
            Assert.IsNull(bad);
            StringAssert.Contains("no items array", err);
        }

        [Test]
        public void GameId_ParsesFromTheWire_AndIsNullWhenAbsent()
        {
            var withId = CaptureJob.ParseClaim(
                @"{ ""job"": { ""runId"": ""r"", ""workspaceId"": ""w"", ""leaseSec"": 60, ""gameId"": ""snl-scratch"", ""items"": [ { ""shot"": ""s"", ""take"": 1 } ] } }",
                out _);
            Assert.AreEqual("snl-scratch", withId!.GameId);

            var noId = CaptureJob.ParseClaim(
                @"{ ""job"": { ""runId"": ""r"", ""workspaceId"": ""w"", ""leaseSec"": 60, ""items"": [ { ""shot"": ""s"", ""take"": 1 } ] } }",
                out _);
            Assert.IsNull(noId!.GameId, "an older API sends no gameId; the kit then cannot check and proceeds");
        }

        [Test]
        public void Progress_PersistsKindAndGameId_AcrossTheReload()
        {
            var job = CaptureJob.ParseClaim(
                @"{ ""job"": { ""runId"": ""r"", ""workspaceId"": ""w"", ""leaseSec"": 60, ""gameId"": ""snl-scratch"", ""items"": [ { ""shot"": ""s"", ""take"": 1 } ] } }",
                out _)!;
            var p = CaptureProgress.Start(job);
            Assert.AreEqual(CaptureJob.CaptureKind, p.Kind);
            Assert.AreEqual("snl-scratch", p.GameId);

            var file = Path.Combine(_root, "prog.json");
            p.Save(file);
            var back = CaptureProgress.Load(file)!;
            Assert.AreEqual(CaptureJob.CaptureKind, back.Kind, "kind must survive the Play Mode reload");
            Assert.AreEqual("snl-scratch", back.GameId, "gameId must survive — the wrong-game check runs after the reload");
        }

        [Test]
        public void RecordJobFailure_FailsEveryRemainingItem_AndAtLeastOneForAnEmptyJob()
        {
            // a two-item job refused at the start: both items count as failed, and the job is done
            var twoItems = CaptureJob.ParseList(ListJson, out _)[0];
            var p = CaptureProgress.Start(twoItems);
            p.RecordJobFailure("job is for game 'x' but this editor is game 'y' — refusing (wrong game)");
            Assert.IsTrue(p.Done);
            var report = JObject.Parse(p.DoneReportJson());
            Assert.AreEqual(0, report["captured"]!.Value<int>());
            Assert.AreEqual(2, report["failed"]!.Value<int>(),
                "a wrong-game refusal must read failed, never completed (inv 100)");
            StringAssert.Contains("wrong", report["errors"]![0]!.Value<string>()!.ToLowerInvariant());

            // a job with no items (a self-test this kit cannot handle) still reports one failure, so
            // finish() — which needs failed>0 to read `failed` — cannot mistake it for `completed`
            var selfTest = CaptureJob.ParseClaim(
                @"{ ""job"": { ""runId"": ""r"", ""workspaceId"": ""w"", ""leaseSec"": 60, ""kind"": ""self-test"" } }",
                out _)!;
            var q = CaptureProgress.Start(selfTest);
            q.RecordJobFailure("unsupported job kind 'self-test'");
            Assert.IsTrue(q.Done);
            Assert.AreEqual(1, q.Failed, "even a no-item job must report failed>0");
        }

        [Test]
        public void RecordJobFailure_IsIdempotent_OnADoneRetryWithTheSameReason()
        {
            // A failed `done` POST keeps the progress file; the next poll re-runs the refusal. The
            // same reason must not inflate the count or repeat the message.
            var job = CaptureJob.ParseList(ListJson, out _)[0];
            var p = CaptureProgress.Start(job);
            p.RecordJobFailure("wrong game");
            p.RecordJobFailure("wrong game");
            Assert.IsTrue(p.Done);
            Assert.AreEqual(2, p.Failed, "count is not inflated by the retry");
            Assert.AreEqual(1, p.Errors.Count, "the reason is not repeated");
        }

        [Test]
        public void DoneReportJson_FoldsInShotLoadErrors_DroppingBlanks()
        {
            var job = CaptureJob.ParseList(ListJson, out _)[0];
            var p = CaptureProgress.Start(job);
            p.RecordFailed("unknown shot in this project's shots.json");
            var report = JObject.Parse(
                p.DoneReportJson(new[] { "shots.json: unexpected token at line 5", "  " }));
            var errors = (JArray)report["errors"]!;
            Assert.AreEqual(2, errors.Count, "the per-item error plus one non-blank shot-load error");
            StringAssert.Contains("unexpected token", errors[1]!.Value<string>());
        }

        [Test]
        public void JobGuard_EditorGameId_PrefersAdapterJsonOverARegisteredAdapter()
        {
            // THE regression this slice shipped and the audit caught: SNL's custom adapter registers
            // GameId "snl" but the pack + Library/Nova/adapter.json are "snl-scratch". adapter.json
            // MUST win, or the wrong-game check refuses every SNL job.
            Assert.AreEqual("snl-scratch", JobGuard.EditorGameId("snl-scratch", "snl"));
            Assert.AreEqual("snl", JobGuard.EditorGameId(null, "snl"));  // no adapter.json → fallback
            Assert.AreEqual("snl", JobGuard.EditorGameId("  ", "snl"));   // blank adapter.json → fallback
            Assert.IsNull(JobGuard.EditorGameId(null, ""));               // neither declares one
            Assert.IsNull(JobGuard.EditorGameId(null, null));
        }

        [Test]
        public void JobGuard_RefusalReason_RefusesWrongGameAndUnknownKind_PassesTheRealMatch()
        {
            // positive control (inv 50): a capture job whose stamped game equals this editor's runs
            Assert.IsNull(JobGuard.RefusalReason("capture", "snl-scratch", "snl-scratch"));
            // an older API sent no gameId → cannot check, proceed
            Assert.IsNull(JobGuard.RefusalReason("capture", null, "snl-scratch"));
            // wrong game / wrong workspace
            StringAssert.Contains("wrong game",
                JobGuard.RefusalReason("capture", "askie", "snl-scratch"));
            // stamped game but this project declares none → cannot verify, refuse (never a pass)
            StringAssert.Contains("cannot verify",
                JobGuard.RefusalReason("capture", "snl-scratch", null));
            // a kind this kit does not handle
            StringAssert.Contains("unsupported job kind",
                JobGuard.RefusalReason("sync-nova", "snl-scratch", "snl-scratch"));
            StringAssert.Contains("unsupported job kind",
                JobGuard.RefusalReason("teleport", "snl-scratch", "snl-scratch"));
        }

        [Test]
        public void JobGuard_SelfTest_IsHandled_AndNeverRefusedOnIdentity()
        {
            // Slice B/C commit 2. A self-test is the DOCTOR: it runs on a project that has not been
            // onboarded, so there is no Library/Nova/adapter.json and this editor's identity is the
            // generic fallback "unregistered" (DefaultAdapterBoot). Under the capture rule that is
            // a refusal — "cannot verify" or "wrong game" — which would refuse the ONE job that
            // exists to be run there, and the studio would see nothing at all.
            //
            // Identity is not dropped: the self-test REPORTS this editor's gameId in its facts and
            // the web compares it against the workspace's own bound game. What must not happen is
            // the kit refusing to answer.
            Assert.IsNull(JobGuard.RefusalReason("self-test", "snl-scratch", "snl-scratch"));
            Assert.IsNull(JobGuard.RefusalReason("self-test", "sentaur", null),
                "a project with no adapter.json is exactly where the Doctor is needed");
            Assert.IsNull(JobGuard.RefusalReason("self-test", "sentaur", "unregistered"),
                "the generic fallback identity must not read as the wrong game");
            Assert.IsNull(JobGuard.RefusalReason("self-test", null, null));

            // …and a CAPTURE on the same values still refuses. This is the control that proves the
            // exemption is scoped to the kind and did not quietly disarm invariant 100.
            Assert.IsNotNull(JobGuard.RefusalReason("capture", "sentaur", "unregistered"));
            Assert.IsNotNull(JobGuard.RefusalReason("capture", "sentaur", null));

            Assert.IsTrue(CaptureJob.IsHandled("capture"));
            Assert.IsTrue(CaptureJob.IsHandled("self-test"));
            Assert.IsFalse(CaptureJob.IsHandled("sync-nova"));
            Assert.IsFalse(CaptureJob.IsHandled("probe"));
            Assert.IsFalse(CaptureJob.IsHandled("export"));
        }

        [Test]
        public void SelfTestProgress_IsNotDoneUntilItsResultIsPosted_AndSurvivesTheReload()
        {
            // THE reload trap. A self-test carries no items, so the old `Done` (NextIndex >=
            // Items.Count) was true the instant the job started. Entering Play Mode reloads the
            // domain and wipes every static; the agent that comes back reads this file. Without the
            // flag it would come up, see "done", and report the run complete having never taken the
            // frame — the silent-`done` shape one layer in from the one commit 1 closed.
            var job = new CaptureJob("run-st", "ws1", Array.Empty<CaptureJobItem>(), 1800,
                "self-test", "sentaur");
            var p = CaptureProgress.Start(job);
            Assert.IsTrue(p.ItemsDone, "no items to walk");
            Assert.IsFalse(p.Done, "…but the result is still owed");
            Assert.IsNull(p.Current, "asking a 0-item job for its current item must not throw");

            var file = Path.Combine(_root, "capture-status.json");
            p.Save(file);
            var reloaded = CaptureProgress.Load(file)!;
            Assert.AreEqual("self-test", reloaded.Kind);
            Assert.AreEqual("sentaur", reloaded.GameId);
            Assert.IsFalse(reloaded.Done, "the reloaded agent still owes the result");

            reloaded.ResultPosted = true;
            reloaded.Save(file);
            Assert.IsTrue(CaptureProgress.Load(file)!.Done, "…and is done once it is posted");
        }

        [Test]
        public void SelfTestProgress_ARefusedJobOwesNothingFurther()
        {
            // A job refused whole (unhandled kind, no adapter) could never gather a result, so it
            // must not hang waiting to post one — but it must also not read as having posted one.
            var job = new CaptureJob("run-st", "ws1", Array.Empty<CaptureJobItem>(), 1800,
                "self-test", "sentaur");
            var p = CaptureProgress.Start(job);
            p.RecordJobFailure("no GameAdapter registered in Play Mode");
            Assert.IsTrue(p.Done);
            Assert.IsFalse(p.ResultPosted, "refused is not the same as reported");
            Assert.AreEqual(1, p.Failed, "a 0-item job still counts one failure, never zero");

            // idempotent on a `done`-POST retry after a reload
            p.RecordJobFailure("no GameAdapter registered in Play Mode");
            Assert.AreEqual(1, p.Failed);
            Assert.AreEqual(1, p.Errors.Count);

            var file = Path.Combine(_root, "refused.json");
            p.Save(file);
            Assert.IsTrue(CaptureProgress.Load(file)!.Done, "the refusal survives the reload");
        }

        [Test]
        public void SelfTestProgress_RecordFailedOnAnItemlessJobNoOpsInsteadOfThrowing()
        {
            var job = new CaptureJob("run-st", "ws1", Array.Empty<CaptureJobItem>(), 1800,
                "self-test", null);
            var p = CaptureProgress.Start(job);
            Assert.DoesNotThrow(() => p.RecordFailed("a per-item failure on a job with no items"));
            Assert.AreEqual(0, p.Failed);
            Assert.DoesNotThrow(() => p.RecordCaptured());
            Assert.AreEqual(0, p.Captured);
        }

        [Test]
        public void SelfTestProgress_ResultAttempts_SurviveTheReload_SoRetriesAreBounded()
        {
            // AUDIT M7. Retrying the result POST forever left the run reading "Running in your
            // editor…" on the web with no end — the one outcome a diagnostic must never produce.
            // The count has to survive the domain reload or the cap can never be reached.
            var job = new CaptureJob("run-st", "ws1", Array.Empty<CaptureJobItem>(), 1800,
                "self-test", "sentaur");
            var p = CaptureProgress.Start(job);
            Assert.AreEqual(0, p.ResultAttempts);
            p.ResultAttempts++;
            p.ResultAttempts++;
            var file = Path.Combine(_root, "attempts.json");
            p.Save(file);
            var reloaded = CaptureProgress.Load(file)!;
            Assert.AreEqual(2, reloaded.ResultAttempts);
            Assert.IsFalse(reloaded.Done, "still owes a result while under the cap");

            // At the cap the job is refused WHOLE, which reports done with a reason attached.
            reloaded.RecordJobFailure("could not post the self-test result after 3 attempts: 503");
            Assert.IsTrue(reloaded.Done);
            Assert.IsFalse(reloaded.ResultPosted, "refused is not the same as reported");
            Assert.AreEqual(1, reloaded.Failed);
        }

        [Test]
        public void SelfTestFacts_CarryEveryFieldAlways_AndNeverAVerdict()
        {
            // The kit emits FACTS, never verdicts (3B). An ABSENT field and a false one read the
            // same on a screen, so every field is always present and unknowables are null.
            var facts = SelfTestFacts.Build(
                kitVersion: "0.5.0",
                unityVersion: "6000.0.63f1",
                readyWaitedSec: 30.04,
                framesDrawn: 0,
                playingSec: 31.2f,
                timeScale: 1f,
                runInBackground: true,
                playerSettingsRunInBackground: false,
                unityRecorderPresent: false,
                recorderDriver: "ScreenCaptureDriver",
                editorGameId: null,
                adapterJsonPresent: false,
                bootScene: "Assets/Scenes/Boot.unity",
                shotCount: 0,
                shotLoadErrors: new[] { "shots.json: unexpected token at line 12", "  " },
                frameCaptured: false,
                frameNote: "ScreenCapture wrote no frame within 10s");

            foreach (var key in new[] {
                "kitVersion", "unityVersion", "runInBackground", "playerSettingsRunInBackground",
                "readyWaitedSec", "framesDrawn", "playingSec", "timeScale", "unityRecorderPresent",
                "recorderDriver", "editorGameId", "adapterJsonPresent", "bootScene",
                "shotCount", "shotLoadErrors", "frameCaptured", "frameNote" })
                Assert.IsTrue(facts.ContainsKey(key), key + " must always be present");

            // AUDIT M3/m5: the two runInBackground readings are DIFFERENT numbers (runtime, which
            // the kit forces true on Play Mode entry, vs the build setting it does not touch), and
            // how long the game was given is reported beside whether it ever said it was ready.
            Assert.IsTrue(facts["runInBackground"]!.Value<bool>());
            Assert.IsFalse(facts["playerSettingsRunInBackground"]!.Value<bool>());
            Assert.AreEqual(30.0, facts["readyWaitedSec"]!.Value<double>(), 0.051);
            // Zero frames after 30 s is a HUNG game — the single reading that makes the picture
            // meaningless, and a different fault from a game that drew 400 frames of nothing.
            Assert.AreEqual(0, facts["framesDrawn"]!.Value<int>());

            Assert.AreEqual(JTokenType.Null, facts["editorGameId"]!.Type,
                "an unknown identity is null, never an empty string");
            Assert.IsFalse(facts["frameCaptured"]!.Value<bool>());
            // the parse reason travels WITH the count: "0 shots" and "your shots.json is broken"
            // look identical on a row otherwise
            Assert.AreEqual(0, facts["shotCount"]!.Value<int>());
            Assert.AreEqual(1, ((JArray)facts["shotLoadErrors"]!).Count, "blank reasons dropped");

            // Nothing in here grades the EDITOR. Note what is NOT on this list: `readyGateMet` is
            // the game's own ready gate answering about the GAME, which is a measurement the web
            // needs in order to read a blank frame correctly — banning the substring "ready"
            // banned a fact, which is the opposite of the rule. The list is verdict PHRASINGS.
            var json = SelfTestFacts.ToJson(facts);
            foreach (var verdict in new[] { "healthy", "\"ok\"", "\"pass\"", "\"fail\"", "green", "verdict" })
                StringAssert.DoesNotContain(verdict, json);
        }

        [Test]
        public void SelfTestFacts_AGameWhoseOwnCodeThrewReportsITasAFact()
        {
            // A custom adapter's Shots() is game code and game code throws. Uncaught it reached the
            // agent's catch, which keeps the progress file and retries every 15 s forever with the
            // run stuck on "Running in your editor…". The game's own code failing IS a measurement.
            var facts = SelfTestFacts.Build("0.5.0", "6000.0.63f1", 4.2, 240, 4.3f, 1f, true, true, true,
                "ScreenCaptureDriver", "sentaur", true, "Assets/Scenes/Boot.unity", 0,
                Array.Empty<string>(), true, null,
                "this game's adapter threw while listing its shots: NullReference");
            Assert.IsTrue(facts.ContainsKey("factsError"));
            StringAssert.Contains("NullReference", facts["factsError"]!.Value<string>());
            // …and it is NULL, not absent, when nothing threw (absent ≠ false, the module's rule)
            var clean = SelfTestFacts.Build("0.5.0", "6000.0.63f1", 4.2, 240, 4.3f, 1f, true, true, true,
                "ScreenCaptureDriver", "sentaur", true, "Assets/Scenes/Boot.unity", 7,
                Array.Empty<string>(), true, null);
            Assert.IsTrue(clean.ContainsKey("factsError"));
            Assert.AreEqual(JTokenType.Null, clean["factsError"]!.Type);
        }

        [Test]
        public void SelfTestFacts_ReportFramesDrawn_NotAReadyGateVerdict()
        {
            // RE-AUDIT. The first M3 fix drove the adapter's HygieneReadyGate, which (a) WROTE into
            // the studio's game — `WantsBoard` answers true for an empty shot list, so it ran the
            // game's mute cheat and `RunResist(on:true)` with no matching off, under a screen that
            // says "Records nothing" — and (b) measured footage hygiene, not "is the game up".
            // What the Doctor needs is observable without touching anything: did it draw.
            var hung = SelfTestFacts.Build("0.5.0", "6000.0.63f1", 30.0, 0, 30.1f, 1f, true, true, true,
                "ScreenCaptureDriver", "sentaur", true, "Assets/Scenes/Boot.unity", 7,
                Array.Empty<string>(), true, null);
            Assert.AreEqual(0, hung["framesDrawn"]!.Value<int>());
            Assert.IsFalse(hung.ContainsKey("readyGateMet"), "the gate verdict is gone, not renamed");

            var running = SelfTestFacts.Build("0.5.0", "6000.0.63f1", 4.2, 240, 4.3f, 1f, true, true,
                true, "ScreenCaptureDriver", "sentaur", true, "Assets/Scenes/Boot.unity", 7,
                Array.Empty<string>(), true, null);
            Assert.AreEqual(240, running["framesDrawn"]!.Value<int>());

            // ROUND-3 AUDIT (numeric): `playingSec` is UNSCALED seconds since Play Mode began, not
            // `timeSinceLevelLoad` (scaled by timeScale, reset per scene). A game that pauses
            // itself while the editor is unfocused — the Doctor's NORMAL condition, the studio is
            // in the browser — reported "240 frames in 4.2s · scene up 0s": two numbers on one row
            // contradicting each other. `timeScale` rides along because 0 is the fact that
            // explains a healthy-looking editor recording frozen footage.
            var paused = SelfTestFacts.Build("0.5.0", "6000.0.63f1", 4.2, 240, 12.5f, 0f, true,
                true, true, "ScreenCaptureDriver", "sentaur", true, "Assets/Scenes/Boot.unity", 7,
                Array.Empty<string>(), true, null);
            Assert.AreEqual(12.5, paused["playingSec"]!.Value<double>(), 0.001,
                "unscaled — it must NOT read 0 just because the game paused itself");
            Assert.AreEqual(0.0, paused["timeScale"]!.Value<double>(), 0.001);
            Assert.AreEqual(240, paused["framesDrawn"]!.Value<int>(), "frameCount is not scaled");
            // A blank frame with 240 frames drawn and one with 0 are DIFFERENT faults; the facts
            // have to be able to tell them apart or the row cannot either.
            Assert.AreNotEqual(hung["framesDrawn"]!.Value<int>(), running["framesDrawn"]!.Value<int>());
        }

        [Test]
        public void SelfTestFacts_AGoodEditorReportsTheSameSHAPE(/* positive control */)
        {
            var facts = SelfTestFacts.Build("0.5.0", "6000.0.63f1", 4.2, 240, 4.3f, 1f, true, true, true,
                "UnityRecorderDriver", "sentaur", true, "Assets/Scenes/Boot.unity", 7,
                Array.Empty<string>(), true, null);
            Assert.AreEqual("sentaur", facts["editorGameId"]!.Value<string>());
            Assert.IsTrue(facts["frameCaptured"]!.Value<bool>());
            Assert.AreEqual(JTokenType.Null, facts["frameNote"]!.Type);
            Assert.AreEqual(0, ((JArray)facts["shotLoadErrors"]!).Count);
        }

        [Test]
        public void KitVersion_MatchesThePackageVersionItIsShippedWith()
        {
            // The API's version-skew gate decides which job kinds this editor may be handed from
            // KitVersion.Current. If the const drifts from package.json, the gate is answering
            // about a kit that does not exist. The authoritative lock lives Node-side (it can read
            // both files reliably); this asserts the shape so a typo here is caught in the suite
            // that runs beside the code.
            StringAssert.IsMatch(@"^\d+\.\d+\.\d+$", KitVersion.Current);
            Assert.AreEqual("x-nova-kit-version", KitVersion.Header,
                "Node lowercases incoming header keys; the API reads the lowercase form");
        }

        [Test]
        public void StudioEndpoints_ResultSitsBesideDone()
        {
            Assert.AreEqual("https://box.test/studio/capture-jobs/r1/result",
                StudioEndpoints.Result("https://box.test/", "r1"));
        }
    }
}
