using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// v3 P4 (§3.4) — THE TEACH RECORDER, which had no tests (spike B). The UI is built in code in the edit-mode scene,
    /// the way <c>DumpMarkerTests</c> builds it; the raycast is the one piece not run — the tests hand the recorder the
    /// object a raycast would have hit (<see cref="TeachRecorder.OnPointerDown"/> / <c>OnPointerUp</c>), which is the
    /// "fake EventSystem": everything from the hit on is the production code. Every name carries a <c>Tr</c> prefix and
    /// the screen is read over this test's own canvas only.
    /// </summary>
    public class TeachRecorderTests
    {
        private readonly List<GameObject> _made = new();
        private Canvas _canvas = null!;
        private double _clock;

        [SetUp]
        public void SetUp()
        {
            TeachRecorder.ResetForTests();
            _clock = 100;
            _canvas = Root("TrCanvas").GetComponent<Canvas>();
            TeachRecorder.Observe = () => ScreenSignature.Observe(new[] { _canvas });
            TeachRecorder.Now = () => _clock;
        }

        [TearDown]
        public void TearDown()
        {
            TeachRecorder.ResetForTests();
            foreach (var go in _made)
                if (go != null) Object.DestroyImmediate(go);
            _made.Clear();
        }

        private GameObject Root(string name)
        {
            var go = new GameObject(name, typeof(Canvas));
            _made.Add(go);
            return go;
        }

        private static GameObject Child(GameObject parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        private static GameObject ButtonOn(GameObject parent, string name, string? label = null)
        {
            var go = Child(parent, name);
            go.AddComponent<Image>();
            go.AddComponent<Button>();
            if (label != null)
            {
                var l = Child(go, "TrLabel");
                l.AddComponent<Text>().text = label;
            }
            return go;
        }

        private sealed class DownOnly : MonoBehaviour, IPointerDownHandler
        {
            public void OnPointerDown(PointerEventData eventData) { }
        }

        private static readonly Vector2 Screen1080 = new(1920, 1080);

        private void Press(GameObject? hit, Vector2 at, double seconds = 0.1, GameObject? upHit = null, Vector2? upAt = null)
        {
            TeachRecorder.OnPointerDown(hit, at, Screen1080);
            _clock += seconds;
            TeachRecorder.OnPointerUp(upHit ?? hit, upAt ?? at);
        }

        private void Settle()
        {
            _clock += TeachRecorder.SettleSec + 0.01;
            TeachRecorder.Tick();
        }

        // ---- recording only between Teach and Stop --------------------------------------------------------------------

        [Test]
        public void NothingIsRecorded_BeforeStart_OrAfterStop()
        {
            var lobby = Child(_canvas.gameObject, "TrLobby");
            var play = ButtonOn(lobby, "TrPlay", "Play");
            Press(play, new Vector2(10, 10));
            TeachRecorder.NoteCheat("give-gold", true);
            Assert.IsFalse(TeachRecorder.IsTeaching);
            Assert.AreEqual(0, TeachRecorder.Presses.Count, "a press before Start is not recorded");

            Assert.IsNull(TeachRecorder.Begin(null));
            Press(play, new Vector2(10, 10));
            Assert.IsTrue(TeachRecorder.Stop());
            Press(play, new Vector2(10, 10));
            TeachRecorder.NoteCheat("give-gold", true);
            Assert.AreEqual(1, TeachRecorder.Presses.Count, "only the press between Start and Stop is kept");
            Assert.IsFalse(TeachRecorder.Stop(), "Stop when not teaching says so");
        }

        // ---- kit 0.14.0 (fix 5a): the screen at Stop ------------------------------------------------------------------

        [Test]
        public void Stop_TakesTheStopScreen_MeasuredAgainstTheLastGoalPress_SoAScreenThatBuildsLateIsTheArrival()
        {
            var lobby = Child(_canvas.gameObject, "TrLobby");
            var play = ButtonOn(lobby, "TrPlay", "Play");
            Assert.IsNull(TeachRecorder.Begin(null));
            Assert.IsNull(TeachRecorder.StopJson, "no Stop screen before a Stop");
            Press(play, new Vector2(10, 10));
            // one beat later only the board is up (RL: PlayButton → the board)
            lobby.SetActive(false);
            Child(_canvas.gameObject, "TrBoard");
            Settle();
            // seconds later the fight builds itself — and a popup rises, which the person closes (a trailing dismiss)
            var fight = Child(_canvas.gameObject, "TrFight");
            Child(fight, "TrFightHud");
            Child(fight, "TrEnemyHp");
            var popup = Child(_canvas.gameObject, "TrPopup");
            var close = ButtonOn(popup, "TrClose");
            _clock += 3;
            TeachRecorder.OnPointerDown(close, new Vector2(20, 20), Screen1080);
            TeachRecorder.OnPointerUp(close, new Vector2(20, 20));
            popup.SetActive(false); // the game closes it; the close's "after" is owed and taken at Stop
            Assert.IsTrue(TeachRecorder.Stop());

            var presses = TeachRecorder.PressesJson();
            Assert.AreEqual(2, presses.Count);
            Assert.IsTrue(TeachAnalysis.IsDismiss((JObject)presses[1]), "precondition: the close is a dismiss");
            CollectionAssert.DoesNotContain(presses[0]!["after"]!["added"]!.Values<string>().ToList(), "TrFightHud",
                "precondition: the fight was not up one beat after the press");
            var stop = TeachRecorder.StopJson!;
            // measured against TrPlay's own before (the last GOAL press), not the dismiss's: the board AND the fight
            CollectionAssert.IsSubsetOf(new[] { "TrBoard", "TrEnemyHp", "TrFight", "TrFightHud" }, stop["added"]!.Values<string>().ToList());
            CollectionAssert.DoesNotContain(stop["added"]!.Values<string>().ToList(), "TrPopup", "the closed popup is not on the Stop screen");
            CollectionAssert.AreEquivalent(new[] { "TrCanvas/TrBoard", "TrCanvas/TrFight" }, stop["roots"]!.Values<string>().ToList());
            Assert.AreEqual(JTokenType.Null, stop["thumbnail"]!.Type, "no probe, no picture — and none is named");
            // audit H1: how long after the last GOAL press (TrPlay, at 0 s) the person stopped — not after the dismiss
            Assert.AreEqual(4.11, stop["afterLastGoalSec"]!.Value<double>(), 0.011);
            // the analysis reads the arrival from it
            var a = TeachAnalysis.Analyse(presses, new[] { "TrFightHud", "TrEnemyHp" }, null, new JObject { ["kind"] = "lobby-after-boot" }, stop);
            CollectionAssert.AreEqual(new[] { "TrEnemyHp", "TrFightHud" }, a.TeachArrival);
            var before = TeachAnalysis.Analyse(presses, new[] { "TrFightHud", "TrEnemyHp" }, null, new JObject { ["kind"] = "lobby-after-boot" });
            CollectionAssert.IsEmpty(before.TeachArrival, "control: without the Stop screen the late fight is not the arrival");
            // a new teach forgets it
            Assert.IsNull(TeachRecorder.Begin(null));
            Assert.IsNull(TeachRecorder.StopJson);
            TeachRecorder.Stop();
        }

        [Test]
        public void AStopAtTheTimeLimit_IsMarked_AndItsScreenIsNeverTheArrival()
        {
            // audit L4
            var lobby = Child(_canvas.gameObject, "TrLobby");
            var play = ButtonOn(lobby, "TrPlay", "Play");
            Assert.IsNull(TeachRecorder.Begin(null));
            Press(play, new Vector2(10, 10));
            Settle();
            Child(Child(_canvas.gameObject, "TrIdle"), "TrIdleHud");
            Assert.IsTrue(TeachRecorder.Stop(timeLimit: true));
            var stop = TeachRecorder.StopJson!;
            Assert.IsTrue(stop["timeLimit"]!.Value<bool>());
            var a = TeachAnalysis.Analyse(TeachRecorder.PressesJson(), new[] { "TrIdle", "TrIdleHud" }, null,
                new JObject { ["kind"] = "lobby-after-boot" }, stop);
            Assert.IsFalse(a.ArrivalFromStop);
            CollectionAssert.IsEmpty(a.TeachArrival, "the idle screen at the time limit was taken as the arrival");
            StringAssert.Contains(TeachAnalysis.TimeLimitWhy, a.Why);
            // control: the same screen at a person's Stop IS the arrival
            stop["timeLimit"] = false;
            var b = TeachAnalysis.Analyse(TeachRecorder.PressesJson(), new[] { "TrIdle", "TrIdleHud" }, null,
                new JObject { ["kind"] = "lobby-after-boot" }, stop);
            CollectionAssert.AreEqual(new[] { "TrIdle", "TrIdleHud" }, b.TeachArrival);
        }

        [Test]
        public void ABusyScreenPastTheCap_KeepsTheAuthoredNamesFirst()
        {
            // audit L3: 400 new names, the only authored one last in the alphabet
            var before = new ScreenSignature.Observation();
            var after = new ScreenSignature.Observation();
            for (var i = 0; i < 400; i++) after.Names.Add("A_Clone_" + i.ToString("D3"));
            after.Names.Add("Zz_FightHud");
            var plain = ScreenSignature.Added(before, after);
            Assert.AreEqual(ScreenSignature.MaxAdded, plain.Count);
            CollectionAssert.DoesNotContain(plain, "Zz_FightHud", "precondition: the alphabetical cut loses it");
            var kept = ScreenSignature.Added(before, after, new HashSet<string> { "Zz_FightHud" });
            Assert.AreEqual(ScreenSignature.MaxAdded, kept.Count);
            CollectionAssert.Contains(kept, "Zz_FightHud");
            CollectionAssert.AreEqual(kept.OrderBy(n => n, System.StringComparer.Ordinal).ToList(), kept, "still sorted");
            // the recorder hands the job's authored names on (Begin's second argument) to the Stop screen
            var lobby = Child(_canvas.gameObject, "TrLobby");
            var play = ButtonOn(lobby, "TrPlay", "Play");
            Assert.IsNull(TeachRecorder.Begin(null, new HashSet<string> { "TrZzHud" }));
            Press(play, new Vector2(10, 10));
            var busy = Child(_canvas.gameObject, "TrBusy");
            for (var i = 0; i < 350; i++) Child(busy, "TrA" + i.ToString("D3"));
            Child(busy, "TrZzHud");
            TeachRecorder.Stop();
            CollectionAssert.Contains(TeachRecorder.StopJson!["added"]!.Values<string>().ToList(), "TrZzHud");
        }

        [Test]
        public void LeftoverPictureHosts_AreSwept()
        {
            // audit L2: a host whose one frame never came (Play Mode ended) — HideAndDontSave, found by its marker
            var host = new GameObject("[Nova Teach picture]") { hideFlags = HideFlags.HideAndDontSave };
            host.AddComponent<TeachShutter>();
            var probe = new GameObject("[Nova Teach]") { hideFlags = HideFlags.HideAndDontSave };
            probe.AddComponent<TeachProbe>();
            Assert.GreaterOrEqual(TeachThumbnail.SweepLeftoverHosts(), 2);
            Assert.IsTrue(host == null, "the picture host leaked");
            Assert.IsTrue(probe == null, "the stopped teach's probe leaked");
            Assert.AreEqual(0, TeachThumbnail.SweepLeftoverHosts(), "a second sweep found more");
        }

        [Test]
        public void Start_OutsidePlayMode_IsRefusedByName()
        {
            StringAssert.Contains("Play Mode", TeachRecorder.Start());
            Assert.IsFalse(TeachRecorder.IsTeaching);
        }

        // ---- what fired ---------------------------------------------------------------------------------------------

        [Test]
        public void APressOnAButtonsChild_RecordsTheButtonUnityWouldClick_WithItsThreePartIdentityAndRoot()
        {
            var lobby = Child(_canvas.gameObject, "TrLobby");
            var equip = ButtonOn(lobby, "TrEquip", "Equipment");
            TeachRecorder.Begin(null);
            Press(equip.transform.Find("TrLabel").gameObject, new Vector2(960, 540));
            var p = TeachRecorder.Presses.Single();
            Assert.AreEqual("click", p.Fired);
            Assert.AreEqual("TrEquip", p.Selector);
            Assert.AreEqual("Button", p.HandlerKind);
            Assert.AreEqual("TrLabel", p.TopHit);
            Assert.AreEqual("TrEquip", p.Element!.Name);
            Assert.AreEqual("Equipment", p.Element.Label);
            Assert.IsNull(p.Element.Handler, "no persistent call is wired in code-built UI");
            Assert.AreEqual("TrCanvas/TrLobby", p.PressRoot);
            Assert.IsFalse(p.Shared);
        }

        [Test]
        public void ASharedName_IsNamedByItsUniqueLabel_AndATwinWithTheSameLabelIsMarkedShared()
        {
            var lobby = Child(_canvas.gameObject, "TrLobby");
            ButtonOn(lobby, "TrBtn", "Play");
            var shop = ButtonOn(lobby, "TrBtn", "Shop");
            TeachRecorder.Begin(null);
            Press(shop, Vector2.zero);
            Assert.AreEqual("TrBtn@Shop", TeachRecorder.Presses[0].Selector);
            Assert.IsFalse(TeachRecorder.Presses[0].Shared);

            var twin = ButtonOn(lobby, "TrBtn", "Shop");
            Press(twin, Vector2.zero);
            Assert.AreEqual("TrBtn", TeachRecorder.Presses[1].Selector);
            Assert.IsTrue(TeachRecorder.Presses[1].Shared, "the label no longer tells them apart — a replay could press the other");
        }

        [Test]
        public void AClickReleasedOverAnotherHandler_FiredNothing_AndIsDropped()
        {
            var lobby = Child(_canvas.gameObject, "TrLobby");
            var a = ButtonOn(lobby, "TrA");
            var b = ButtonOn(lobby, "TrB");
            TeachRecorder.Begin(null);
            Press(a, new Vector2(100, 100), upHit: b, upAt: new Vector2(102, 101));
            Assert.AreEqual(0, TeachRecorder.Presses.Count);
            Assert.AreEqual(1, TeachRecorder.Cancelled);
        }

        [Test]
        public void APointerThatMovedPastTheThreshold_IsADrag_KeptByItsNormalisedPoints_NeverAName()
        {
            var lobby = Child(_canvas.gameObject, "TrLobby");
            var list = ButtonOn(lobby, "TrList");
            TeachRecorder.Begin(null);
            Press(list, new Vector2(960, 540), seconds: 0.4, upAt: new Vector2(960, 140));
            var p = TeachRecorder.Presses.Single();
            Assert.AreEqual("drag", p.Fired);
            Assert.IsNull(p.Selector);
            var j = TeachRecorder.PressJson(p);
            Assert.AreEqual(0.5, j["x"]!.Value<double>(), 1e-4);
            Assert.AreEqual(0.5, j["y"]!.Value<double>(), 1e-4);
            Assert.AreEqual(0.5, j["x2"]!.Value<double>(), 1e-4);
            Assert.AreEqual(140.0 / 1080, j["y2"]!.Value<double>(), 1e-4);
            Assert.AreEqual(0.4, j["seconds"]!.Value<double>(), 1e-6);
        }

        [Test]
        public void APressOnTheWorld_IsATapAt_AndALongOneIsAHoldAtAPoint()
        {
            TeachRecorder.Begin(null);
            Press(null, new Vector2(480, 270));
            Press(null, new Vector2(480, 270), seconds: 1.2);
            Assert.AreEqual("tap-at", TeachRecorder.Presses[0].Fired);
            Assert.IsNull(TeachRecorder.Presses[0].Selector);
            Assert.AreEqual(0.25, TeachRecorder.PressJson(TeachRecorder.Presses[0])["x"]!.Value<double>(), 1e-4);
            Assert.AreEqual("hold", TeachRecorder.Presses[1].Fired);
            Assert.IsNull(TeachRecorder.Presses[1].Selector);
            Assert.AreEqual(1.2, TeachRecorder.PressJson(TeachRecorder.Presses[1])["seconds"]!.Value<double>(), 1e-6);
        }

        [Test]
        public void AUiGraphicThatTakesNoPress_IsAPressOnTheWorldBehindIt()
        {
            var backdrop = Child(_canvas.gameObject, "TrBackdrop");
            backdrop.AddComponent<Image>();
            TeachRecorder.Begin(null);
            Press(backdrop, new Vector2(10, 10));
            Assert.AreEqual("tap-at", TeachRecorder.Presses.Single().Fired);
            Assert.AreEqual("TrBackdrop", TeachRecorder.Presses.Single().TopHit);
        }

        [Test]
        public void AContainerWhoseButtonIsAChild_FiredInvokeButton_OnTheContainersName()
        {
            var lobby = Child(_canvas.gameObject, "TrLobby");
            var widget = Child(lobby, "TrWidget");
            widget.AddComponent<Image>();
            ButtonOn(widget, "TrInner");
            TeachRecorder.Begin(null);
            Press(widget, Vector2.zero);
            var p = TeachRecorder.Presses.Single();
            Assert.AreEqual("invoke-button", p.Fired);
            Assert.AreEqual("TrWidget", p.Selector);
            Assert.AreEqual("Button (child)", p.HandlerKind);
        }

        [Test]
        public void AnElementThatTakesADownButNoClick_IsAHold_WithItsSeconds()
        {
            var lobby = Child(_canvas.gameObject, "TrLobby");
            var charge = Child(lobby, "TrCharge");
            charge.AddComponent<DownOnly>();
            TeachRecorder.Begin(null);
            Press(charge, Vector2.zero, seconds: 2.0);
            var p = TeachRecorder.Presses.Single();
            Assert.AreEqual("hold", p.Fired);
            Assert.AreEqual("TrCharge", p.Selector);
            Assert.AreEqual(2.0, TeachRecorder.PressJson(p)["seconds"]!.Value<double>(), 1e-6);
            Assert.IsNull(TeachRecorder.PressJson(p)["x"], "a named hold carries no point");
        }

        [Test]
        public void ACheatTheKitRanWhileTeaching_IsAStep_AndAFailedOneIsNot()
        {
            TeachRecorder.Begin(null);
            TeachRecorder.NoteCheat("  Skip 10 Levels ", true);
            TeachRecorder.NoteCheat("broken", false);
            var p = TeachRecorder.Presses.Single();
            Assert.AreEqual("cheat", p.Fired);
            Assert.AreEqual("Skip 10 Levels", p.Command);
        }

        // ---- the screen before and after each step ---------------------------------------------------------------------

        [Test]
        public void TheBeforeIsTakenAtThePress_NotBorrowedFromThePreviousAfter_SoAPopupInBetweenIsNotThePressesDoing()
        {
            var lobby = Child(_canvas.gameObject, "TrLobby");
            var a = ButtonOn(lobby, "TrA");
            var popup = Child(_canvas.gameObject, "TrPopup");
            ButtonOn(popup, "TrClose");
            popup.SetActive(false);
            TeachRecorder.Begin(null);
            Press(a, Vector2.zero);
            Settle();
            CollectionAssert.AreEqual(new[] { "TrCanvas/TrLobby" }, TeachRecorder.Presses[0].After!.Roots);

            popup.SetActive(true); // a popup arrives by itself between the two presses
            Press(a, Vector2.zero);
            CollectionAssert.AreEqual(new[] { "TrCanvas/TrLobby", "TrCanvas/TrPopup" }, TeachRecorder.Presses[1].Before.Roots,
                "the second press's before already has the popup — the press did not open it");
        }

        [Test]
        public void APressBeforeTheBeatHasPassed_TakesTheOwedAfterAtOnce_BeforeTheScreenChanges()
        {
            var lobby = Child(_canvas.gameObject, "TrLobby");
            var a = ButtonOn(lobby, "TrA");
            var panel = Child(_canvas.gameObject, "TrPanel");
            panel.SetActive(false);
            TeachRecorder.Begin(null);
            Press(a, Vector2.zero);
            panel.SetActive(true);             // what press A opened
            Press(a, Vector2.zero);            // pressed again before A's beat passed
            Assert.IsNotNull(TeachRecorder.Presses[0].After, "A's after was taken at B's down");
            CollectionAssert.Contains(TeachRecorder.Presses[0].After!.Roots, "TrCanvas/TrPanel");
            var afterJson = (JObject)TeachRecorder.PressJson(TeachRecorder.Presses[0])["after"]!;
            CollectionAssert.Contains(afterJson["added"]!.Values<string>().ToList(), "TrPanel");
        }

        [Test]
        public void StopTakesTheLastAfter_AndAPressStillDownFiredNothing()
        {
            var lobby = Child(_canvas.gameObject, "TrLobby");
            var a = ButtonOn(lobby, "TrA");
            TeachRecorder.Begin(null);
            Press(a, Vector2.zero);
            TeachRecorder.OnPointerDown(a, Vector2.zero, Screen1080); // still down at Stop
            TeachRecorder.Stop();
            Assert.AreEqual(1, TeachRecorder.Presses.Count);
            Assert.IsNotNull(TeachRecorder.Presses[0].After);
            Assert.AreEqual(1, TeachRecorder.Cancelled);
        }

        [Test]
        public void ThePressJson_CarriesWhatFired_TheIdentity_TheRootsBeforeAndAfter_AndTheThumbnailName()
        {
            var lobby = Child(_canvas.gameObject, "TrLobby");
            var equip = ButtonOn(lobby, "TrEquip", "Equipment");
            var eq = Child(_canvas.gameObject, "TrEquipment");
            ButtonOn(eq, "TrBack");
            eq.SetActive(false);
            TeachRecorder.Begin(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tr-thumbs"));
            Press(equip, Vector2.zero);
            eq.SetActive(true);
            Settle();
            var j = TeachRecorder.PressesJson()[0];
            Assert.AreEqual("click", j["fired"]!.Value<string>());
            Assert.AreEqual("TrEquip", j["selector"]!.Value<string>());
            Assert.AreEqual("Equipment", j["element"]!["label"]!.Value<string>());
            CollectionAssert.AreEqual(new[] { "TrCanvas/TrLobby" }, j["before"]!["roots"]!.Values<string>().ToList());
            CollectionAssert.AreEqual(new[] { "TrCanvas/TrEquipment", "TrCanvas/TrLobby" }, j["after"]!["roots"]!.Values<string>().ToList());
            CollectionAssert.AreEquivalent(new[] { "TrBack", "TrEquipment", "TrLabel" }.Where(n => n != "TrLabel"),
                j["after"]!["added"]!.Values<string>().Where(n => n != "TrLabel").ToList());
            CollectionAssert.Contains(j["after"]!["buttons"]!.Values<string>().ToList(), "TrBack");
            Assert.AreEqual("step-0.jpg", j["thumbnail"]!.Value<string>());
        }

        [Test]
        public void ARecordedTeach_AnalysedAsWritten_SplitsThePopupCloseAsADismiss_AndArrivesOnlyOnAuthoredNames()
        {
            // the recorder's own JSON straight into the rule: the seam that runs in production (invariant 101)
            var lobby = Child(_canvas.gameObject, "TrLobby");
            var equip = ButtonOn(lobby, "TrEquip", "Equipment");
            var offer = Child(_canvas.gameObject, "TrOffer");
            var close = ButtonOn(offer, "TrClose");
            var eq = Child(_canvas.gameObject, "TrEquipment");
            Child(eq, "TrGrid");
            Child(eq, "TrFx(Clone)");
            eq.SetActive(false);
            TeachRecorder.Begin(null);
            Press(close, Vector2.zero);
            offer.SetActive(false);
            Settle();
            Press(equip, Vector2.zero);
            eq.SetActive(true);
            TeachRecorder.Stop();
            var r = TeachAnalysis.Analyse(TeachRecorder.PressesJson(),
                new[] { "TrLobby", "TrEquip", "TrClose", "TrEquipment", "TrGrid" }, null,
                new JObject { ["kind"] = TeachAnalysis.StartLobby });
            CollectionAssert.AreEqual(new[] { 1 }, r.Goal);
            CollectionAssert.AreEqual(new[] { "TrClose" }, r.Dismiss);
            CollectionAssert.AreEqual(new[] { "TrEquipment", "TrGrid" }, r.TeachArrival, "the clone is not authored");
            Assert.IsNull(r.ReplayBlocked);
            StringAssert.Contains("the confirming replay did not run", r.Why);
        }

        [Test]
        public void PressesPastTheCap_AreCountedNotKept()
        {
            TeachRecorder.Begin(null);
            for (var i = 0; i < TeachRecorder.MaxPresses + 3; i++) Press(null, Vector2.zero);
            Assert.AreEqual(TeachRecorder.MaxPresses, TeachRecorder.Presses.Count);
            Assert.AreEqual(3, TeachRecorder.Dropped);
        }
    }
}
