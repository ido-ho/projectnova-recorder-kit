using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P4 (§3.4) — TEACH: record what a person's presses ACTUALLY FIRED, so the kit can replay it.
    ///
    /// RECORDS ONLY BETWEEN <see cref="Start"/> AND <see cref="Stop"/>: the per-frame probe exists only in that window,
    /// and every entry point refuses when it is not teaching. In Play Mode only.
    ///
    /// AT POINTER-DOWN — before the game reacts and the screen changes — it raycasts the EventSystem at the point and
    /// resolves the top hit (<see cref="Resolve"/>, pure over the hit object, so the tests hand it objects directly):
    /// <list type="bullet">
    /// <item><c>click</c>: the object Unity will send the click to (<see cref="ExecuteEvents.GetEventHandler{T}"/> for
    /// <see cref="IPointerClickHandler"/> — the same lookup <see cref="UguiDriver.Click"/> presses through), as a selector
    /// the driver resolves back to it: its name, or <c>Name@Label</c> when the name is shared and the label unique.</item>
    /// <item><c>invoke-button</c>: no click handler above the hit, but a <see cref="Button"/> on the hit or its children
    /// (the shape the bridge's <c>invoke-button</c> exists for — a widget whose name is on a container and whose Button is
    /// a child the pointer walk never reaches). Recorded as what fired; the replay does NOT press it (a bridge verb, outside
    /// the press guard — plan §4), so a teach that needs it is marked unconfirmed and says why.</item>
    /// <item><c>hold</c>: a named element that takes a pointer-DOWN but no click (a press-and-hold control).</item>
    /// <item>a gesture on the game world (nothing in the UI takes the press): <c>tap-at</c>, <c>hold</c> at a point, or
    /// <c>drag</c> — screen points kept normalised to the screen (0..1), never replayed as a name.</item>
    /// </list>
    /// It also takes the whole screen at that moment (<see cref="ScreenSignature.Observe()"/>) — the "before" of the press,
    /// measured at the press, never borrowed from the previous press's "after" (a popup that appeared between two presses
    /// would otherwise be counted as something the second press opened).
    ///
    /// AT POINTER-UP it decides what fired: a pointer that moved past the drag threshold is a <c>drag</c>; a click whose
    /// release is over another handler fired NOTHING (Unity sends no click) and is dropped, counted in
    /// <see cref="Cancelled"/>; a hold longer than <see cref="HoldSec"/> keeps its seconds.
    ///
    /// A BEAT AFTER each press (<see cref="SettleSec"/>, or at once when the next press starts sooner) it takes the screen
    /// again — the "after" (roots, the names new since the press, the clickable names: its ui-dump) — and a thumbnail.
    ///
    /// A CHEAT run through the kit's own bridge while teaching (the relay's <c>run-cheat</c>, the Levers window) is noted as
    /// a <c>cheat</c> step (<see cref="NoteCheat"/>). A cheat typed into the game's own console never passes the kit and
    /// is not seen — said, not guessed.
    ///
    /// READ-ONLY toward the game: it raycasts and reads names, labels, component types and persistent calls; it adds no
    /// listener and changes nothing. It writes only thumbnails under <c>Library/Nova/teach/</c>. Nothing is uploaded here:
    /// the recipe is shown in the Nova Capture window first, and the person uploads or discards it.
    /// </summary>
    public static class TeachRecorder
    {
        /// <summary>How long after a release the screen is taken again (the game's reaction: a panel's open tween).</summary>
        public const float SettleSec = 1.0f;
        /// <summary>A press held at least this long is a hold.</summary>
        public const double HoldSec = 0.6;
        /// <summary>The fewest pixels a pointer must move to be a drag (the EventSystem's own threshold when larger).</summary>
        public const float MinDragPixels = 12f;
        /// <summary>The most presses one teach keeps (a teach is "one screen or moment"; the facts stay small).</summary>
        public const int MaxPresses = 40;

        public const string FiredClick = "click";
        public const string FiredInvokeButton = "invoke-button";
        public const string FiredHold = "hold";
        public const string FiredDrag = "drag";
        public const string FiredTapAt = "tap-at";
        public const string FiredCheat = "cheat";

        /// <summary>What the press at pointer-down resolved to.</summary>
        public sealed class Target
        {
            public string Fired = FiredTapAt;
            /// <summary>The object the press reaches (the click handler, the Button's host, the down handler), or null
            /// for the game world.</summary>
            public GameObject? Element;
            public string TopHit = "";
            public string HandlerKind = "none";
        }

        /// <summary>One recorded step.</summary>
        public sealed class Press
        {
            public string Fired = FiredTapAt;
            public string? Selector;
            public ElementIdentity? Element;
            public string? PressRoot;
            /// <summary>The name is shared by other objects on that screen and no label tells it apart — a replay could
            /// press another one. Judged AT THE PRESS, on the screen it was made on.</summary>
            public bool Shared;
            public string TopHit = "";
            public string HandlerKind = "none";
            public Vector2 Down;
            public Vector2 Up;
            public Vector2 ScreenSize;
            public double At;
            public double Seconds;
            public string? Command;
            public ScreenSignature.Observation Before = new();
            public ScreenSignature.Observation? After;
            /// <summary>The thumbnail's file name under the teach folder, or null.</summary>
            public string? Thumbnail;
            internal GameObject? DownElement;
        }

        private static TeachProbe? _probe;
        private static readonly List<Press> _presses = new();
        private static Press? _pending;
        private static Press? _awaitingAfter;
        private static double _afterDueAt = -1;
        private static double _startedAt;
        private static bool _teaching;

        /// <summary>The screen when teaching started — the recipe's declared start as it looked.</summary>
        public static ScreenSignature.Observation? StartScreen { get; private set; }

        /// <summary>Kit 0.14.0 (fix 5a) — the Stop picture's file name under the teach folder.</summary>
        public const string StopThumbnail = "stop.jpg";
        /// <summary>Kit 0.14.0 (fix 5a) — the confirming replay's end picture's file name under the teach folder.</summary>
        public const string ReplayStopThumbnail = "replay-stop.jpg";

        /// <summary>Kit 0.14.0 (fix 5a) — THE SCREEN WHEN THE PERSON PRESSED STOP, as the teach facts carry it: its roots,
        /// the names new since the LAST GOAL press's "before" (<see cref="StopJsonOf"/> — the baseline that press's own
        /// <c>after.added</c> is measured against, so the box can read the two the same way), its clickable names, and the
        /// Stop picture's name (<see cref="StopThumbnail"/>, or null when no picture is taken). Null until a Stop. The
        /// recipe's arrival screen is this screen (<see cref="TeachAnalysis.ArrivalSource"/>).</summary>
        public static JObject? StopJson { get; private set; }

        /// <summary>Kit 0.14.0 (audit L3) — the export's authored names the job sent (null: a relay teach): kept first when a
        /// screen's new names pass the cap (<see cref="ScreenSignature.Added"/>).</summary>
        internal static ICollection<string>? Prefer { get; private set; }
        /// <summary>Clicks that fired nothing (released over another handler).</summary>
        public static int Cancelled { get; private set; }
        /// <summary>Presses past <see cref="MaxPresses"/>, not kept.</summary>
        public static int Dropped { get; private set; }
        public static bool IsTeaching => _teaching;
        public static IReadOnlyList<Press> Presses => _presses;
        /// <summary>Where thumbnails go (null = none are taken — the tests, and a relay teach with no job).</summary>
        public static string? ThumbnailDir { get; private set; }

        /// <summary>How the screen is read (the tests may hand their own).</summary>
        internal static Func<ScreenSignature.Observation> Observe = ScreenSignature.Observe;
        /// <summary>The clock (unscaled real time).</summary>
        internal static Func<double> Now = () => Time.realtimeSinceStartupAsDouble;

        /// <summary>Start recording. Returns null on success, or why it cannot start.</summary>
        public static string? Start(string? thumbnailDir = null, ICollection<string>? authoredNames = null)
        {
            if (!Application.isPlaying) return "Teach needs Play Mode";
            TeachThumbnail.SweepLeftoverHosts(); // audit L2
            var why = Begin(thumbnailDir, authoredNames);
            if (why != null) return why;
            var go = new GameObject("[Nova Teach]") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _probe = go.AddComponent<TeachProbe>();
            Debug.Log("[RecorderKit] Teach: recording what each press fires. Stop to see the recipe.");
            return null;
        }

        /// <summary>The recording state without the per-frame probe — the tests drive the entry points themselves.</summary>
        internal static string? Begin(string? thumbnailDir, ICollection<string>? authoredNames = null)
        {
            if (_teaching) return "already teaching";
            Prefer = authoredNames;
            _presses.Clear();
            _pending = null;
            _awaitingAfter = null;
            _afterDueAt = -1;
            Cancelled = 0;
            Dropped = 0;
            StopJson = null;
            ThumbnailDir = thumbnailDir;
            if (thumbnailDir != null)
            {
                try
                {
                    Directory.CreateDirectory(thumbnailDir);
                    // kit 0.14.0: a Teach again starts with no pictures of the last attempt — a step or Stop picture this
                    // attempt does not take must not go up as if it did (a replay's end picture included)
                    foreach (var old in Directory.GetFiles(thumbnailDir, "*.jpg"))
                    {
                        var name = Path.GetFileName(old);
                        if (name == StopThumbnail || name == ReplayStopThumbnail || name.StartsWith("step-", StringComparison.Ordinal))
                            try { File.Delete(old); }
                            catch (Exception) { /* best-effort: an undeletable old picture is overwritten when retaken */ }
                    }
                }
                catch (Exception) { ThumbnailDir = null; }
            }
            _startedAt = Now();
            StartScreen = Observe();
            _teaching = true;
            return null;
        }

        /// <summary>Stop recording: the pending "after" is taken now, and the STOP SCREEN (<see cref="StopJson"/>), both
        /// synchronously; the probe goes. Returns false when nothing was being taught. What was recorded stays in
        /// <see cref="Presses"/> until the next Start.
        ///
        /// Kit 0.14.0 (fix 5a): the pictures owed at Stop — the pending press's <c>step-N.jpg</c> (named, but before this
        /// never written: the probe was destroyed first) and <see cref="StopThumbnail"/> — are taken by the probe at the end
        /// of the frame (<see cref="TeachThumbnail.Capture"/> needs one), and the probe destroys itself after them. Without
        /// a probe (the tests, or Play Mode already gone) no picture is taken and none is named.</summary>
        /// <param name="timeLimit">audit L4 — the recording was stopped at its time limit, not by a person: the Stop
        /// screen is then whatever idle screen shows, and is never the arrival (<c>timeLimit</c> on <see cref="StopJson"/>).</param>
        public static bool Stop(bool timeLimit = false)
        {
            if (!_teaching) return false;
            if (_pending != null) { _pending = null; Cancelled++; } // a press still down at Stop fired nothing yet
            var owed = TakeAfterNow();
            var pictures = new List<string>();
            if (ThumbnailDir != null && _probe != null && Application.isPlaying)
            {
                if (owed?.Thumbnail != null) pictures.Add(Path.Combine(ThumbnailDir, owed.Thumbnail));
                pictures.Add(Path.Combine(ThumbnailDir, StopThumbnail));
            }
            // (a named picture that was never written is dropped at upload: FactsJson nulls a name the thumbnails lack)
            StopJson = StopJsonOf(_presses, StartScreen, Observe(), pictures.Count > 0 ? StopThumbnail : null, Now() - _startedAt, timeLimit);
            _teaching = false;
            if (_probe != null)
            {
                if (pictures.Count > 0) _probe.TakeThenDestroy(pictures);
                else UnityEngine.Object.DestroyImmediate(_probe.gameObject);
                _probe = null;
            }
            return true;
        }

        /// <summary>Kit 0.14.0 (fix 5a) — the Stop screen as the facts carry it (<see cref="StopJson"/>): <c>added</c> is
        /// measured against the LAST GOAL press's "before" — the goal chosen by <see cref="TeachAnalysis.IsDismiss"/> over the
        /// press as recorded, the SAME split the box runs, so both sides read the one baseline — or against the start screen
        /// when no press is a goal. <c>afterLastGoalSec</c> is how long after that press (its pointer-down) the person
        /// pressed Stop — the confirming replay waits the same lag after its last step before it reads the screen, so both
        /// observations are taken at the same moment of the destination (audit H1: a fight that builds itself seconds
        /// later is seen by both, or by neither). Pure over the observations (the tests hand their own).</summary>
        internal static JObject StopJsonOf(IReadOnlyList<Press> presses, ScreenSignature.Observation? start,
            ScreenSignature.Observation stop, string? thumbnail, double stopAt = 0, bool timeLimit = false)
        {
            ScreenSignature.Observation? baseline = null;
            double goalAt = stopAt;
            for (var i = presses.Count - 1; i >= 0 && baseline == null; i--)
                if (!TeachAnalysis.IsDismiss(PressJson(presses[i])))
                {
                    baseline = presses[i].Before;
                    goalAt = presses[i].At;
                }
            var o = ScreenSignature.AfterJson(baseline ?? start ?? new ScreenSignature.Observation(), stop, Prefer);
            o["afterLastGoalSec"] = Math.Round(Math.Max(0, stopAt - goalAt), 2);
            o["timeLimit"] = timeLimit;
            o["thumbnail"] = thumbnail == null ? JValue.CreateNull() : thumbnail;
            return o;
        }

        /// <summary>The spike's relay form (<c>teach-stop</c>): stop and write the presses to
        /// <c>Library/Nova/teach/presses-*.json</c>. Returns the path, or null when nothing was being taught.</summary>
        public static string? Stop(string projectRoot)
        {
            if (!Stop()) return null;
            var dir = Path.Combine(RelayPaths.NovaDir(projectRoot), "teach");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"presses-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, PressesJson().ToString());
            return path;
        }

        // ---- resolution (pure over the hit) --------------------------------------------------------------------------

        /// <summary>What a press on <paramref name="topHit"/> reaches. Null = nothing in the UI took the press: the game
        /// world. Pure over the object tree, so the tests hand it objects instead of a raycast.</summary>
        public static Target Resolve(GameObject? topHit)
        {
            if (topHit == null) return new Target { Fired = FiredTapAt };
            var t = new Target { TopHit = topHit.name };
            var click = ExecuteEvents.GetEventHandler<IPointerClickHandler>(topHit);
            if (click != null)
            {
                t.Fired = FiredClick;
                t.Element = click;
                t.HandlerKind = click.GetComponent<Button>() != null ? "Button"
                    : click.GetComponent<Toggle>() != null ? "Toggle"
                    : "IPointerClickHandler";
                return t;
            }
            // the bridge's invoke-button search: the object THEN its children, never parents
            var button = topHit.GetComponent<Button>() ?? topHit.GetComponentInChildren<Button>();
            if (button != null)
            {
                t.Fired = FiredInvokeButton;
                t.Element = topHit;
                t.HandlerKind = "Button (child)";
                return t;
            }
            var down = ExecuteEvents.GetEventHandler<IPointerDownHandler>(topHit);
            if (down != null)
            {
                t.Fired = FiredHold;
                t.Element = down;
                t.HandlerKind = "IPointerDownHandler";
                return t;
            }
            t.Fired = FiredTapAt; // a UI graphic that takes no press (a backdrop): the press is on the world behind it
            return t;
        }

        /// <summary>The selector the driver resolves to THIS object: its name when unique on screen, else
        /// <c>Name@Label</c> when that label is unique among the same-named objects, else the name — with
        /// <paramref name="shared"/> true (a replay could press another one; a goal step like that is refused as unstable).</summary>
        public static string SelectorFor(GameObject go, out bool shared)
        {
            shared = false;
            var matches = UguiDriver.MatchesInClickOrder(go.name);
            if (matches.Count < 2) return go.name;
            var label = UguiDriver.LabelOf(go);
            if (label != null)
            {
                var byLabel = UguiDriver.MatchesInClickOrder($"{go.name}@{label}");
                if (byLabel.Count == 1 && byLabel[0] == go) return $"{go.name}@{label}";
            }
            shared = true;
            return go.name;
        }

        // ---- the entry points the probe (and the tests) drive ---------------------------------------------------------

        /// <summary>Pointer DOWN at <paramref name="position"/> on <paramref name="topHit"/> (null = nothing in the UI).</summary>
        internal static void OnPointerDown(GameObject? topHit, Vector2 position, Vector2 screenSize)
        {
            if (!_teaching) return;
            // the previous press's "after" is owed: take it NOW, before this press changes the screen
            TakeAfterNow();
            var before = Observe();
            var target = Resolve(topHit);
            var press = new Press
            {
                Fired = target.Fired,
                TopHit = target.TopHit,
                HandlerKind = target.HandlerKind,
                Down = position,
                Up = position,
                ScreenSize = screenSize,
                At = Now() - _startedAt,
                Before = before,
                DownElement = target.Element,
            };
            if (target.Element != null)
            {
                press.Selector = SelectorFor(target.Element, out var shared);
                press.Shared = shared;
                press.Element = ElementIdentity.Of(target.Element);
                press.PressRoot = ScreenSignature.RootOf(target.Element);
            }
            _pending = press;
        }

        /// <summary>Pointer UP at <paramref name="position"/>; <paramref name="topHitAtUp"/> is what is under it now.</summary>
        internal static void OnPointerUp(GameObject? topHitAtUp, Vector2 position)
        {
            if (!_teaching || _pending == null) return;
            var p = _pending;
            _pending = null;
            p.Up = position;
            p.Seconds = Math.Max(0, Now() - _startedAt - p.At);
            var moved = Vector2.Distance(p.Down, p.Up);
            var threshold = Math.Max(MinDragPixels, EventSystem.current != null ? EventSystem.current.pixelDragThreshold : 0);
            if (moved >= threshold)
            {
                // a swipe or a scroll: a gesture, kept by its points — never as a name
                p.Fired = FiredDrag;
                p.Selector = null;
                p.Shared = false;
            }
            else if (p.Fired == FiredClick)
            {
                // Unity sends the click only when the release is over the SAME handler — otherwise nothing fired
                var upHandler = topHitAtUp == null ? null : ExecuteEvents.GetEventHandler<IPointerClickHandler>(topHitAtUp);
                if (upHandler != p.DownElement)
                {
                    Cancelled++;
                    return;
                }
            }
            else if (p.Fired == FiredTapAt && p.Seconds >= HoldSec)
            {
                p.Fired = FiredHold; // a long press on the world: a hold at a point
            }
            Keep(p);
        }

        /// <summary>A cheat the kit's bridge ran while teaching. Called at the bridge seam; a no-op when not teaching.</summary>
        public static void NoteCheat(string command, bool ok)
        {
            if (!_teaching || string.IsNullOrWhiteSpace(command) || !ok) return;
            TakeAfterNow();
            var p = new Press
            {
                Fired = FiredCheat,
                Command = command.Trim(),
                At = Now() - _startedAt,
                Before = Observe(),
            };
            Keep(p);
        }

        private static void Keep(Press p)
        {
            if (_presses.Count >= MaxPresses)
            {
                Dropped++;
                return;
            }
            p.DownElement = null;
            _presses.Add(p);
            _awaitingAfter = p;
            _afterDueAt = Now() + SettleSec;
        }

        /// <summary>The probe's tick: take the owed "after" once its beat has passed. Returns the press whose "after" was
        /// just taken (so the probe can take its thumbnail), or null.</summary>
        internal static Press? Tick()
        {
            if (!_teaching || _awaitingAfter == null || Now() < _afterDueAt) return null;
            return TakeAfterNow();
        }

        private static Press? TakeAfterNow()
        {
            var p = _awaitingAfter;
            if (p == null) return null;
            _awaitingAfter = null;
            _afterDueAt = -1;
            p.After = Observe();
            if (ThumbnailDir != null) p.Thumbnail = $"step-{_presses.IndexOf(p)}.jpg";
            return p;
        }

        // ---- what a teach hands on ------------------------------------------------------------------------------------

        /// <summary>The presses as the recipe draft carries them (<c>presses[]</c> of the teach facts).</summary>
        public static JArray PressesJson()
        {
            var a = new JArray();
            foreach (var p in _presses) a.Add(PressJson(p));
            return a;
        }

        internal static JObject PressJson(Press p)
        {
            var o = new JObject { ["fired"] = p.Fired, ["at"] = Math.Round(p.At, 2) };
            if (p.Selector != null) o["selector"] = p.Selector;
            if (p.Element != null) o["element"] = p.Element.ToJson();
            o["pressRoot"] = p.PressRoot == null ? JValue.CreateNull() : p.PressRoot;
            o["shared"] = p.Shared;
            o["topHit"] = p.TopHit;
            o["handlerKind"] = p.HandlerKind;
            if (p.Command != null) o["command"] = p.Command;
            if (p.Fired == FiredTapAt || p.Fired == FiredDrag || (p.Fired == FiredHold && p.Selector == null))
            {
                o["x"] = Norm(p.Down.x, p.ScreenSize.x);
                o["y"] = Norm(p.Down.y, p.ScreenSize.y);
                if (p.Fired == FiredDrag)
                {
                    o["x2"] = Norm(p.Up.x, p.ScreenSize.x);
                    o["y2"] = Norm(p.Up.y, p.ScreenSize.y);
                }
            }
            if (p.Fired == FiredHold || p.Fired == FiredDrag) o["seconds"] = Math.Round(p.Seconds, 2);
            o["before"] = ScreenSignature.BeforeJson(p.Before);
            o["after"] = p.After == null ? JValue.CreateNull() : ScreenSignature.AfterJson(p.Before, p.After, Prefer);
            o["thumbnail"] = p.Thumbnail == null ? JValue.CreateNull() : p.Thumbnail;
            return o;
        }

        private static double Norm(float v, float size) => size > 0 ? Math.Round(Mathf.Clamp01(v / size), 4) : 0;

        internal static void ResetForTests()
        {
            _teaching = false;
            _presses.Clear();
            _pending = null;
            _awaitingAfter = null;
            Observe = ScreenSignature.Observe;
            Now = () => Time.realtimeSinceStartupAsDouble;
            StartScreen = null;
            StopJson = null;
            Prefer = null;
            ThumbnailDir = null;
        }
    }

    /// <summary>The per-frame watcher Teach adds while it records: reads the pointer, raycasts at down and up, takes the
    /// "after" and its thumbnail. Changes nothing in the game.</summary>
    [AddComponentMenu("")]
    internal sealed class TeachProbe : MonoBehaviour
    {
        private bool _wasDown;
        private bool _finishing;
        internal bool Finishing => _finishing;

        /// <summary>Kit 0.14.0 (fix 5a) — the teach has stopped: take the owed pictures (end of frame, one after the other),
        /// then destroy this probe. It reads no pointer from here on.</summary>
        internal void TakeThenDestroy(IReadOnlyList<string> paths)
        {
            _finishing = true;
            StartCoroutine(TeachThumbnail.CaptureAllThenDestroy(paths, gameObject));
        }

        private void Update()
        {
            if (_finishing) return;
            var (down, pos) = TeachPointer.Read();
            var size = new Vector2(Screen.width, Screen.height);
            if (down && !_wasDown) TeachRecorder.OnPointerDown(TopHit(pos), pos, size);
            if (!down && _wasDown) TeachRecorder.OnPointerUp(TopHit(pos), pos);
            _wasDown = down;
            var taken = TeachRecorder.Tick();
            if (taken?.Thumbnail != null && TeachRecorder.ThumbnailDir != null)
                StartCoroutine(TeachThumbnail.Capture(Path.Combine(TeachRecorder.ThumbnailDir, taken.Thumbnail)));
        }

        private static GameObject? TopHit(Vector2 position)
        {
            var es = EventSystem.current;
            if (es == null) return null;
            var results = new List<RaycastResult>();
            es.RaycastAll(new PointerEventData(es) { position = position }, results);
            return results.Count > 0 ? results[0].gameObject : null;
        }
    }

    /// <summary>A small JPEG of the screen, taken at the end of a frame. Best-effort: a failure leaves no file, and the
    /// recipe then carries no thumbnail for that step.</summary>
    internal static class TeachThumbnail
    {
        /// <summary>Kit 0.14.0 (fix 5a): 360 px wide (was 160 — too thin to tell a fight from the board by eye or by a
        /// model). A 360-px JPEG at quality 60 is tens of KB; the box's per-picture cap is 64 KB (a larger one is left out,
        /// <c>TeachJob.Thumbnails</c>), and the teach's pictures share one budget so the facts stay under the result door.</summary>
        public const int Width = 360;
        public const int Quality = 60;
        /// <summary>The box's per-picture cap (<c>TEACH_FACTS_CAPS.thumbnailBytes</c>) — a larger picture is left out there
        /// and here (<c>TeachJob.Thumbnails</c>).</summary>
        public const int MaxBytes = 64 * 1024;
        /// <summary>The qualities tried in turn when a picture is over <see cref="MaxBytes"/> (audit M6: a busy 360-px screen
        /// at quality 60 can pass it, and the Stop picture was then silently dropped).</summary>
        public static readonly int[] StepDown = { Quality, 50, 40, 30, 20 };

        /// <summary>The picture at the first quality of <see cref="StepDown"/> that fits <see cref="MaxBytes"/> — or, when
        /// none does, the smallest one (it is then left out at upload, as before). Pure over the encoder.</summary>
        public static byte[] EncodeUnderCap(Func<int, byte[]> encode)
        {
            byte[]? smallest = null;
            foreach (var q in StepDown)
            {
                var bytes = encode(q);
                if (bytes.Length <= MaxBytes) return bytes;
                if (smallest == null || bytes.Length < smallest.Length) smallest = bytes;
            }
            return smallest ?? Array.Empty<byte>();
        }

        /// <summary>Each picture in turn (each at the end of its own frame), then <paramref name="host"/> is destroyed —
        /// the Stop's deferred destroy (<see cref="TeachProbe.TakeThenDestroy"/>).</summary>
        public static IEnumerator CaptureAllThenDestroy(IReadOnlyList<string> paths, GameObject host)
        {
            try
            {
                foreach (var p in paths)
                {
                    var one = Capture(p);
                    while (one.MoveNext()) yield return one.Current;
                }
            }
            finally
            {
                if (host != null) UnityEngine.Object.Destroy(host);
            }
        }

        /// <summary>Kit 0.14.0 (fix 5a) — ONE picture with no teach probe alive (the confirming replay's end picture): a
        /// hidden host runs the capture at the end of the frame and destroys itself. Best-effort, as every thumbnail is:
        /// outside Play Mode nothing is taken.</summary>
        public static void CaptureDetached(string path)
        {
            if (!Application.isPlaying) return;
            SweepLeftoverHosts();
            var go = new GameObject("[Nova Teach picture]") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<TeachShutter>().Begin(new[] { path });
        }

        /// <summary>Kit 0.14.0 (audit L2) — destroy every picture host a capture left behind (HideAndDontSave +
        /// DontDestroyOnLoad: when Play Mode ends inside its one frame, its <c>finally</c> may never run). Found by its marker
        /// component, <see cref="TeachShutter"/>, including hidden objects. Called on every Play Mode exit, at every Teach
        /// start and before a new detached picture. Returns how many went.</summary>
        public static int SweepLeftoverHosts()
        {
            var gone = 0;
            foreach (var host in Resources.FindObjectsOfTypeAll<TeachShutter>())
            {
                if (host == null || host.gameObject == null) continue;
                // a host still taking its picture this frame is not swept from under it
                if (Application.isPlaying && host.Busy) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(host.gameObject);
                else UnityEngine.Object.DestroyImmediate(host.gameObject);
                gone++;
            }
            // …and a stopped teach's probe that was taking its Stop pictures (TakeThenDestroy) when Play Mode ended
            foreach (var probe in Resources.FindObjectsOfTypeAll<TeachProbe>())
            {
                if (probe == null || probe.gameObject == null || TeachRecorder.IsTeaching) continue;
                if (Application.isPlaying && probe.Finishing) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(probe.gameObject);
                else UnityEngine.Object.DestroyImmediate(probe.gameObject);
                gone++;
            }
            return gone;
        }

        public static IEnumerator Capture(string path)
        {
            yield return new WaitForEndOfFrame();
            Texture2D? shot = null;
            RenderTexture? rt = null;
            Texture2D? small = null;
            try
            {
                shot = ScreenCapture.CaptureScreenshotAsTexture();
                if (shot == null || shot.width <= 0) yield break;
                var h = Math.Max(1, (int)Math.Round((double)shot.height * Width / shot.width));
                rt = RenderTexture.GetTemporary(Width, h);
                Graphics.Blit(shot, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                small = new Texture2D(Width, h, TextureFormat.RGB24, false);
                small.ReadPixels(new Rect(0, 0, Width, h), 0, 0);
                small.Apply();
                RenderTexture.active = prev;
                Texture2D tex = small;
                File.WriteAllBytes(path, EncodeUnderCap(q => tex.EncodeToJPG(q)));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RecorderKit] Teach: thumbnail not taken: " + e.Message);
            }
            finally
            {
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (shot != null) UnityEngine.Object.Destroy(shot);
                if (small != null) UnityEngine.Object.Destroy(small);
            }
        }
    }

    /// <summary>Kit 0.14.0 — the host of a detached picture (<see cref="TeachThumbnail.CaptureDetached"/>).</summary>
    [AddComponentMenu("")]
    internal sealed class TeachShutter : MonoBehaviour
    {
        /// <summary>Taking its pictures now (started in this Play session, not yet done).</summary>
        internal bool Busy { get; private set; }

        internal void Begin(IReadOnlyList<string> paths)
        {
            Busy = true;
            StartCoroutine(TeachThumbnail.CaptureAllThenDestroy(paths, gameObject));
        }
    }

    /// <summary>Is the primary pointer down, and where — from the Input System when the game uses it (reached by
    /// reflection, so the kit compiles without it), else the legacy Input manager.</summary>
    internal static class TeachPointer
    {
        private static readonly Type? MouseType = GameReflection.FindType("UnityEngine.InputSystem.Mouse");
        private static readonly Type? TouchType = GameReflection.FindType("UnityEngine.InputSystem.Touchscreen");

        public static (bool Down, Vector2 Pos) Read()
        {
            if (TryInputSystem(TouchType, "primaryTouch", "press", "position", out var t) && t.Down) return t;
            if (TryInputSystem(MouseType, null, "leftButton", "position", out var m)) return m;
            try { return (Input.GetMouseButton(0), Input.mousePosition); }
            catch (InvalidOperationException) { return (false, default); } // legacy input disabled
        }

        private static bool TryInputSystem(Type? device, string? sub, string buttonProp, string posProp,
            out (bool Down, Vector2 Pos) result)
        {
            result = default;
            if (device == null) return false;
            var current = device.GetProperty("current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
            if (current == null) return false;
            var owner = sub == null ? current : current.GetType().GetProperty(sub)?.GetValue(current);
            if (owner == null) return false;
            var button = owner.GetType().GetProperty(buttonProp)?.GetValue(owner);
            var pos = owner.GetType().GetProperty(posProp)?.GetValue(owner);
            if (button == null || pos == null) return false;
            var pressed = button.GetType().GetProperty("isPressed")?.GetValue(button) is true;
            var value = pos.GetType().GetMethod("ReadValue", Type.EmptyTypes)?.Invoke(pos, null);
            result = (pressed, value is Vector2 v ? v : default);
            return true;
        }
    }
}
