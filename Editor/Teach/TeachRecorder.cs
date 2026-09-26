using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3, spike B — TEACH: record what a person presses, BY NAME, so the kit can replay it.
    ///
    /// Between Start and Stop, in Play Mode, a hidden probe watches the pointer every player frame. At the moment
    /// the pointer goes DOWN — before the game reacts and the screen changes — it raycasts the EventSystem at that
    /// point and asks Unity which object will receive the click (<see cref="ExecuteEvents.GetEventHandler{T}"/>, the
    /// same lookup <see cref="UguiDriver.Click"/> presses through). When the pointer comes UP, that press becomes one
    /// step: <c>click &lt;selector&gt;</c>, where the selector is the name, or <c>Name@Label</c> when the name is shared
    /// and the label is unique — the form <c>ui-dump</c> prints and the driver resolves. A press on nothing that takes
    /// a click (the game world, a board) is kept as a gesture note with its screen point, never replayed as a name.
    ///
    /// READ-ONLY toward the game: it raycasts and reads names, labels and component types; it adds no listener and
    /// changes nothing. It writes only under <c>Library/Nova/teach/</c>. Local only — nothing is uploaded here.
    /// </summary>
    public static class TeachRecorder
    {
        public sealed class Step
        {
            public string Selector = "";
            public string HandlerName = "";
            public string HandlerKind = "";   // Button, Toggle, other IPointerClickHandler, or none
            public string TopHit = "";
            public Vector2 Position;
            public double At;
            public List<string> ScreenAfter = new();
            public bool Ambiguous;
        }

        private static TeachProbe? _probe;
        private static readonly List<Step> _steps = new();
        private static HashSet<string> _screenAtStart = new();
        private static double _startedAt;

        public static bool IsTeaching => _probe != null;
        public static IReadOnlyList<Step> Steps => _steps;

        /// <summary>Start recording. Returns null on success, or why it cannot start.</summary>
        public static string? Start()
        {
            if (!Application.isPlaying) return "Teach needs Play Mode";
            if (_probe != null) return "already teaching";
            _steps.Clear();
            _screenAtStart = ScreenNames();
            _startedAt = Time.realtimeSinceStartupAsDouble;
            var go = new GameObject("[Nova Teach]") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _probe = go.AddComponent<TeachProbe>();
            Debug.Log("[RecorderKit] Teach: recording presses by name. Stop to save.");
            return null;
        }

        /// <summary>Stop and save. Returns the recipe file's path, or null when nothing was being taught.</summary>
        public static string? Stop(string projectRoot)
        {
            if (_probe == null) return null;
            UnityEngine.Object.DestroyImmediate(_probe.gameObject);
            _probe = null;
            return Save(projectRoot);
        }

        internal static void OnPress(Vector2 position)
        {
            var es = EventSystem.current;
            var step = new Step { Position = position, At = Time.realtimeSinceStartupAsDouble - _startedAt };
            if (es != null)
            {
                var results = new List<RaycastResult>();
                es.RaycastAll(new PointerEventData(es) { position = position }, results);
                if (results.Count > 0)
                {
                    var top = results[0].gameObject;
                    step.TopHit = top.name;
                    var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(top);
                    if (handler != null)
                    {
                        step.HandlerName = handler.name;
                        step.HandlerKind = handler.GetComponent<UnityEngine.UI.Button>() != null ? "Button"
                            : handler.GetComponent<UnityEngine.UI.Toggle>() != null ? "Toggle"
                            : "IPointerClickHandler";
                        step.Selector = SelectorFor(handler);
                        // Judged NOW, on the screen the press was made on — the screen at Save time is another screen.
                        step.Ambiguous = !step.Selector.Contains('@') && UguiDriver.MatchesInClickOrder(step.Selector).Count > 1;
                    }
                    else step.HandlerKind = "none";
                }
                else step.HandlerKind = "none";
            }
            _pending = step;
        }

        private static Step? _pending;

        internal static void OnRelease()
        {
            if (_pending == null) return;
            _steps.Add(_pending);
            _pending = null;
        }

        /// <summary>A beat after each press, the names on screen — so the recipe knows where each press landed.</summary>
        internal static void SnapshotAfterLast()
        {
            if (_steps.Count == 0) return;
            _steps[_steps.Count - 1].ScreenAfter = ScreenNames().OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        /// <summary>The selector the driver resolves to THIS object: its name when unique, else Name@Label when that
        /// label is unique among the same-named objects, else the name with a note (index order is not stable).</summary>
        internal static string SelectorFor(GameObject go)
        {
            var matches = UguiDriver.MatchesInClickOrder(go.name);
            if (matches.Count < 2) return go.name;
            var label = UguiDriver.LabelOf(go);
            if (label != null && UguiDriver.MatchesInClickOrder($"{go.name}@{label}").Count == 1)
                return $"{go.name}@{label}";
            return go.name; // ambiguous: kept, and flagged in the recipe (see "ambiguous")
        }

        /// <summary>Names of every active object on every active canvas.</summary>
        internal static HashSet<string> ScreenNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                if (canvas.gameObject.activeInHierarchy) Walk(canvas.transform, names);
            return names;
        }

        private static void Walk(Transform t, HashSet<string> names)
        {
            for (var i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (!c.gameObject.activeInHierarchy) continue;
                names.Add(c.name);
                Walk(c, names);
            }
        }

        private static string Save(string projectRoot)
        {
            var dir = Path.Combine(RelayPaths.NovaDir(projectRoot), "teach");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var steps = new JArray();
            var shotSteps = new JArray();
            foreach (var s in _steps)
            {
                steps.Add(new JObject
                {
                    ["selector"] = s.Selector,
                    ["handler"] = s.HandlerName,
                    ["handlerKind"] = s.HandlerKind,
                    ["topHit"] = s.TopHit,
                    ["x"] = Math.Round(s.Position.x, 1),
                    ["y"] = Math.Round(s.Position.y, 1),
                    ["at"] = Math.Round(s.At, 2),
                    ["ambiguous"] = s.Ambiguous,
                    ["screenAfterCount"] = s.ScreenAfter.Count,
                    ["screenAfter"] = new JArray(s.ScreenAfter),
                });
                if (s.Selector.Length == 0) continue; // a gesture on the world: noted, not replayed by name
                shotSteps.Add(new JObject { ["kind"] = "click", ["name"] = s.Selector });
                shotSteps.Add(new JObject { ["kind"] = "wait", ["seconds"] = 1.5 });
            }
            // Arrival: a name on screen after the LAST press that was not on screen just BEFORE it (the previous press's
            // "after", or the screen when teaching started). Against the start screen it picked a name the whole lobby
            // already had, so a replay that never reached the last screen would still have "arrived" (spike B, first run).
            var named = _steps.Where(s => s.Selector.Length > 0).ToList();
            var last = named.LastOrDefault();
            var before = named.Count >= 2 && named[named.Count - 2].ScreenAfter.Count > 0
                ? new HashSet<string>(named[named.Count - 2].ScreenAfter, StringComparer.Ordinal)
                : _screenAtStart;
            var arrived = last?.ScreenAfter.Where(n => !before.Contains(n))
                .OrderBy(n => n, StringComparer.Ordinal).ToList() ?? new List<string>();
            var arrival = arrived.FirstOrDefault();
            var shot = new JObject
            {
                ["name"] = "teach-" + stamp,
                ["steps"] = shotSteps,
                ["settle"] = arrival != null
                    ? new JObject { ["kind"] = "present", ["name"] = arrival }
                    : new JObject { ["kind"] = "present", ["name"] = last?.Selector ?? "" },
            };
            var recipe = new JObject
            {
                ["taughtAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["kitVersion"] = KitInfo.Version,
                ["steps"] = steps,
                ["arrivalCandidates"] = new JArray(arrived.Take(20)),
                ["shot"] = shot,
            };
            var path = Path.Combine(dir, $"recipe-{stamp}.json");
            File.WriteAllText(path, recipe.ToString());
            File.WriteAllText(Path.Combine(dir, $"shots-{stamp}.json"), new JObject { ["$schemaVersion"] = 1, ["shots"] = new JArray(shot) }.ToString());
            Debug.Log($"[RecorderKit] Teach: saved {_steps.Count} press(es) to {path}");
            return path;
        }
    }

    /// <summary>The per-frame watcher Teach adds while it records. Reads the pointer; changes nothing.</summary>
    [AddComponentMenu("")]
    internal sealed class TeachProbe : MonoBehaviour
    {
        private bool _wasDown;
        private float _snapshotAt = -1;

        private void Update()
        {
            var (down, pos) = TeachPointer.Read();
            if (down && !_wasDown) TeachRecorder.OnPress(pos);
            if (!down && _wasDown) { TeachRecorder.OnRelease(); _snapshotAt = Time.unscaledTime + 1.0f; }
            _wasDown = down;
            if (_snapshotAt > 0 && Time.unscaledTime >= _snapshotAt)
            {
                TeachRecorder.SnapshotAfterLast();
                _snapshotAt = -1;
            }
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
