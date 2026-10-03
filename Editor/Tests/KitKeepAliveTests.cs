using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// F0 75a / 75b (2026-10-01 A-to-Z, fix list row 75) — THE KIT STAYS "CONNECTED" DURING ANY LONG ROUTINE.
    ///
    /// Proven live: while a teach waited on a person, <c>Tick</c> returned early (a routine was running), the lease was
    /// refreshed only every 8 minutes, and the box — which calls a kit connected only if a studio-key request arrived in
    /// the last 2 minutes — refused "Make this ad". The window read "Last poll 23:23:59" for 15+ minutes.
    ///
    /// These drive the PRODUCTION seam (invariant 101): <see cref="NovaCaptureAgent.Tick"/> itself, with a never-ending
    /// routine standing in for any long one (the keepalive does not look at the routine's kind, so one stub is the honest
    /// coverage), a fake clock and a fake transport.
    /// </summary>
    public class KitKeepAliveTests
    {
        private const string Run = "run-keepalive-1";
        private double _now;
        private FakeHttp _http = null!;
        private string? _oldKey;
        private bool _hadKey, _hadEnabled, _oldEnabled;

        private sealed class FakeResponse : IStudioResponse
        {
            public bool IsDone { get; set; } = true;
            public long Status { get; set; } = 201;
            public string Body { get; set; } = "{}";
            public string? TransportError { get; set; }
            public void Dispose() { }
        }

        private sealed class Call
        {
            public string Verb = "", Url = "", Body = "";
            public double At;
        }

        /// <summary>Records every call; answers with <see cref="Answer"/>. GET is recorded too — a GET of the jobs list is
        /// the failure this suite exists to catch (it would claim a SECOND job).</summary>
        private sealed class FakeHttp : IStudioHttp
        {
            private readonly Func<double> _now;
            public FakeHttp(Func<double> now) => _now = now;
            public readonly List<Call> Calls = new();
            public Func<Call, FakeResponse> Answer = _ => new FakeResponse();

            private IStudioResponse Record(string verb, string url, string body)
            {
                var c = new Call { Verb = verb, Url = url, Body = body, At = _now() };
                Calls.Add(c);
                return Answer(c);
            }

            public IStudioResponse Get(string url, string studioKey) => Record("GET", url, "");
            public IStudioResponse PostJson(string url, string studioKey, string json) => Record("POST", url, json);
            public IStudioResponse PostFile(string url, string studioKey, string filePath, string fileField,
                IReadOnlyDictionary<string, string> fields) => Record("UPLOAD", url, "");
            public IStudioResponse PostMultipart(string url, string studioKey, IReadOnlyDictionary<string, string> fields,
                string? filePath, string fileField, string contentType) => Record("MULTIPART", url, "");
            public IStudioResponse PutFile(string url, string filePath, IReadOnlyDictionary<string, string> headers) =>
                Record("PUT", url, "");
        }

        /// <summary>A long routine: never ends, does nothing but hand the editor back.</summary>
        private static IEnumerator Forever()
        {
            while (true) yield return null;
        }

        private static IEnumerator EndsAtOnce()
        {
            yield break;
        }

        [SetUp]
        public void SetUp()
        {
            var keyPref = NovaCaptureAgent.PrefKeyForTests("Key");
            var enabledPref = NovaCaptureAgent.PrefKeyForTests("Enabled");
            _hadKey = EditorPrefs.HasKey(keyPref);
            _oldKey = EditorPrefs.GetString(keyPref, "");
            _hadEnabled = EditorPrefs.HasKey(enabledPref);
            _oldEnabled = EditorPrefs.GetBool(enabledPref, false);
            NovaCaptureAgent.ResetForTests();
            _now = 1000;
            NovaCaptureAgent.Clock = () => _now;
            _http = new FakeHttp(() => _now);
            NovaCaptureAgent.Http = _http;
            NovaCaptureAgent.StudioKey = "sk_test_keepalive";
            NovaCaptureAgent.Enabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            NovaCaptureAgent.ResetForTests();
            StartAsk.Window.End();
            var keyPref = NovaCaptureAgent.PrefKeyForTests("Key");
            var enabledPref = NovaCaptureAgent.PrefKeyForTests("Enabled");
            if (_hadKey) EditorPrefs.SetString(keyPref, _oldKey ?? ""); else EditorPrefs.DeleteKey(keyPref);
            if (_hadEnabled) EditorPrefs.SetBool(enabledPref, _oldEnabled); else EditorPrefs.DeleteKey(enabledPref);
        }

        private void TickAt(double t)
        {
            _now = t;
            NovaCaptureAgent.Tick();
        }

        private string ClaimUrl => StudioEndpoints.Claim(NovaCaptureAgent.BaseUrl, Run);

        // ---- 75a ------------------------------------------------------------------------------------------------------

        [Test]
        public void ALongRoutine_KeepsTheBoxInformedEveryMinute_ByReclaimingTheSameRun_NeverByPollingForJobs()
        {
            NovaCaptureAgent.InstallRoutineForTests(Forever(), Run);
            var before = DateTime.UtcNow;

            TickAt(1000 + 30);
            Assert.IsEmpty(_http.Calls, "nothing is sent before a minute has passed since the box last heard from us");

            TickAt(1000 + 61);
            Assert.AreEqual(1, _http.Calls.Count, "a minute in, the box is told this editor is alive");
            Assert.AreEqual("POST", _http.Calls[0].Verb);
            Assert.AreEqual(ClaimUrl, _http.Calls[0].Url, "the SAME run is re-claimed (it also keeps the lease)");
            Assert.AreEqual("{}", _http.Calls[0].Body,
                "no `waiting` key: the box leaves what the kit waits for untouched, so this never races the routine's own refresh");

            TickAt(1000 + 62); // the answer is read on the next tick
            Assert.IsNotNull(NovaCaptureAgent.LastPollUtc, "the window's \"Last poll\" moves with every keepalive");
            Assert.GreaterOrEqual(NovaCaptureAgent.LastPollUtc!.Value, before);

            TickAt(1000 + 100);
            Assert.AreEqual(1, _http.Calls.Count, "the next one waits a minute from the last contact");
            TickAt(1000 + 123);
            Assert.AreEqual(2, _http.Calls.Count, "…and then goes");

            // a 15-minute wait on a person (the live failure): one keepalive a minute, never longer than the 2-minute window
            for (var t = 1000 + 124; t <= 1000 + 15 * 60; t++) TickAt(t);
            var posts = _http.Calls.Select(c => c.At).ToList();
            for (var i = 1; i < posts.Count; i++)
                Assert.Less(posts[i] - posts[i - 1], 2 * 60, "the box's 2-minute rule must never lapse");
            Assert.IsFalse(_http.Calls.Any(c => c.Verb == "GET"),
                "never the capture-jobs poll — it would claim a SECOND job");
            Assert.IsTrue(_http.Calls.All(c => c.Url == ClaimUrl));
        }

        [Test]
        public void AKeepaliveStillInFlight_IsNotSentAgain()
        {
            var pending = new FakeResponse { IsDone = false };
            _http.Answer = _ => pending;
            NovaCaptureAgent.InstallRoutineForTests(Forever(), Run);
            TickAt(1000 + 61);
            TickAt(1000 + 200);
            Assert.AreEqual(1, _http.Calls.Count, "one keepalive at a time");
            pending.IsDone = true;
            TickAt(1000 + 201);
            Assert.IsNotNull(NovaCaptureAgent.LastPollUtc);
        }

        [Test]
        public void ARefusedKeepalive_EndsNothingItself_ButMakesTheRoutinesOwnLeaseRefreshDue()
        {
            NovaCaptureAgent.InstallRoutineForTests(Forever(), Run);
            Assert.IsFalse(NovaCaptureAgent.LeaseRefreshDue, "positive control: just claimed, nothing due");

            _http.Answer = _ => new FakeResponse { Status = 200 };
            TickAt(1000 + 61);
            TickAt(1000 + 62);
            Assert.IsFalse(NovaCaptureAgent.LeaseRefreshDue, "an answered keepalive does not force a refresh");

            _http.Answer = _ => new FakeResponse { Status = 409, Body = "{\"message\":\"claimed by another editor\"}" };
            TickAt(1000 + 130);
            TickAt(1000 + 131);
            Assert.IsTrue(NovaCaptureAgent.LeaseRefreshDue,
                "a refusal hands the decision to RefreshLeaseIfDue — the one place that ends a job (K3, EndTeach routing)");
        }

        [Test]
        public void ADroppedConnection_IsNotARefusal()
        {
            NovaCaptureAgent.InstallRoutineForTests(Forever(), Run);
            _http.Answer = _ => new FakeResponse { Status = 0, TransportError = "Cannot resolve destination host" };
            TickAt(1000 + 61);
            TickAt(1000 + 62);
            Assert.IsFalse(NovaCaptureAgent.LeaseRefreshDue, "nobody answered — nothing about the lease is decided");
            Assert.IsNull(NovaCaptureAgent.LastPollUtc, "and the window does not claim a contact that did not happen");
        }

        [Test]
        public void WithCaptureJobsOff_NoKeepaliveGoes()
        {
            NovaCaptureAgent.Enabled = false;
            NovaCaptureAgent.InstallRoutineForTests(Forever(), Run);
            TickAt(1000 + 61);
            Assert.IsEmpty(_http.Calls, "jobs are off: the box is not told this editor takes jobs");
            NovaCaptureAgent.Enabled = true; // positive control
            TickAt(1000 + 62);
            Assert.AreEqual(1, _http.Calls.Count);
        }

        [Test]
        public void ARoutineWithNoRunInFlight_SendsNoKeepalive()
        {
            NovaCaptureAgent.InstallRoutineForTests(Forever(), null);
            TickAt(1000 + 61);
            Assert.IsEmpty(_http.Calls, "connect and an idle poll are short; there is no run to re-claim");
        }

        [Test]
        public void TheRoutinesOwnLeaseRefresh_StampsLastPoll()
        {
            var progress = CaptureProgress.Start(CaptureJob.ParseList(JobJson, out _)[0]);
            var file = Path.Combine(Path.GetTempPath(), "novakit-keepalive-" + Path.GetRandomFileName() + ".json");
            try
            {
                progress.Save(file);
                NovaCaptureAgent.InstallRoutineForTests(Forever(), Run);
                _now = 1000 + 9 * 60; // past the 8-minute clock
                foreach (var _ in NovaCaptureAgent.RefreshLeaseIfDue(progress, file)) { }
                Assert.AreEqual(1, _http.Calls.Count);
                Assert.IsNotNull(NovaCaptureAgent.LastPollUtc);
            }
            finally { File.Delete(file); }
        }

        // ---- 75b ------------------------------------------------------------------------------------------------------

        private const string JobJson = @"{ ""jobs"": [
            { ""runId"": ""run-keepalive-1"", ""workspaceId"": ""ws1"", ""leaseSec"": 1800, ""createdAt"": ""2026-10-01T10:00:00.000Z"",
              ""items"": [ { ""shot"": ""a"", ""take"": 1 } ] }
        ] }";

        [Test]
        public void AWaitingTeach_ToldTheBoxWhatItWaitsFor_OnTheLeaseRefresh()
        {
            var progress = CaptureProgress.Start(CaptureJob.ParseList(JobJson, out _)[0]);
            var file = Path.Combine(Path.GetTempPath(), "novakit-keepalive-" + Path.GetRandomFileName() + ".json");
            try
            {
                progress.Save(file);
                NovaCaptureAgent.InstallRoutineForTests(Forever(), Run);
                var sentence = TeachJob.WaitingSentence("show me a run state at an early skeleton battle");
                NovaCaptureAgent.SetTeachWaitingForTests(sentence);
                Assert.AreEqual(sentence, NovaCaptureAgent.WaitingForPerson);
                Assert.IsTrue(NovaCaptureAgent.LeaseRefreshDue, "the ask changed: the refresh goes at once");
                foreach (var _ in NovaCaptureAgent.RefreshLeaseIfDue(progress, file)) { }
                Assert.AreEqual(1, _http.Calls.Count);
                Assert.AreEqual(sentence, (string?)JObject.Parse(_http.Calls[0].Body)["waiting"]);

                // the wait ends: cleared at once
                NovaCaptureAgent.SetTeachWaitingForTests(null);
                Assert.IsTrue(NovaCaptureAgent.LeaseRefreshDue);
                foreach (var _ in NovaCaptureAgent.RefreshLeaseIfDue(progress, file)) { }
                Assert.AreEqual(2, _http.Calls.Count);
                Assert.AreEqual(JTokenType.Null, JObject.Parse(_http.Calls[1].Body)["waiting"]!.Type);
            }
            finally { File.Delete(file); }
        }

        /// <summary>Audit M1 — a refresh that carried a NEW waiting sentence and was not answered (no network, a 5xx) must
        /// be retried soon, not in 8 minutes: the keepalive keeps the lease alive meanwhile, so the website would keep the old
        /// sentence ("Waiting for you in Unity") through the whole teach.</summary>
        [Test]
        public void AFailedWaitingChange_IsRetriedSoon_NotInEightMinutes()
        {
            var progress = CaptureProgress.Start(CaptureJob.ParseList(JobJson, out _)[0]);
            var file = Path.Combine(Path.GetTempPath(), "novakit-keepalive-" + Path.GetRandomFileName() + ".json");
            try
            {
                progress.Save(file);
                NovaCaptureAgent.InstallRoutineForTests(Forever(), Run);
                NovaCaptureAgent.SetTeachWaitingForTests(TeachJob.WaitingSentence("x"));
                foreach (var _ in NovaCaptureAgent.RefreshLeaseIfDue(progress, file)) { }
                Assert.IsFalse(NovaCaptureAgent.LeaseRefreshDue, "positive control: the sentence was sent and answered");

                // the person pressed Teach; the clear does not get through
                NovaCaptureAgent.SetTeachWaitingForTests(null);
                _http.Answer = _ => new FakeResponse { Status = 503, Body = "busy" };
                foreach (var _ in NovaCaptureAgent.RefreshLeaseIfDue(progress, file)) { }
                Assert.AreEqual(2, _http.Calls.Count);
                Assert.IsFalse(NovaCaptureAgent.LeaseRefreshDue, "not on every tick — a dead network is not hammered");
                _now += NovaCaptureAgent.WaitingRetrySec + 1;
                Assert.IsTrue(NovaCaptureAgent.LeaseRefreshDue, "…but within seconds, not 8 minutes");
                _http.Answer = _ => new FakeResponse();
                foreach (var _ in NovaCaptureAgent.RefreshLeaseIfDue(progress, file)) { }
                Assert.AreEqual(JTokenType.Null, JObject.Parse(_http.Calls[2].Body)["waiting"]!.Type);
                Assert.IsFalse(NovaCaptureAgent.LeaseRefreshDue, "answered: nothing owed");
            }
            finally { File.Delete(file); }
        }

        [Test]
        public void AReviewNobodyCanFinishButThePerson_HasItsOwnSentence()
        {
            var s = TeachJob.ReviewWaitingSentence("show me the boss fight");
            StringAssert.Contains("show me the boss fight", s);
            StringAssert.Contains("Upload", s);
            StringAssert.Contains("Discard", s);
            Assert.AreNotEqual(TeachJob.WaitingSentence("show me the boss fight"), s);
        }

        /// <summary>Audit M2 — structural guard: a Review that did not decide by itself (the person must press Upload, Teach
        /// again or Discard) reports its own sentence; the loop top does not overwrite it.</summary>
        [Test]
        public void RunTeach_AReviewWaitingOnThePerson_ReportsIt()
        {
            var src = File.ReadAllText(Path.Combine(KitHygieneTests.PackageRoot(), "Editor", "Cloud", "NovaCaptureAgent.cs"));
            var start = src.IndexOf("private static IEnumerable RunTeach(", StringComparison.Ordinal);
            var loop = src.IndexOf("while (true)", start, StringComparison.Ordinal);
            StringAssert.Contains("if (s.Phase != TeachState.Review) _teachWaiting =", src.Substring(loop, 900));
            var review = src.IndexOf("case TeachState.Review:", loop, StringComparison.Ordinal);
            var set = src.IndexOf("TeachJob.ReviewWaitingSentence(ask)", review, StringComparison.Ordinal);
            Assert.Greater(set, review);
            Assert.Less(set - review, 2500, "set inside the Review case");
        }

        [Test]
        public void TheStartChecksAsk_StillWins_WhenBothAreSet()
        {
            NovaCaptureAgent.SetTeachWaitingForTests(TeachJob.WaitingSentence("x"));
            StartAsk.Window.Begin("Put the game on the lobby and press Continue.");
            Assert.AreEqual("Put the game on the lobby and press Continue.", NovaCaptureAgent.WaitingForPerson);
        }

        [Test]
        public void WhenTheRoutineEnds_TheTeachsAskIsCleared()
        {
            NovaCaptureAgent.InstallRoutineForTests(EndsAtOnce(), Run);
            NovaCaptureAgent.SetTeachWaitingForTests(TeachJob.WaitingSentence("x"));
            TickAt(1001);
            Assert.IsNull(NovaCaptureAgent.WaitingForPerson, "no routine, nobody waiting");
        }

        [Test]
        public void TheSentenceNamesTheAskAndTheButton()
        {
            var s = TeachJob.WaitingSentence("show me the boss fight");
            StringAssert.Contains("show me the boss fight", s);
            StringAssert.Contains("Teach", s);
        }

        /// <summary>A structural guard over RunTeach (it has no seam a pure test can drive): the teach's ask is set from its
        /// phase at the TOP of the loop, before the lease refresh — so leaving the Waiting phase clears it on the same tick.</summary>
        [Test]
        public void RunTeach_SetsItsAskFromThePhase_BeforeTheLeaseRefresh()
        {
            var src = File.ReadAllText(Path.Combine(KitHygieneTests.PackageRoot(), "Editor", "Cloud", "NovaCaptureAgent.cs"));
            var start = src.IndexOf("private static IEnumerable RunTeach(", StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0);
            var loop = src.IndexOf("while (true)", start, StringComparison.Ordinal);
            var set = src.IndexOf("_teachWaiting = s.Phase == TeachState.Waiting", loop, StringComparison.Ordinal);
            var refresh = src.IndexOf("RefreshLeaseIfDue(", loop, StringComparison.Ordinal);
            Assert.Greater(set, loop, "the teach's ask is set inside RunTeach's loop");
            Assert.Less(set, refresh, "…before the loop's first lease refresh");
            var body = src.Substring(src.IndexOf("internal static IEnumerable RefreshLeaseIfDue(", StringComparison.Ordinal), 900);
            StringAssert.Contains("WaitingForPerson", body, "the refresh sends what the accessor says, not StartAsk alone");
        }
    }
}
