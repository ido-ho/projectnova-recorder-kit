using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Drives Unity's Input System <b>entirely by reflection</b> — no `using UnityEngine.InputSystem`,
    /// no asmdef reference, no `package.json` dependency. That is not a style preference: a
    /// compile-time `using TMPro` once broke this kit in every project without TextMeshPro, and the
    /// kit's own ugui pin can silently downgrade a consuming game's UI package. A third hard pin
    /// would repeat a known mistake. A game without the package gets a sentence, not a
    /// ReflectionTypeLoadException.
    ///
    /// Everything here is shaped by what Task 0 measured on gem-match3 (spec §4):
    ///   • ONE event per editor tick. A press+moves+release queued inside a single call left the
    ///     board byte-identical, because the game compares a later frame's position against the
    ///     press frame's and there was no later frame. Hence the queue and the pump.
    ///   • `Mouse`, not `Touchscreen` — no Touchscreen device exists in that game, and it binds
    ///     `<Mouse>/press` and `<Mouse>/position`. `buttons = 1` is the left button.
    ///   • A hover precedes the press, so <see cref="Press"/> queues two events rather than one.
    /// </summary>
    public sealed class InputSystemPointerInjector : IPointerInjector
    {
        private const ushort BUTTON_NONE = 0;
        private const ushort BUTTON_LEFT = 1;

        private const string NO_BACKEND =
            "no injectable input backend — this game has no com.unity.inputsystem package " +
            "(tier: unreachable)";

        private readonly Func<string, Type?> _resolve;
        private readonly Queue<(Vector2 Position, ushort Buttons)> _pending = new();

        private bool _resolved;
        private bool _available;
        private string _unavailable = "";
        private bool _pumping;
        /// <summary>Button state carried between events, so a Move mid-drag stays pressed.</summary>
        private ushort _buttons = BUTTON_NONE;
        /// <summary>Last fixed-update time an event was emitted at, so at most one goes out per
        /// fixed step — the unit a ProcessEventsInFixedUpdate game actually observes input in.</summary>
        private float _lastEmittedFixedTime = float.NaN;

        private object? _device;
        private Type? _mouseType;
        private PropertyInfo? _currentProp;
        private PropertyInfo? _enabledProp;
        private PropertyInfo? _lastUpdateTimeProp;
        private Type? _stateType;
        private FieldInfo? _positionField;
        private FieldInfo? _buttonsField;
        private MethodInfo? _queueStateEvent;

        /// <param name="typeResolver">Type lookup, defaulting to the kit's cached one. Injected so the
        /// absent-package path — the one behaviour that must never throw — is testable without an
        /// editor that lacks the package.</param>
        public InputSystemPointerInjector(Func<string, Type?>? typeResolver = null)
        {
            _resolve = typeResolver ?? GameReflection.FindType;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        /// <summary>
        /// A gesture still draining when Play Mode ends must not outlive it. Two concrete hazards:
        /// `Time.fixedUnscaledTime` stops advancing in edit mode, so the pump would spin forever
        /// without draining or unsubscribing; and any leftover events would fire into the first
        /// frames of the NEXT play session, where nobody asked for them. `Mouse.current` can also
        /// differ across sessions, so the resolution is dropped too.
        /// </summary>
        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingPlayMode)
                return;
            _pending.Clear();
            _buttons = BUTTON_NONE;
            _lastEmittedFixedTime = float.NaN;
            if (_pumping)
            {
                EditorApplication.update -= Pump;
                _pumping = false;
            }
            _resolved = false;
            _available = false;
            _device = null;
        }

        public bool Available
        {
            get { Resolve(); return _available; }
        }

        public string Unavailable
        {
            get { Resolve(); return _unavailable; }
        }

        public bool Press(Vector2 p)
        {
            // Hover first. Task 0's working sequence was hover(0) → press(1) → moves(1) → release(0),
            // and it is queued as two events rather than one so a caller cannot forget the hover.
            return Enqueue(p, BUTTON_NONE) && Enqueue(p, BUTTON_LEFT);
        }

        public bool Move(Vector2 p) => Enqueue(p, _buttons);

        public bool Release(Vector2 p) => Enqueue(p, BUTTON_NONE);

        /// <summary>
        /// Resolution is LAZY and cached. Lazy because the injector may be constructed before the
        /// package's assembly is loaded, and a negative answer cached from that moment would be
        /// wrong for the rest of the session. Cached because the miss path walks every loaded
        /// assembly, and this runs off the editor update loop.
        /// </summary>
        private void Resolve()
        {
            if (_resolved)
                return;
            _resolved = true;

            var inputSystem = _resolve("UnityEngine.InputSystem.InputSystem");
            var mouse = _resolve("UnityEngine.InputSystem.Mouse");
            var stateType = _resolve("UnityEngine.InputSystem.LowLevel.MouseState");
            if (inputSystem == null || mouse == null || stateType == null)
            {
                _unavailable = NO_BACKEND;
                return;
            }

            _mouseType = mouse;
            _currentProp = mouse.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            _device = _currentProp?.GetValue(null);
            if (_device == null)
            {
                // The package is present but no mouse is connected to the input system. Distinct from
                // NO_BACKEND on purpose: the fix is different, and "no package" would be a lie.
                _unavailable = "the input-system package is present but Mouse.current is null — no " +
                               "mouse device is connected, so there is nothing to drive";
                return;
            }

            // InputDevice.enabled and .lastUpdateTime are the two facts a pre-flight needs; both are
            // public on the device, so one lookup each and they are cached with everything else.
            _enabledProp = _device.GetType().GetProperty("enabled");
            _lastUpdateTimeProp = _device.GetType().GetProperty("lastUpdateTime");
            _positionField = stateType.GetField("position");
            _buttonsField = stateType.GetField("buttons");
            // QueueStateEvent is generic over the state struct, so it has to be closed over MouseState
            // before it can be invoked — calling the open definition throws.
            _queueStateEvent = inputSystem
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "QueueStateEvent"
                                     && m.IsGenericMethodDefinition
                                     && m.GetParameters().Length == 3)
                ?.MakeGenericMethod(stateType);
            // Deliberately NOT calling InputSystem.Update() per emit. In this update mode it either
            // does nothing useful — the game's own FixedUpdate is what must observe the event — or, if
            // the Game View lacks focus, resolves to an Editor update that consumes the pointer event
            // into the EDITOR's device state, eating it before the player ever sees it.
            if (_positionField == null || _buttonsField == null || _queueStateEvent == null)
            {
                _unavailable = "the input-system package is present but its API does not match what " +
                               "this kit reflects on (MouseState.position/buttons, " +
                               "InputSystem.QueueStateEvent<T>) — version drift";
                return;
            }

            _stateType = stateType;
            _available = true;
        }

        /// <summary>
        /// The timestamp every injected event carries, and the single hardest-won line in this file.
        /// It must sit inside a window with a wall on each side:
        ///
        ///   • BELOW `currentTimeForFixedUpdate` (≈ `fixedUnscaledTime`), or the fixed-update
        ///     time-slicer DEFERS it — `InputManager.cs:3062`. `-1.0` ("stamp it now") means the wall
        ///     clock, which under `Time.captureFramerate` runs away from game time, so every event is
        ///     deferred for the whole take and replays after capture ends.
        ///   • AT OR ABOVE the device's `lastUpdateTime`, or the event is DROPPED as stale —
        ///     `InputManager.cs:3248`, "we don't allow devices to go back in time", and it is a silent
        ///     drop. `0.0` is below every floor, so it works only until the physical mouse is touched
        ///     once, and is dead for the rest of the session after that.
        ///
        /// Measured live on 2026-08-06 with injection dead: `lastUpdateTime` 89.52,
        /// `fixedUnscaledTime` 327.14, `realtimeSinceStartup` 328.15 — `0.0` far under the floor,
        /// `-1.0` over the slicer's ceiling. Both stamps broken at once, for opposite reasons.
        /// </summary>
        private static double Stamp() => (double)Time.fixedUnscaledTime - 1e-4;

        /// <summary>
        /// Re-reads `Mouse.current` and checks the two conditions that make an emit pointless, so the
        /// verb can FAIL with a reason instead of reporting "queued" into the void. That silent-success
        /// was the single most expensive property of the earlier design: hours went into testing
        /// coordinates against an injector that was dropping every event.
        /// </summary>
        private bool Preflight(out string reason)
        {
            reason = "";
            _device = _currentProp?.GetValue(null) ?? _device; // devices survive focus loss, but re-read
            if (_device == null)
            {
                reason = "Mouse.current is null — no mouse device to drive";
                return false;
            }
            if (_enabledProp?.GetValue(_device) is bool enabled && !enabled)
            {
                reason = "the Mouse device is DISABLED — the Game View does not have player focus. " +
                         "This project runs backgroundBehavior=ResetAndDisableNonBackgroundDevices, so " +
                         "pointer devices are switched off while the Game View is unfocused. Click into " +
                         "the Game View and retry.";
                return false;
            }
            if (_lastUpdateTimeProp?.GetValue(_device) is double last && Stamp() < last)
            {
                reason = $"the device's clock is ahead of the game's: lastUpdateTime {last:F2} > stamp " +
                         $"{Stamp():F2}. The Input System drops events older than the last one a device " +
                         "processed, so anything queued now would be silently discarded. The physical " +
                         "mouse has been moved more recently than the game clock has reached — leave it " +
                         "alone and retry once game time passes that mark.";
                return false;
            }
            return true;
        }

        private bool Enqueue(Vector2 p, ushort buttons)
        {
            Resolve();
            if (!_available)
                return false;
            if (!Preflight(out var reason))
            {
                _unavailable = reason;
                return false;
            }
            _buttons = buttons;
            _pending.Enqueue((p, buttons));
            if (!_pumping)
            {
                _pumping = true;
                EditorApplication.update += Pump;
            }
            return true;
        }

        /// <summary>
        /// Drains at most ONE event per FIXED STEP. See the class comment for why never a loop.
        ///
        /// The unit matters and it is not the editor tick: this game reads input in FixedUpdate, so a
        /// gesture is "spread across frames" only if consecutive events fall in different fixed steps.
        /// Pacing by rendered frame was tried first and is not equivalent under capture.
        /// </summary>
        private void Pump()
        {
            if (_pending.Count == 0)
            {
                EditorApplication.update -= Pump;
                _pumping = false;
                return;
            }
            // One event per FIXED step. Not per rendered frame and not per editor tick: with
            // ProcessEventsInFixedUpdate the game reads input in FixedUpdate, so the fixed step is the
            // only unit in which "spread the gesture across frames" means anything to it.
            if (Time.fixedUnscaledTime == _lastEmittedFixedTime)
                return;
            _lastEmittedFixedTime = Time.fixedUnscaledTime;
            var (position, buttons) = _pending.Dequeue();
            Emit(position, buttons);
        }

        private void Emit(Vector2 position, ushort buttons)
        {
            if (_stateType == null || _positionField == null || _buttonsField == null
                || _queueStateEvent == null)
                return;
            // A struct reached through reflection is boxed, and SetValue mutates the box rather than
            // any copy — so the SAME boxed object must be what gets passed to QueueStateEvent.
            var state = Activator.CreateInstance(_stateType);
            if (state == null)
                return;
            _positionField.SetValue(state, position);
            _buttonsField.SetValue(state, buttons);
            // TIMESTAMP 0.0, NOT -1.0, AND THIS IS THE WHOLE BALLGAME UNDER CAPTURE.
            //
            // -1.0 means "stamp it now", and "now" is the native WALL clock. With
            // updateMode=ProcessEventsInFixedUpdate the manager time-slices: it defers any event whose
            // stamp is >= currentTimeForFixedUpdate, which is a GAME-clock quantity
            // (Time.fixedUnscaledTime + offset). Interactively those two clocks stay within a fixed
            // step of each other, so an event lands on the next FixedUpdate and everything works.
            // Under Time.captureFramerate = 60 the game clock advances 16.7ms per frame while the wall
            // clock advances the real render-and-encode time, so the gap grows monotonically and EVERY
            // event stays ahead of EVERY fixed window for the whole take. The events are not dropped —
            // they sit in the buffer and replay once capture ends and the clocks reconverge, which is
            // why a human watching sees the gesture play while every captured frame shows nothing
            // happened. A stamp of 0.0 is unconditionally in the past, so it is never deferred.
            _queueStateEvent.Invoke(null, new[] { _device, state, (object)Stamp() });
        }
    }
}
