using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Kit 0.14.0 (fixes 2b, 2c — the RL test 2026-09-28; the fresh audit's M1–M5, L5) — THE SHOT'S FIRST SCREEN, over a
    /// fake game: a lobby shot on a game that booted onto its board fails NOW in a sentence naming the missing press and
    /// what the screen showed; with a taught way back to the lobby it plays it once and starts; a shot whose first screen
    /// cannot be told runs as it always did. The try and the capture build the step through their own plans
    /// (<see cref="ProbePlan"/>, <see cref="BackOffer"/>) and reach the same outcome (invariant 101).
    /// </summary>
    public class FirstScreenTests
    {
        private double _clock;
        private string _root = "";

        private const string PlainAdapter = @"{ ""gameId"": ""g"" }";
        private const string LobbyShot = @"{ ""name"": ""lobby-pets"", ""baseline"": ""lobby"",
  ""steps"": [ { ""kind"": ""wait"", ""seconds"": 0.5 }, { ""kind"": ""click"", ""name"": ""Pet_Tab"" } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""CollectionTab_Button"" } }";
        private const string BackId = "r00000000000000bb";

        [SetUp]
        public void SetUp()
        {
            _clock = 0;
            _root = Path.Combine(Path.GetTempPath(), "first-screen-" + Guid.NewGuid().ToString("N"));
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

        // ---- the fake game --------------------------------------------------------------------------------------------

        private sealed class Probe : IUiProbe
        {
            private readonly FakeReplayScreen _s;
            public Probe(FakeReplayScreen s) => _s = s;
            public bool Exists(string name) => _s.Exists(name);
            public string? GetText(string name) => null;
            public bool IsInteractable(string name) => _s.Exists(name);
        }

        /// <summary>The board with a tutorial finger — RL's boot screen. <c>Home_Btn</c> goes to the lobby.</summary>
        private static FakeReplayScreen Board(bool homeWorks = true)
        {
            var s = new FakeReplayScreen();
            s.Roots.Add("Ui/Board");
            foreach (var n in new[] { "Roll_Btn", "TutorialFinger", "Home_Btn", "Settings_Btn", "Dice_1", "Dice_2", "Dice_3", "Dice_4", "Dice_5", "Chest" })
                s.Add(n);
            if (homeWorks)
                s.OnClick["Home_Btn"] = () =>
                {
                    s.Roots.Remove("Ui/Board");
                    s.Roots.Add("Ui/Lobby");
                    s.Elements.Clear();
                    s.Add("Lobby");
                    s.Add("Pet_Tab");
                    s.Add("PlayButton");
                };
            return s;
        }

        private static FakeReplayScreen LobbyScreen()
        {
            var s = new FakeReplayScreen();
            s.Roots.Add("Ui/Lobby");
            s.Add("Lobby");
            s.Add("Pet_Tab");
            s.Add("PlayButton");
            return s;
        }

        private static AdShot Load(string text)
        {
            var loaded = JsonShotLoader.LoadFrom(ProbePlan.DocumentFor(JObject.Parse(text)), remember: false);
            Assert.IsEmpty(loaded.Errors, string.Join(" · ", loaded.Errors));
            return loaded.Shots[0];
        }

        /// <summary>The way back as the box would send it (the only way a recipe reaches the kit).</summary>
        private static BackOffer Home(bool confirmed = true) => BackOffer.Of(true, BackClaim(confirmed: confirmed), null);
        /// <summary>The box can send a way back, but this game has none taught (<c>back: null</c>).</summary>
        private static BackOffer NoneTaught => BackOffer.Of(true, null, null);

        private FirstScreen Run(FirstScreen first, FakeReplayScreen s, double waitSec = 2)
        {
            var ticks = 0;
            foreach (var _ in first.Run(new Probe(s), null, s, () => s.Elements.Select(e => e.Name).ToList(), _ => true, () => _clock, waitSec))
            {
                _clock += 0.1;
                Assert.Less(++ticks, 100_000, "the first-screen step never finished");
            }
            Assert.IsTrue(first.Finished);
            return first;
        }

        // ---- 2b: the sentence ------------------------------------------------------------------------------------------

        [Test]
        public void ALobbyShotOnTheBoard_FailsNow_NamingThePressAndWhatTheScreenShowed_AndSaysWhatToDo()
        {
            var s = Board();
            var first = Run(FirstScreen.ForTake(Load(LobbyShot), null, new GameAdapter(), NoneTaught), s);
            Assert.IsFalse(first.ReadyMet);
            StringAssert.StartsWith("The game opened on a screen without Pet_Tab (it showed: Roll_Btn, TutorialFinger, Home_Btn, Settings_Btn, Dice_1, Dice_2, Dice_3, Dice_4)", first.Failure);
            StringAssert.DoesNotContain("Dice_5", first.Failure, "more than eight names listed");
            // the box offers a way back and none is taught: teach it
            StringAssert.EndsWith("— this shot starts in the lobby. " + FirstScreen.TeachTheWayBack, first.Failure);
            CollectionAssert.IsEmpty(s.Clicked, "the step pressed something");
            Assert.GreaterOrEqual(_clock, 2 + 0.5, "it did not wait out the wait and the shot's own leading wait before failing");

            // audit M4: a box that sends no `back` at all never promises a Teach it cannot deliver
            _clock = 0;
            var noOffer = Run(FirstScreen.ForTake(Load(LobbyShot), null, new GameAdapter(), BackOffer.None), Board());
            StringAssert.EndsWith("— this shot starts in the lobby. " + FirstScreen.StartOnTheLobby, noOffer.Failure);
            StringAssert.DoesNotContain("Show me once", noOffer.Failure);
        }

        [Test]
        public void PositiveControls_TheFirstPressOnScreen_OrTheShotsScreenUp_RunTheTakeAsToday()
        {
            var inLobby = Run(FirstScreen.ForTake(Load(LobbyShot), null, new GameAdapter(), NoneTaught), LobbyScreen());
            Assert.IsNull(inLobby.Failure, inLobby.Failure);
            Assert.IsFalse(inLobby.ReadyMet);
            var s = LobbyScreen();
            s.Add("CollectionTab_Button");
            _clock = 0;
            var up = Run(FirstScreen.ForTake(Load(LobbyShot), null, new GameAdapter(), NoneTaught), s);
            Assert.IsTrue(up.ReadyMet);
            Assert.IsNull(up.Failure);
            Assert.Less(_clock, 0.5, "it waited for a screen that was up");
        }

        [Test]
        public void MinTakeSec_IsReadOffTheClaim_OnlyPositiveNumbers_AbsentOrBrokenIsNone()
        {
            var d = CaptureStart.MinTakeFromClaimBody(@"{ ""minTakeSec"": { ""board-roll"": 6.5, ""pets"": 4, ""bad"": -1, ""word"": ""x"" } }");
            Assert.IsNotNull(d);
            Assert.AreEqual(6.5, d!["board-roll"]);
            Assert.AreEqual(4.0, d["pets"]);
            Assert.IsFalse(d.ContainsKey("bad"));
            Assert.IsFalse(d.ContainsKey("word"));
            Assert.IsNull(CaptureStart.MinTakeFromClaimBody(@"{ ""job"": {} }"));
            Assert.IsNull(CaptureStart.MinTakeFromClaimBody("not json"));
        }

        /// <summary>Plan v3.1 phase 6.2 — dials: the claim's per-shot values; only plain pairs survive.</summary>
        [Test]
        public void Bindings_AreReadOffTheClaim_PerShot_OnlyPlainPairs()
        {
            var b = CaptureStart.BindingsFromClaimBody(
                @"{ ""bindings"": { ""hero-intro"": { ""hero"": ""hero.draco"", ""level"": ""5"", ""bad name"": ""x"", ""tpl"": ""{v}"", ""num"": 5 }, ""empty"": {}, ""notobj"": ""x"" } }");
            Assert.IsNotNull(b);
            CollectionAssert.AreEquivalent(new[] { "hero-intro" }, b!.Keys);
            Assert.AreEqual("hero.draco", b["hero-intro"]["hero"]);
            Assert.AreEqual("5", b["hero-intro"]["level"]);
            Assert.AreEqual(2, b["hero-intro"].Count, "a spaced name, a braced value and a number are dropped, never guessed");
            Assert.IsNull(CaptureStart.BindingsFromClaimBody(@"{ ""job"": {} }"));
            Assert.IsNull(CaptureStart.BindingsFromClaimBody("not json"));
        }

        /// <summary>The shared binding cases (bindings.cases.json) — the API's cleanBindings runs the same list.</summary>
        [Test]
        public void Bindings_TheSharedCases()
        {
            var cases = (JArray)SharedFixture.Load("bindings.cases.json")["pairs"]!;
            Assert.Greater(cases.Count, 5);
            foreach (var c in cases)
            {
                var body = new JObject { ["bindings"] = new JObject { ["s"] = new JObject { [(string)c["name"]!] = (string)c["value"]! } } };
                var kept = CaptureStart.BindingsFromClaimBody(body.ToString()) != null;
                Assert.AreEqual((bool)c["keep"]!, kept, $"{c["name"]} = {c["value"]}");
            }
        }

        /// <summary>…and they reach the director, which binds the shot's placeholders with them.</summary>
        [Test]
        public void Bindings_SetOnTheDirector_AreTheOnesItBindsWith()
        {
            var shot = new AdShot("hero-intro", new[] { "stage-hero {hero}" }, new[] { AdStep.Wait(0.1) },
                WaitCondition.Present("X"), parameters: new[] { "hero" });
            var cheats = new List<string>();
            var adapter = new GameAdapter
            {
                GameId = "testgame",
                CheatBridge = new ListBridge(cheats),
                Shots = () => new[] { shot },
            };
            var clock = 0.0;
            var d = AdDirector.Run(adapter, new[] { shot }, new AdDirector.Options
            {
                AutoPump = false,
                MaxAttempts = 1,
                Recorder = new NullRecorder(),
                Now = () => clock,
                ReleaseCameraHold = () => { },
            })!;
            d.Bindings = new Dictionary<string, string> { ["hero"] = "hero.draco" };
            for (var i = 0; !d.IsFinished && i < 10_000; i++) { d.PumpOnce(); clock += 0.1; }
            CollectionAssert.Contains(cheats, "stage-hero hero.draco");
            AdDirector.Active?.Finish("test end");
        }

        private sealed class ListBridge : ICheatBridge
        {
            private readonly List<string> _into;
            public ListBridge(List<string> into) => _into = into;
            public bool Run(string command) { _into.Add(command); return true; }
        }

        private sealed class NullRecorder : IRecorderDriver
        {
            public string OutputDir { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nova-bind-" + System.Guid.NewGuid().ToString("N"));
            public bool IsRecording { get; private set; }
            public int OutputWidth => 0;
            public int OutputHeight => 0;
            public void Start(string clipName) { System.IO.Directory.CreateDirectory(OutputDir); IsRecording = true; }
            public void Stop() => IsRecording = false;
        }

        // ---- plan v3.1 phase 4.5 (fix list 30): the start, not the end ------------------------------------------------

        private const string PetsUntil = @"{ ""name"": ""lobby-pet-summon"", ""baseline"": ""lobby"",
  ""steps"": [ { ""kind"": ""click"", ""name"": ""Pet_Tab"", ""until"": { ""kind"": ""present"", ""name"": ""SummonTab_Button"" } } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""CollectionTab_Button"" } }";

        [Test]
        public void NoArm_TheShotsFirstPressOnScreen_StartsTheTakeAtOnce_NotAfterTheEndStateWait()
        {
            // RL 2026-09-30: the kit sat "waiting for Present 'CollectionTab_Button'" — the END state, which the shot's own
            // first press opens — for the whole ready wait before a take that could start at once
            var first = Run(FirstScreen.ForTake(Load(PetsUntil), null, new GameAdapter(), NoneTaught), LobbyScreen(), waitSec: 60);
            Assert.IsNull(first.Failure, first.Failure);
            Assert.IsTrue(first.StartSeen);
            Assert.IsFalse(first.ReadyMet);
            Assert.Less(_clock, 0.5, "it waited for the end state with the start on screen");
        }

        [Test]
        public void WithAnArm_TheWaitIsTheArm_AsBefore_Control()
        {
            var armed = PetsUntil.Replace(@"""steps"":", @"""arm"": { ""kind"": ""present"", ""name"": ""Lobby_Ready"" }, ""steps"":");
            var first = Run(FirstScreen.ForTake(Load(armed), null, new GameAdapter(), NoneTaught), LobbyScreen(), waitSec: 3);
            Assert.IsFalse(first.StartSeen);
            Assert.GreaterOrEqual(_clock, 3, "an explicit arm must still be waited for");
        }

        [Test]
        public void AShotsOwnLeadingWait_IsGraceBeforeItFails_ASlowBootStillStarts()
        {
            // audit M2: the director would have waited 5 s before its press — the step grants the same
            var slow = LobbyShot.Replace(@"""seconds"": 0.5", @"""seconds"": 5");
            var s = Board(homeWorks: false);
            s.OnQuery = () => { if (_clock >= 5.5 && !s.Elements.Any(e => e.Name == "Pet_Tab")) s.Add("Pet_Tab"); };
            var first = Run(FirstScreen.ForTake(Load(slow), null, new GameAdapter(), NoneTaught), s);
            Assert.IsNull(first.Failure, "a boot slower than the ready wait but within the shot's own wait was refused");
            // control: the same shot, the press never comes — it fails after the grace
            _clock = 0;
            var never = Run(FirstScreen.ForTake(Load(slow), null, new GameAdapter(), NoneTaught), Board(homeWorks: false));
            Assert.IsNotNull(never.Failure);
            Assert.GreaterOrEqual(_clock, 2 + 5);
            Assert.AreEqual(5.0, FirstScreen.FirstPressOf(Load(slow), out var grace) == null ? -1 : grace);
        }

        [Test]
        public void ABoardShot_SaysItCannotStartHere_WithoutAskingForTheLobby()
        {
            var board = LobbyShot.Replace(@"""baseline"": ""lobby"",", "");
            var first = Run(FirstScreen.ForTake(Load(board), null, new GameAdapter(), Home()), Board());
            StringAssert.Contains("the shot's first press is not on screen, so it cannot start here.", first.Failure);
            StringAssert.DoesNotContain("lobby", first.Failure);
            Assert.IsFalse(first.BackPlayed, "a way back to the lobby was played for a board shot");
        }

        [Test]
        public void AShotWhoseFirstScreenCannotBeTold_RunsAsItAlwaysDid()
        {
            var shots = new[]
            {
                // a setup cheat, a cheat before the press, a placeholder, no press at all
                LobbyShot.Replace(@"""steps"":", @"""setup"": [ ""OpenLobby"" ], ""steps"":"),
                LobbyShot.Replace(@"{ ""kind"": ""wait"", ""seconds"": 0.5 }", @"{ ""kind"": ""cheat"", ""command"": ""OpenLobby"" }"),
                LobbyShot.Replace(@"""name"": ""Pet_Tab""", @"""name"": ""{tab}_Tab""").Replace(@"""baseline""", @"""parameters"": [ ""tab"" ], ""baseline"""),
                LobbyShot.Replace(@"{ ""kind"": ""click"", ""name"": ""Pet_Tab"" }", @"{ ""kind"": ""wait"", ""seconds"": 1 }"),
                // audit M1: a click with an `until` that may already hold succeeds without its button; a hold on an absent
                // target succeeds
                LobbyShot.Replace(@"""name"": ""Pet_Tab"" }", @"""name"": ""Pet_Tab"", ""until"": { ""kind"": ""present"", ""name"": ""CollectionTab_Button"" } }"),
                LobbyShot.Replace(@"{ ""kind"": ""click"", ""name"": ""Pet_Tab"" }", @"{ ""kind"": ""hold"", ""name"": ""Pet_Tab"", ""seconds"": 1 }"),
                // audit M2: a waitFor before the press is the shot saying "the game will get there"
                LobbyShot.Replace(@"{ ""kind"": ""wait"", ""seconds"": 0.5 }", @"{ ""kind"": ""waitFor"", ""condition"": { ""kind"": ""absent"", ""name"": ""Loading"" }, ""timeout"": 30 }"),
            };
            foreach (var text in shots)
            {
                Assert.IsNull(FirstScreen.FirstPressOf(Load(text)), text);
                _clock = 0;
                var first = Run(FirstScreen.ForTake(Load(text), null, new GameAdapter(), Home()), Board());
                Assert.IsNull(first.Failure, text);
                Assert.IsFalse(first.BackPlayed, text);
            }
            foreach (var adapter in new[] { new GameAdapter { Recovery = new Moves() }, new GameAdapter { ReadyGate = new Navigates() } })
            {
                var first = Run(FirstScreen.ForTake(Load(LobbyShot), null, adapter, Home()), Board());
                Assert.IsNull(first.Failure);
                Assert.IsFalse(first.BackPlayed);
            }
            Assert.AreEqual("Pet_Tab", FirstScreen.FirstPressOf(Load(LobbyShot)), "control: the plain shot IS tellable");
        }

        private sealed class Moves : IRecoveryPolicy { public IEnumerable Recover(DirectorContext ctx) { yield break; } }
        private sealed class Navigates : IReadyGate { public IEnumerable WaitUntilReady(DirectorContext ctx) { yield break; } }

        // ---- 2c: the way back to the lobby ---------------------------------------------------------------------------

        [Test]
        public void WithAWayBackToTheLobby_ItIsPlayedOnce_AndTheTakeStarts()
        {
            var s = Board();
            var first = Run(FirstScreen.ForTake(Load(LobbyShot), null, new GameAdapter(), Home()), s);
            Assert.IsNull(first.Failure, first.Failure);
            Assert.IsTrue(first.BackPlayed);
            CollectionAssert.AreEqual(new[] { "Home_Btn" }, s.Clicked, "the way back pressed something else, or twice");
            Assert.IsTrue(s.Exists("Pet_Tab"));
            Assert.AreEqual(1, first.Log.Edges.List.Count, "the way back's press is an edge");
        }

        [Test]
        public void AWayBackThatDoesNotHelp_FailsSayingSo_AndDoesNotAskToTeachFromScratch()
        {
            var s = Board(homeWorks: false);
            var first = Run(FirstScreen.ForTake(Load(LobbyShot), null, new GameAdapter(), Home()), s);
            Assert.IsTrue(first.BackPlayed);
            StringAssert.Contains($"The way back to the lobby (recipe {BackId}) was played", first.Failure);
            StringAssert.Contains("Pet_Tab still was not on screen", first.Failure);
            StringAssert.DoesNotContain(FirstScreen.TeachTheWayBack, first.Failure);
            CollectionAssert.AreEqual(new[] { "Home_Btn" }, s.Clicked);

            var elsewhere = Board();
            elsewhere.Remove("Home_Btn");
            var notPlayed = Run(FirstScreen.ForTake(Load(LobbyShot), null, new GameAdapter(), Home()), elsewhere);
            Assert.IsFalse(notPlayed.BackPlayed);
            StringAssert.Contains("was not played: its first step is not on this screen either", notPlayed.Failure);
            CollectionAssert.IsEmpty(elsewhere.Clicked);
        }

        [Test]
        public void TheWayBackOnTheWire_IsOneConfirmedRecipeMadeForTheLobby_ABadOneIsDroppedWithItsReason_NeverARefusal()
        {
            Assert.IsNull(BackToLobby.Read(BackClaim()).Problem, BackToLobby.Read(BackClaim()).Problem);
            Assert.AreEqual(BackId, BackToLobby.Read(BackClaim()).Recipe!.Id);
            StringAssert.Contains("not made for the lobby", BackToLobby.Read(BackClaim(need: "equipment")).Problem);
            StringAssert.Contains("not confirmed", BackToLobby.Read(BackClaim(confirmed: false)).Problem);
            var badSha = BackToLobby.Read(new StartClaim(BackDoc(), new string('0', 64), new[] { BackId }, Array.Empty<string>())).Problem;
            StringAssert.Contains("is not the one the website sent", badSha);
            StringAssert.DoesNotContain("nothing was run", badSha, "a dropped way back refuses nothing");

            // a try: present but broken is NOT a refusal (audit M5)
            ProbeRequest? Parse(string back) =>
                ProbeRequest.FromJson(JObject.Parse(@"{ ""shot"": ""S"", ""adapter"": ""A"", ""shotSha256"": ""aa"", ""adapterSha256"": ""bb""" + back + " }"));
            Assert.IsFalse(Parse("")!.BackOffered, "no key: the box sends none");
            Assert.IsTrue(Parse(@", ""back"": null")!.BackOffered, "a null: the box can send one, none is taught");
            Assert.IsNull(Parse(@", ""back"": null")!.Back);
            var broken = Parse(@", ""back"": { ""recipes"": ""x"" }");
            Assert.IsNotNull(broken, "a broken way back refused the try");
            Assert.AreEqual(ProbeRequest.BackBrokenReason, broken!.BackBroken);
            Assert.AreEqual(ProbeRequest.BackBrokenReason, ProbeRequest.FromJson(broken.ToJson())!.BackBroken, "the reason did not survive the reload");
            var good = Parse(", \"back\": " + BackClaim().ToJson().ToString(Newtonsoft.Json.Formatting.None))!;
            Assert.AreEqual(BackClaim().Sha256, ProbeRequest.FromJson(good.ToJson())!.Back!.Sha256, "the way back did not survive the reload");
            Assert.IsNull(ProbeRequest.FromJson(Parse("")!.ToJson())!.ToJson()["back"], "a try without one persists the block it always did");

            var bad = new ProbeRequest(LobbyShot, PlainAdapter, SyncNova.Sha256OfText(LobbyShot), SyncNova.Sha256OfText(PlainAdapter),
                back: BackClaim(confirmed: false));
            var plan = ProbePlan.Prepare(_root, bad);
            Assert.IsNull(plan.Refusal, "an unusable way back refused the whole try");
            StringAssert.Contains("not confirmed", plan.Back.Problem);
            StringAssert.Contains("not used", plan.Back.LogLine);
            // a lobby shot that needs it says why
            var needs = Run(FirstScreen.ForTry(plan.Shot!, plan.Gate, new GameAdapter(), plan.Back), Board());
            StringAssert.Contains("The way back to the lobby could not be used (", needs.Failure);
            StringAssert.EndsWith(FirstScreen.StartOnTheLobby, needs.Failure);
            // a board shot on the board is untouched by it (it fails for its own reason, never for the way back)
            _clock = 0;
            var boardShot = LobbyShot.Replace(@"""baseline"": ""lobby"",", "");
            var boardFirst = Run(FirstScreen.ForTake(Load(boardShot), null, new GameAdapter(), plan.Back), Board());
            StringAssert.DoesNotContain("way back", boardFirst.Failure);

            // a recording: offered, broken said (never the job's refusal), surviving the reload
            var body = new JObject { ["back"] = BackClaim().ToJson() }.ToString();
            Assert.AreEqual(BackClaim().Sha256, CaptureStart.BackFromClaimBody(body, out var offered, out var noBreak)!.Sha256);
            Assert.IsTrue(offered);
            Assert.IsNull(noBreak);
            Assert.IsNull(CaptureStart.BackFromClaimBody("{}", out var notOffered, out _));
            Assert.IsFalse(notOffered);
            CaptureStart.BackFromClaimBody(@"{ ""back"": { ""recipes"": 5 } }", out _, out var isBroken);
            Assert.AreEqual(ProbeRequest.BackBrokenReason, isBroken);
            var job = CaptureJob.FromJson(JObject.Parse(@"{ ""runId"": ""r1"", ""kind"": ""capture"", ""items"": [ { ""shot"": ""s"", ""take"": 1 } ] }"), out _)!;
            var p = CaptureProgress.Start(job);
            p.Back = BackClaim();
            p.BackOffered = true;
            p.BackBroken = "x";
            var path = Path.Combine(_root, "progress.json");
            p.Save(path);
            var loaded = CaptureProgress.Load(path)!;
            Assert.AreEqual(BackClaim().Sha256, loaded.Back!.Sha256);
            Assert.IsTrue(loaded.BackOffered);
            Assert.AreEqual("x", loaded.BackBroken);
        }

        // ---- invariant 101: the try and the capture reach the same outcome ------------------------------------------

        [Test]
        public void TheTryAndTheCapture_BuildTheStepThroughTheirOwnPlans_AndReachTheSameOutcome()
        {
            foreach (var (label, withBack, homeWorks) in new[] { ("no way back", false, true), ("way back works", true, true), ("way back fails", true, false) })
            {
                var request = new ProbeRequest(LobbyShot, PlainAdapter, SyncNova.Sha256OfText(LobbyShot), SyncNova.Sha256OfText(PlainAdapter),
                    back: withBack ? BackClaim() : null, backOffered: true);
                var plan = ProbePlan.Prepare(_root, request);
                Assert.IsNull(plan.Refusal, plan.Refusal);
                var tryScreen = Board(homeWorks);
                _clock = 0;
                var tried = Run(FirstScreen.ForTry(plan.Shot!, plan.Gate, new GameAdapter(), plan.Back), tryScreen);

                var back = withBack ? BackOffer.Of(true, BackClaim(), null) : NoneTaught;
                var takeScreen = Board(homeWorks);
                _clock = 0;
                var taken = Run(FirstScreen.ForTake(Load(LobbyShot), null, new GameAdapter(), back), takeScreen);

                Assert.AreEqual(taken.Failure, tried.Failure, label);
                Assert.AreEqual(taken.BackPlayed, tried.BackPlayed, label);
                CollectionAssert.AreEqual(takeScreen.Clicked, tryScreen.Clicked, label);
                Assert.AreEqual(!homeWorks || !withBack, tried.Failure != null, label + ": " + tried.Failure);

                if (tried.Failure != null)
                {
                    var facts = ProbeFacts.OfTry(plan, null, new GameAdapter(), new[] { "Roll_Btn [Button]" }, 1280, 720, 1f, true, null, null, tried);
                    Assert.AreEqual(FirstScreen.FailedKind, facts["failedStepKind"]!.Value<string>(), label);
                    Assert.AreEqual(tried.Failure, facts["reason"]!.Value<string>(), label);
                    Assert.IsFalse(facts["reached"]!.Value<bool>(), label);
                    Assert.AreEqual(JTokenType.Null, facts["failedStep"]!.Type, label);
                }
            }
            var ok = ProbePlan.Prepare(_root, new ProbeRequest(LobbyShot, PlainAdapter, SyncNova.Sha256OfText(LobbyShot), SyncNova.Sha256OfText(PlainAdapter)));
            var fine = Run(FirstScreen.ForTry(ok.Shot!, ok.Gate, new GameAdapter(), ok.Back), LobbyScreen());
            var plainFacts = ProbeFacts.OfTry(ok, null, new GameAdapter(), null, 1, 1, 1f, true, null, null, fine);
            Assert.AreNotEqual(FirstScreen.FailedKind, plainFacts["failedStepKind"]?.Value<string>());
        }

        [Test]
        public void WithATutorialGate_EitherFirstPressLetsTheTakeStart_AndTheTryWaitsForTheGatesScreen()
        {
            const string gate = @"{ ""name"": ""tutorial-gate"", ""steps"": [ { ""kind"": ""click"", ""name"": ""Skip_Btn"" } ],
  ""settle"": { ""kind"": ""present"", ""name"": ""Lobby"" } }";
            var claim = CaptureGateClaim.FromJson(new ProbeGate(gate, SyncNova.Sha256OfText(gate)).ToJson());
            var planned = CaptureGatePlan.Prepare(claim)!;
            Assert.IsNull(planned.Refusal, planned.Refusal);
            var onBoard = Run(FirstScreen.ForTake(Load(LobbyShot), planned.Gate, new GameAdapter(), NoneTaught), Board());
            StringAssert.Contains("without Skip_Btn or Pet_Tab", onBoard.Failure);
            var tutorial = Board();
            tutorial.Add("Skip_Btn");
            Assert.IsNull(Run(FirstScreen.ForTake(Load(LobbyShot), planned.Gate, new GameAdapter(), NoneTaught), tutorial).Failure,
                "the tutorial's own press is up: the gate starts the take");

            // audit M3: the shot's own settle holding at boot must not end a TRY's wait while a gate exists (the take's rule
            // accepts either screen — documented in invariant 179 as the capture's known risk)
            var settled = LobbyScreen();
            settled.Remove("Lobby"); // the gate's screen (Present Lobby) is not up
            settled.Add("CollectionTab_Button");
            _clock = 0;
            var tryFirst = Run(FirstScreen.ForTry(Load(LobbyShot), planned.Gate, new GameAdapter(), NoneTaught), settled);
            Assert.IsFalse(tryFirst.ReadyMet, "the try started on the shot's screen before its tutorial gate's");
            Assert.AreEqual(1, tryFirst.Ready.Screens.Count);
            _clock = 0;
            var takeFirst = Run(FirstScreen.ForTake(Load(LobbyShot), planned.Gate, new GameAdapter(), NoneTaught), settled);
            Assert.IsTrue(takeFirst.ReadyMet, "control: the take's rule accepts the shot's screen");
        }

        // ---- kit 0.14.1 (invariant 187): the start check — the try and the capture, the same way (invariant 101) ---------

        private FirstScreen RunChecked(FirstScreen first, FakeReplayScreen s, string? home, IStartAsk? ask, Func<string, bool> cheat)
        {
            first.CheckStart = true;
            first.HomeCheat = home;
            first.Ask = ask;
            var ticks = 0;
            foreach (var _ in first.Run(new Probe(s), null, s, () => s.Elements.Select(e => e.Name).ToList(), cheat, () => _clock, 2))
            {
                _clock += 0.1;
                Assert.Less(++ticks, 100_000, "the first-screen step never finished");
            }
            Assert.IsTrue(first.Finished);
            return first;
        }

        [Test]
        public void TheStartCheck_TheTryAndTheCapture_PutTheGameOnItsStartScreen_TheSameWay_LeverThenPersonThenOneSentence()
        {
            foreach (var (label, home, person) in new[] { ("the go-home lever", (string?)"go-home", false), ("the person", null, true), ("nobody", null, false) })
            {
                FirstScreen One(bool isTry, out FakeReplayScreen screen, out FakeAsk ask, out List<string> ran)
                {
                    var s = Board();
                    var goHome = s.OnClick["Home_Btn"];
                    var runs = new List<string>();
                    var a = new FakeAsk { Person = person ? goHome : null };
                    bool Cheat(string c) { runs.Add(c); if (c == "go-home") goHome(); return true; }
                    _clock = 0;
                    FirstScreen fs;
                    if (isTry)
                    {
                        var plan = ProbePlan.Prepare(_root, new ProbeRequest(LobbyShot, PlainAdapter, SyncNova.Sha256OfText(LobbyShot), SyncNova.Sha256OfText(PlainAdapter), backOffered: true));
                        Assert.IsNull(plan.Refusal, plan.Refusal);
                        fs = RunChecked(FirstScreen.ForTry(plan.Shot!, plan.Gate, new GameAdapter(), plan.Back), s, home, a, Cheat);
                    }
                    else fs = RunChecked(FirstScreen.ForTake(Load(LobbyShot), null, new GameAdapter(), NoneTaught), s, home, a, Cheat);
                    screen = s;
                    ask = a;
                    ran = runs;
                    return fs;
                }
                var tried = One(true, out var tryScreen, out var tryAsk, out var tryRan);
                var taken = One(false, out var takeScreen, out var takeAsk, out var takeRan);
                Assert.AreEqual(taken.Failure, tried.Failure, label);
                CollectionAssert.AreEqual(takeRan, tryRan, label);
                CollectionAssert.AreEqual(takeAsk.Asked, tryAsk.Asked, label);
                CollectionAssert.AreEqual(takeScreen.Clicked, tryScreen.Clicked, label);
                if (home != null)
                {
                    Assert.IsNull(tried.Failure, label + ": " + tried.Failure);
                    CollectionAssert.AreEqual(new[] { "go-home" }, tryRan, label);
                    CollectionAssert.IsEmpty(tryAsk.Asked, label + ": asked although the lever brought it home");
                }
                else if (person)
                {
                    Assert.IsNull(tried.Failure, label + ": " + tried.Failure);
                    CollectionAssert.AreEqual(new[] { "Put the game on the lobby and press Continue." }, tryAsk.Asked, label);
                    Assert.IsTrue(tryAsk.Ended, label);
                }
                else
                {
                    StringAssert.Contains("— this shot starts in the lobby. " + FirstScreen.TeachTheWayBack, tried.Failure, label);
                    StringAssert.EndsWith(StartCheck.StoppedSentence, tried.Failure, label);
                }
            }
        }

        [Test]
        public void TheStartCheck_ABoardShot_NeverRunsTheGoHomeLever_AndAsksForTheScreenWithItsFirstPress()
        {
            var boardShot = LobbyShot.Replace(@"""baseline"": ""lobby"",", "");
            var s = LobbyScreen();
            s.Remove("Pet_Tab"); // the shot's first press is on another screen
            var ran = new List<string>();
            var ask = new FakeAsk { Person = () => s.Add("Pet_Tab") };
            var first = RunChecked(FirstScreen.ForTake(Load(boardShot), null, new GameAdapter(), Home()), s, "go-home", ask, c => { ran.Add(c); return true; });
            Assert.IsNull(first.Failure, first.Failure);
            CollectionAssert.IsEmpty(ran, "the go-home lever ran for a shot that does not start at home");
            CollectionAssert.AreEqual(new[] { "Put the game on the screen this shot starts on and press Continue." }, ask.Asked);
            // control: the check off (a kit job that does not ask) fails as before, pressing nothing
            _clock = 0;
            var s2 = LobbyScreen();
            s2.Remove("Pet_Tab");
            var off = Run(FirstScreen.ForTake(Load(boardShot), null, new GameAdapter(), Home()), s2);
            StringAssert.EndsWith("the shot's first press is not on screen, so it cannot start here.", off.Failure);
        }

        // ---- the wire's recipe document ------------------------------------------------------------------------------

        private static string BackDoc(string need = "lobby", bool confirmed = true) => new JObject
        {
            ["schemaVersion"] = 1,
            ["gameId"] = "g",
            ["updatedAt"] = "t",
            ["recipes"] = new JArray(new JObject
            {
                ["id"] = BackId,
                ["ask"] = "show me the way back to the lobby",
                ["learnedBy"] = "teach",
                ["runId"] = "run-back",
                ["taughtAt"] = "2026-09-28T10:00:00.000Z",
                ["declaredStart"] = new JObject { ["kind"] = "lobby-after-boot" },
                ["stamp"] = new JObject { ["gameCommit"] = null, ["kitVersion"] = "0.14.0" },
                ["steps"] = new JArray(new JObject { ["kind"] = "click", ["name"] = "Home_Btn" }),
                ["dismiss"] = new JArray(),
                ["arrival"] = new JArray(),
                ["confirm"] = new JObject { ["state"] = confirmed ? "confirmed" : "unconfirmed", ["why"] = null, ["replayRan"] = true },
                ["screens"] = new JArray(),
                ["thumbnailKey"] = null,
                ["need"] = new JObject { ["kind"] = "screen", ["name"] = need },
            }),
        }.ToString(Newtonsoft.Json.Formatting.None);

        private static StartClaim BackClaim(string need = "lobby", bool confirmed = true)
        {
            var doc = BackDoc(need, confirmed);
            return new StartClaim(doc, SyncNova.Sha256OfText(doc), new[] { BackId }, Array.Empty<string>());
        }
    }
}
