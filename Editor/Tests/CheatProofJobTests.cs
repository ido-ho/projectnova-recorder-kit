using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace NovaFixtures.ProofGame
{
    public sealed class FixtureTimer
    {
        public float Seconds;
    }

    public sealed class FixturePlayer
    {
        public int Coins;
        public int Level { get; set; } = 1;
        public List<string> Items = new();
        public Dictionary<string, int> Stats = new() { ["hp"] = 10 };
        public FixtureTimer Timer = new();
        public FixturePlayer? Self; // a cycle: the snapshot walks it once
    }

    public sealed class FixtureSave
    {
        public static FixtureSave Instance = new();
        public FixturePlayer Player = new();

        public static void ResetForTests()
        {
            Instance = new FixtureSave();
            Instance.Player.Self = Instance.Player;
        }
    }

    /// <summary>GAP-KIT-1 — a login the game holds. Its own static root, NOT under <see cref="FixtureSave"/>, so the snapshot
    /// tests' counts do not move.</summary>
    public sealed class FixtureLogin
    {
        public static FixtureLogin Instance = new();
        public string AuthToken = "tok-SECRET-4242";
        public int Logins = 3;
        public FixtureProfile Profile = new();
    }

    public sealed class FixtureProfile
    {
        public int Level = 7;
        public string Nick = "kit";
    }

    /// <summary>GAP-KIT-1 — a static holder: a class keeping a key in a static field.</summary>
    public static class FixtureVault
    {
        public static string SessionKey = "sk-SECRET-99";
        public const string TokenHeader = "X-Token"; // a constant names a header, holds no secret: not a reason to refuse
        public static int Opens = 2;
    }

    public static class FixtureHeaders
    {
        public const string TokenHeader = "X-Token";
        public static int Sent = 5;
    }

    /// <summary>The fixture game's cheats — each named the way a game names them, none of them any game's.</summary>
    public static class FixtureCheats
    {
        public static int Calls;
        public static int PendingSwords;
        public static void AddSword()
        {
            Calls++;
            FixtureSave.Instance.Player.Items.Add("sword");
            FixtureSave.Instance.Player.Timer.Seconds += 1.5f; // an unrelated change the snapshot also sees
        }
        /// <summary>The server's answer arrives later — the test delivers it a few frames on.</summary>
        public static void AddSwordLater() { Calls++; PendingSwords++; }
        public static void DeliverPending()
        {
            while (PendingSwords > 0) { PendingSwords--; FixtureSave.Instance.Player.Items.Add("sword"); }
        }
        public static void DoNothing() => Calls++;
        public static void ResetEverything() { Calls++; FixtureSave.Instance.Player.Level = 1; }
    }
}

namespace ProjectNova.RecorderKit.Tests
{
    using NovaFixtures.ProofGame;

    /// <summary>
    /// v3 P3 (§3.3 step 3) — the <c>cheat-proof</c> job at its seam (<see cref="CheatProofRun.Run"/>, pumped with a fake
    /// clock), the snapshot that PROPOSES a check value, the plain-text UI read, and the window rule that ticks a check's
    /// read with its cheat. Everything runs through the kit job's gate on a real temp project; the cheats run through the
    /// kit's own bridge.
    /// </summary>
    public class CheatProofJobTests
    {
        private const string Items = "NovaFixtures.ProofGame.FixtureSave.Player.Items.Count";
        private const string AddSword = "call NovaFixtures.ProofGame.FixtureCheats.AddSword";
        private string _root = "";
        private double _clock;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "p3proof-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RelayPaths.NovaDir(_root));
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
            FixtureSave.ResetForTests();
            FixtureCheats.Calls = 0;
            FixtureCheats.PendingSwords = 0;
            _clock = 0;
        }

        [TearDown]
        public void TearDown()
        {
            LeverGateBridge.ForgetRefused();
            Levers.ForgetRowsForTests();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private void Tick(string lever) => Assert.IsNull(Levers.SetApproved(_root, lever, true));

        private static CheatProofRequest Proof(string command, JObject check, double settle = 1) =>
            CheatProofRequest.FromJson(new JObject { ["command"] = command, ["check"] = check, ["settleSec"] = settle })!;

        private static JObject GetCheck(string path, string expect = "increase") => new() { ["get"] = path, ["expect"] = expect };

        private CheatProofRun Pump(CheatProofRequest request, Action<int>? onFrame = null)
        {
            var run = new CheatProofRun(request);
            var frame = 0;
            foreach (var _ in CheatProofRun.Run(run, _root, new GenericCheatBridge(_root, new UguiDriver()), () => _clock))
            {
                _clock += 0.25;
                onFrame?.Invoke(frame++);
                Assert.Less(frame, 1000);
            }
            return run;
        }

        [Test]
        public void AnUntickedCheatIsNeverRunAndBecomesARow()
        {
            Tick(CheatCheck.GetLever(Items));
            var request = Proof(AddSword, GetCheck(Items));
            Assert.IsNotNull(CheatProofRun.LeverRefusalNow(_root, request));
            var run = Pump(request);
            Assert.IsFalse(run.Ran);
            Assert.AreEqual(0, FixtureCheats.Calls, "the cheat reached the game un-ticked");
            StringAssert.Contains(AddSword, run.LeverRefused!);
            CollectionAssert.Contains(LeverGateBridge.RefusedThisSession, AddSword);
        }

        [Test]
        public void TheCheckReadIsALeverTooAndAnUntickedOneStopsTheProofBeforeTheCheatRuns()
        {
            Tick(AddSword);
            var run = Pump(Proof(AddSword, GetCheck(Items)));
            Assert.IsFalse(run.Ran);
            Assert.AreEqual(0, FixtureCheats.Calls);
            StringAssert.Contains("get " + Items, run.LeverRefused!, "reading the value runs getters: it needs its own tick");
        }

        [Test]
        public void ATickedCheatIsRunOnceAndItsValueReadBeforeAndAfter()
        {
            Tick(AddSword);
            Tick(CheatCheck.GetLever(Items));
            var run = Pump(Proof(AddSword, GetCheck(Items)));
            Assert.IsTrue(run.Ran);
            Assert.IsTrue(run.RunOk);
            Assert.AreEqual(1, FixtureCheats.Calls, "run ONCE");
            Assert.AreEqual(0, run.Before!.Number);
            Assert.AreEqual(1, run.After!.Number);
            var facts = JObject.Parse(run.ToFactsJson("9.9.9"));
            Assert.AreEqual(0, facts["before"]!["number"]!.Value<double>());
            Assert.AreEqual(1, facts["after"]!["number"]!.Value<double>());
            Assert.AreEqual("increase", facts["check"]!["expect"]!.Value<string>());
            Assert.IsTrue(facts["ran"]!.Value<bool>());
        }

        [Test]
        public void TheAfterReadWaitsOutTheSettleTimeSoALateEffectIsSeen()
        {
            const string later = "call NovaFixtures.ProofGame.FixtureCheats.AddSwordLater";
            Tick(later);
            Tick(CheatCheck.GetLever(Items));
            // the "server" answers 2 s after the cheat ran (8 frames of 0.25 s); the settle wait is 3 s
            var ranAt = -1;
            var run = Pump(Proof(later, GetCheck(Items), settle: 3), frame =>
            {
                if (FixtureCheats.Calls == 1 && ranAt < 0) ranAt = frame;
                if (ranAt >= 0 && frame == ranAt + 8) FixtureCheats.DeliverPending();
            });
            Assert.AreEqual(1, run.After!.Number, "read after the late effect landed, not the moment the call returned");
            // kit 0.13.4 polls every second: the reads at 1 s and 2 s came before the answer (2.25 s), the next is the
            // wait's end — the last read, taken once the settle wait is over
            Assert.AreEqual("timeout", run.StoppedBy);
            Assert.GreaterOrEqual(run.SettledSec, 3);
        }

        [Test]
        public void ACheatThatChangesNothingSaysSoAndItsOkIsNotAProof()
        {
            const string nothing = "call NovaFixtures.ProofGame.FixtureCheats.DoNothing";
            Tick(nothing);
            Tick(CheatCheck.GetLever(Items));
            var run = Pump(Proof(nothing, GetCheck(Items)));
            Assert.IsTrue(run.RunOk, "the call said ok…");
            Assert.AreEqual(run.Before!.Number, run.After!.Number, "…and the value did not move: facts, for the website to grade");
        }

        [Test]
        public void ARiskyTickedCheatWaitsForTheNonProductionTick()
        {
            const string reset = "call NovaFixtures.ProofGame.FixtureCheats.ResetEverything";
            Tick(reset);
            Tick(CheatCheck.GetLever("NovaFixtures.ProofGame.FixtureSave.Player.Level"));
            var run = Pump(Proof(reset, GetCheck("NovaFixtures.ProofGame.FixtureSave.Player.Level", "decrease")));
            Assert.IsFalse(run.Ran);
            StringAssert.Contains(CheatRisk.NonProductionLabel, run.LeverRefused!);
            Assert.AreEqual(0, FixtureCheats.Calls);
        }

        /// <summary>The P3 review, M3: a before-read that FAILED leaves nothing to grade against — so the cheat is not run
        /// and the studio's game is left as it was. Positive control: the same proof on a path that reads runs once.</summary>
        [Test]
        public void ABeforeReadThatFailsStopsTheProofBeforeTheCheatRuns()
        {
            const string missing = "NovaFixtures.ProofGame.FixtureSave.Player.NoSuchMember";
            Tick(AddSword);
            Tick(CheatCheck.GetLever(missing));
            var request = Proof(AddSword, GetCheck(missing));
            Assert.IsNull(CheatProofRun.LeverRefusalNow(_root, request), "both ticked: the gate lets the proof start");
            var run = Pump(request);
            Assert.IsNotNull(run.Before!.Error, "the value could not be read before the cheat");
            Assert.IsFalse(run.Ran);
            Assert.AreEqual(0, FixtureCheats.Calls, "the cheat ran with nothing to grade it against");
            Assert.IsNull(run.After);
            Assert.IsNull(run.LeverRefused, "not a lever refusal — the read failed");
            Assert.IsTrue(run.Log.Any(l => l.Contains("the cheat was not run")), string.Join(" | ", run.Log));
            var facts = JObject.Parse(run.ToFactsJson("0.11.0"));
            Assert.IsFalse(facts["ran"]!.Value<bool>());
            Assert.IsNotNull(facts["before"]!["error"]!.Value<string>());
            // control: a path that reads — the same proof runs the cheat once
            Tick(CheatCheck.GetLever(Items));
            var ok = Pump(Proof(AddSword, GetCheck(Items)));
            Assert.IsTrue(ok.Ran);
            Assert.AreEqual(1, FixtureCheats.Calls);
        }

        /// <summary>The P3 review, M4: ONE rule for a label read — the proof's text/screen read is the <c>ui-text</c> lever,
        /// gated exactly as the <c>ui-text</c> verb is. Un-ticked, nothing runs; ticked, the labels are read.</summary>
        [Test]
        public void ALabelChecksReadIsTheUiTextLeverGatedLikeTheVerb()
        {
            var text = Proof(AddSword, new JObject { ["text"] = "Victory" });
            var screen = Proof(AddSword, new JObject { ["screen"] = "is the sword there?" });
            Assert.AreEqual(UiText.Verb, text.Check.Lever);
            Assert.AreEqual(UiText.Verb, screen.Check.Lever);
            Assert.IsFalse(Levers.IsReadOnly(UiText.Verb), "the verb is not one of the kit's three ungated verbs");
            // the verb, through the kit job's gate: refused un-ticked — the same gate the proof's read asks
            var log = new List<string>();
            Assert.IsFalse(KitJobRun.Gate(new KitFunctionBridge(_ => true), _root, log.Add).Run(UiText.Verb));
            Tick(AddSword);
            StringAssert.Contains(UiText.Verb, CheatProofRun.LeverRefusalNow(_root, text)!);
            var refused = Pump(text);
            Assert.IsFalse(refused.Ran);
            Assert.AreEqual(0, FixtureCheats.Calls);
            StringAssert.Contains(UiText.Verb, refused.LeverRefused!);
            // ticked: the verb passes the gate, and the proof reads the labels before and after and runs the cheat once
            Tick(UiText.Verb);
            Assert.IsTrue(KitJobRun.Gate(new KitFunctionBridge(_ => true), _root, log.Add).Run(UiText.Verb));
            var run = Pump(screen);
            Assert.IsTrue(run.Ran);
            Assert.AreEqual(1, FixtureCheats.Calls);
            Assert.IsNotNull(run.Before!.Labels);
            Assert.IsNotNull(run.After!.Labels);
            Assert.IsNull(run.Before.Error);
        }

        // ---- the snapshot: it PROPOSES, never proves ------------------------------------------------------------------

        [Test]
        public void ASnapshotReadsEveryNumberUnderTheRootAndItsDiffProposesPathsAGetCanReadBack()
        {
            const string root = "NovaFixtures.ProofGame.FixtureSave.Player";
            Tick(AddSword);
            Tick(CheatCheck.SnapshotLever(root));
            var run = Pump(Proof(AddSword, new JObject { ["snapshot"] = root }));
            Assert.IsTrue(run.Ran);
            var facts = JObject.Parse(run.ToFactsJson("1.0.0"));
            var changed = ((JArray)facts["changed"]!).Select(c => c["path"]!.Value<string>()!).ToList();
            CollectionAssert.Contains(changed, root + ".Items.Count");
            CollectionAssert.Contains(changed, root + ".Timer.Seconds",
                "an unrelated change is ALSO in the diff — which is why a snapshot only proposes (invariant 100)");
            Assert.IsFalse(changed.Contains(root + ".Coins"));
            foreach (var path in changed)
                Assert.IsTrue(GameReflection.TryGetPath(path, out _, out var err), $"{path}: a proposal a `get` cannot read back ({err})");
            Assert.Greater(facts["before"]!["leaves"]!.Value<int>(), 3);
        }

        [Test]
        public void TheSnapshotWalksACycleOnceAndCountsCollectionsWithoutOpeningThem()
        {
            var leaves = SnapshotRead.Read("NovaFixtures.ProofGame.FixtureSave.Player", out var error)!;
            Assert.IsNull(error);
            Assert.IsTrue(leaves.ContainsKey("NovaFixtures.ProofGame.FixtureSave.Player.Stats.Count"));
            Assert.IsTrue(leaves.ContainsKey("NovaFixtures.ProofGame.FixtureSave.Player.Level"));
            Assert.IsFalse(leaves.Keys.Any(k => k.Contains(".Self.Self.")), "the cycle is walked once");
            Assert.LessOrEqual(leaves.Count, SnapshotRead.MaxLeaves);
            Assert.IsNull(SnapshotRead.Read("No.Such.Root", out error));
            Assert.IsNotNull(error);
        }

        // ---- the check value: its reader, and the window's companion tick --------------------------------------------

        [Test]
        public void ATickListCandidatesCheckReadIsTickedWithItAndNothingElseIs()
        {
            var list = new JObject
            {
                ["$schemaVersion"] = 1,
                ["candidates"] = new JArray
                {
                    new JObject { ["command"] = AddSword, ["kind"] = "give", ["check"] = GetCheck(Items) },
                    new JObject { ["command"] = "raw ForceWin", ["check"] = new JObject { ["text"] = "Victory" } },
                    new JObject { ["command"] = "raw Plain" },
                },
            };
            File.WriteAllBytes(TickList.FilePath(_root), new UTF8Encoding(false).GetBytes(list.ToString()));
            CollectionAssert.AreEqual(new[] { "get " + Items }, Levers.CompanionTicks(_root, AddSword));
            CollectionAssert.AreEqual(new[] { UiText.Verb }, Levers.CompanionTicks(_root, "raw ForceWin"),
                "a label read runs the game's text getters: its lever is ui-text, ticked with the cheat (the P3 review, M4)");
            CollectionAssert.IsEmpty(Levers.CompanionTicks(_root, "raw Plain"));
            CollectionAssert.IsEmpty(Levers.CompanionTicks(_root, "raw Other"), "only the candidate of exactly this command");
            var row = Levers.Rows(_root, null).Single(r => r.Command == AddSword);
            StringAssert.Contains("checked by: get " + Items + " should increase", row.Note);
        }

        [Test]
        public void TheProofClaimIsReadByTheCheckReaderAndItsSettleIsBounded()
        {
            var claim = new JObject
            {
                ["job"] = new JObject { ["runId"] = "r", ["workspaceId"] = "ws12345678", ["kind"] = "cheat-proof", ["items"] = new JArray(), ["leaseSec"] = 600 },
                ["cheatProof"] = new JObject { ["command"] = AddSword, ["check"] = GetCheck(Items), ["settleSec"] = 999 },
            }.ToString();
            var p = CheatProofRequest.FromClaim(claim)!;
            Assert.AreEqual(AddSword, p.Command);
            Assert.AreEqual(CheatProofRequest.MaxSettleSec, p.SettleSec);
            Assert.IsNull(CheatProofRequest.FromJson(new JObject { ["command"] = AddSword, ["check"] = new JObject { ["get"] = Items } }),
                "a get check with no expectation is no check");
            Assert.IsTrue(CaptureJob.IsHandled(CaptureJob.CheatProofKind));
            Assert.IsNotNull(JobGuard.RefusalReason(CaptureJob.CheatProofKind, "some-game", null),
                "a proof runs a cheat in the game: admitted by the game's identity, like a try");

            var path = Path.Combine(_root, "progress.json");
            var progress = CaptureProgress.Start(CaptureJob.ParseClaim(claim, out _)!);
            progress.CheatProof = p;
            progress.Save(path);
            Assert.AreEqual(AddSword, CaptureProgress.Load(path)!.CheatProof!.Command, "it survives the Play Mode reload");
        }

        [Test]
        public void ThePlainTextUiReadListsEveryActiveLabelNotOnlyButtons()
        {
            var canvas = new GameObject("P3Canvas", typeof(Canvas));
            try
            {
                var label = new GameObject("VictoryLabel", typeof(RectTransform), typeof(UnityEngine.UI.Text));
                label.transform.SetParent(canvas.transform);
                label.GetComponent<UnityEngine.UI.Text>().text = "Victory!\nTap to go on";
                var hidden = new GameObject("HiddenLabel", typeof(RectTransform), typeof(UnityEngine.UI.Text));
                hidden.transform.SetParent(canvas.transform);
                hidden.GetComponent<UnityEngine.UI.Text>().text = "Secret";
                hidden.SetActive(false);

                var rows = UiText.Rows();
                CollectionAssert.Contains(rows, "VictoryLabel: \"Victory! Tap to go on\"");
                Assert.IsFalse(rows.Any(r => r.Contains("Secret")), "an inactive label is not on screen");
                Assert.IsTrue(UiText.Shows(rows, "victory"));
                Assert.IsFalse(ReflectionCheatBridge.OnScreenRows().Any(r => r.Contains("VictoryLabel")),
                    "control: ui-dump lists buttons only — why this read exists");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvas);
            }
        }

        // ---- GAP-KIT-1 (kit 0.14.3): a `get` whose value is sent never reads through a credential ----------------------

        private const string Login = "NovaFixtures.ProofGame.FixtureLogin.Instance";

        [Test]
        public void AGetCheckOnACredentialIsRefusedBeforeTheCheatRuns_AndItsValueNeverLeaves()
        {
            foreach (var path in new[] { Login + ".AuthToken", Login + ".Logins", Login + ".Profile.Level", "NovaFixtures.ProofGame.FixtureVault.Opens" })
            {
                TearDown();
                SetUp();
                Tick(AddSword);
                Tick(CheatCheck.GetLever(path));
                var run = Pump(Proof(AddSword, GetCheck(path, "change")));
                Assert.IsNotNull(run.Before!.Error, $"{path}: read through a credential");
                Assert.IsNull(run.Before.Value, path);
                Assert.IsFalse(run.Ran, path);
                Assert.AreEqual(0, FixtureCheats.Calls, $"{path}: the cheat ran");
                var facts = run.ToFactsJson("0.14.3");
                StringAssert.DoesNotContain("SECRET", facts, path);
                StringAssert.Contains("credential", run.Before.Error!, path);
            }
            // the `get` verb's own report is held to the same rule (it reaches the probe file and the director's log)
            Assert.IsFalse(SnapshotRead.TryGetForSending(Login, out var holder, out var why), "the login object itself renders its fields");
            Assert.IsNull(holder);
            StringAssert.Contains("credential", why);
            Assert.IsFalse(new GenericCheatBridge(_root, new UguiDriver()).Run("get " + Login + ".AuthToken"));
            // positive controls: a plain object's number, a constant-only static class, and the unfiltered read still reaches it
            Assert.IsTrue(SnapshotRead.TryGetForSending("NovaFixtures.ProofGame.FixtureHeaders.Sent", out var sent, out var e1), e1);
            Assert.AreEqual(5, sent);
            Assert.IsTrue(GameReflection.TryGetPath(Login + ".Logins", out var logins, out var e2), "control: the path itself reads — " + e2);
            Assert.AreEqual(3, logins);
            TearDown();
            SetUp();
            Tick(AddSword);
            Tick(CheatCheck.GetLever(Items));
            var ok = Pump(Proof(AddSword, GetCheck(Items)));
            Assert.IsTrue(ok.Ran);
            Assert.AreEqual(1, ok.After!.Number);
        }

        /// <summary>GAP-KIT-2 (kit 0.14.3) — the label rows are SENT: what a person typed and a credential-named label are not.</summary>
        [Test]
        public void TheLabelReadLeavesOutWhatAPersonTypedAndCredentialNamedLabels()
        {
            var canvas = new GameObject("GapKit2Canvas", typeof(Canvas));
            try
            {
                UnityEngine.UI.Text Label(string name, string text)
                {
                    var go = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Text));
                    go.transform.SetParent(canvas.transform);
                    var t = go.GetComponent<UnityEngine.UI.Text>();
                    t.text = text;
                    return t;
                }
                Label("ShopTitle", "Shop");
                Label("PasswordLabel", "hunter2");
                var field = new GameObject("EmailField", typeof(RectTransform), typeof(UnityEngine.UI.InputField));
                field.transform.SetParent(canvas.transform);
                var typedText = Label("Text", "me@example.com");
                typedText.transform.SetParent(field.transform);
                field.GetComponent<UnityEngine.UI.InputField>().textComponent = typedText;
                typedText.text = "me@example.com"; // the field may redraw its label when wired: what is on screen now
                Assert.AreEqual("me@example.com", typedText.text, "precondition: the typed text is on screen");
                Assert.IsTrue(typedText.gameObject.activeInHierarchy);

                var rows = UiText.Rows();
                CollectionAssert.Contains(rows, "ShopTitle: \"Shop\"", "control: an ordinary label is read");
                Assert.IsFalse(rows.Any(r => r.Contains("hunter2")), string.Join(" | ", rows));
                Assert.IsFalse(rows.Any(r => r.Contains("me@example.com")), string.Join(" | ", rows));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvas);
            }
        }

        /// <summary>GAP-KIT-1 (kit 0.14.3) — the director's watched `get` (a dismiss flag sends its before and after) is held to
        /// the same credential rule as the proof's check.</summary>
        [Test]
        public void ADirectorsWatchedGetNeverReadsACredential()
        {
            var token = Login + ".AuthToken";
            Tick(CheatCheck.GetLever(token));
            Tick(CheatCheck.GetLever(Items));
            var log = new List<string>();
            var read = AdDirector.WatchReader(_root, cloudContent: true, log.Add);
            Assert.IsNull(read(token), "a ticked watch of a token is still never read out");
            Assert.AreEqual("0", read(Items), "control: an ordinary watched value is read");
        }
    }
}
