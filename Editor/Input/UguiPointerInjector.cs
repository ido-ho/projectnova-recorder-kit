using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// EventSystem-dispatched pointer — the fallback behind <see cref="InputSystemPointerInjector"/>
    /// for games WITHOUT com.unity.inputsystem. Legacy UnityEngine.Input has no injection seam at
    /// all, so this cannot make the game's own Input polling see anything; what it CAN do is walk
    /// the full ugui pointer protocol — down / initializePotentialDrag / beginDrag / drag / endDrag
    /// / up / click — through EventSystem.RaycastAll + ExecuteEvents, which reaches every
    /// ScrollRect, Slider and Button in the game. Measured need, Rogue Legend 2026-08-12: `drag`
    /// (scrolling an inventory grid) was structurally unreachable in a legacy-input game while
    /// tap-at worked fine, because tap-at already used exactly this dispatch mechanism.
    ///
    /// Same queue-and-pump contract the interface demands: ONE event per editor tick. A gesture
    /// collapsed into a single frame is invisible to handlers that compare positions across frames
    /// (ScrollRect's velocity math included), so Press/Move/Release only QUEUE, and a caller waits
    /// on the gesture's EFFECT, never on these returning true.
    /// </summary>
    public sealed class UguiPointerInjector : IPointerInjector
    {
        private enum Phase { Down, Move, Up }

        private readonly Queue<(Phase Phase, Vector2 Pos)> _pending = new();
        private bool _pumping;

        private PointerEventData? _ped;
        private GameObject? _pressTarget;
        private GameObject? _dragTarget;
        private bool _dragging;
        private Vector2 _lastPos;

        public UguiPointerInjector()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingPlayMode) return;
            _pending.Clear();
            ResetGesture();
            StopPump();
        }

        public bool Available => Application.isPlaying && EventSystem.current != null;

        public string Unavailable =>
            !Application.isPlaying
                ? "not in Play Mode — no EventSystem to dispatch through (tier: ugui)"
                : "EventSystem.current is NULL — nothing can receive pointer events (tier: ugui)";

        public bool Press(Vector2 p) => Enqueue(Phase.Down, p);
        public bool Move(Vector2 p) => Enqueue(Phase.Move, p);
        public bool Release(Vector2 p) => Enqueue(Phase.Up, p);

        private bool Enqueue(Phase phase, Vector2 p)
        {
            if (!Available) return false;
            _pending.Enqueue((phase, p));
            if (!_pumping)
            {
                _pumping = true;
                EditorApplication.update += Pump;
            }
            return true;
        }

        private void Pump()
        {
            if (_pending.Count == 0)
            {
                StopPump();
                return;
            }
            var es = EventSystem.current;
            if (es == null)
            {
                // The scene changed under a queued gesture. Dropping it is the honest move — there
                // is nothing left that could receive the remaining events.
                _pending.Clear();
                ResetGesture();
                StopPump();
                return;
            }
            var (phase, pos) = _pending.Dequeue();
            switch (phase)
            {
                case Phase.Down: DoDown(es, pos); break;
                case Phase.Move: DoMove(pos); break;
                case Phase.Up: DoUp(pos); break;
            }
        }

        private void StopPump()
        {
            if (!_pumping) return;
            EditorApplication.update -= Pump;
            _pumping = false;
        }

        private void DoDown(EventSystem es, Vector2 p)
        {
            _ped = new PointerEventData(es)
            {
                position = p,
                pressPosition = p,
                button = PointerEventData.InputButton.Left,
            };
            var results = new List<RaycastResult>();
            es.RaycastAll(_ped, results);
            if (results.Count == 0)
            {
                // A press over nothing is a valid gesture start (background pan); keep the event
                // data so Moves still track, but there is no target to dispatch to.
                _pressTarget = null;
                _dragTarget = null;
                _dragging = false;
                _lastPos = p;
                return;
            }
            var hit = results[0];
            _ped.pointerCurrentRaycast = hit;
            _ped.pointerPressRaycast = hit;
            _pressTarget = ExecuteEvents.ExecuteHierarchy(hit.gameObject, _ped, ExecuteEvents.pointerDownHandler);
            _ped.pointerPress = _pressTarget;
            // The drag owner is found the way real pointer input finds it: nearest ancestor of the
            // raycast hit implementing IDragHandler (the raycast usually hits a child Image).
            _dragTarget = ExecuteEvents.GetEventHandler<IDragHandler>(hit.gameObject);
            _ped.pointerDrag = _dragTarget;
            if (_dragTarget != null)
                ExecuteEvents.Execute(_dragTarget, _ped, ExecuteEvents.initializePotentialDrag);
            _dragging = false;
            _lastPos = p;
        }

        private void DoMove(Vector2 p)
        {
            if (_ped == null) return;
            _ped.delta = p - _lastPos;
            _ped.position = p;
            _lastPos = p;
            if (_dragTarget == null) return;
            if (!_dragging)
            {
                _ped.dragging = true;
                ExecuteEvents.Execute(_dragTarget, _ped, ExecuteEvents.beginDragHandler);
                _dragging = true;
            }
            ExecuteEvents.Execute(_dragTarget, _ped, ExecuteEvents.dragHandler);
        }

        private void DoUp(Vector2 p)
        {
            if (_ped == null) return;
            _ped.delta = p - _lastPos;
            _ped.position = p;
            if (_dragging && _dragTarget != null)
                ExecuteEvents.Execute(_dragTarget, _ped, ExecuteEvents.endDragHandler);
            if (_pressTarget != null)
                ExecuteEvents.Execute(_pressTarget, _ped, ExecuteEvents.pointerUpHandler);
            if (!_dragging && _pressTarget != null)
            {
                // No drag happened, so this is a tap: click the nearest click handler, exactly as
                // tap-at does. A drag that DID happen must not also click — real input suppresses
                // the click once the drag threshold is crossed, and so do we.
                var click = ExecuteEvents.GetEventHandler<IPointerClickHandler>(_pressTarget);
                if (click != null)
                    ExecuteEvents.Execute(click, _ped, ExecuteEvents.pointerClickHandler);
            }
            ResetGesture();
        }

        private void ResetGesture()
        {
            _ped = null;
            _pressTarget = null;
            _dragTarget = null;
            _dragging = false;
        }
    }

    /// <summary>
    /// Routes each gesture to the first AVAILABLE injector: the real Input System backend when the
    /// package exists (it reaches non-ugui content too), else the ugui dispatch fallback. Both
    /// unavailable → both reasons, so the operator sees the whole picture in one message.
    /// </summary>
    public sealed class PreferredPointerInjector : IPointerInjector
    {
        private readonly IPointerInjector _primary;
        private readonly IPointerInjector _fallback;

        public PreferredPointerInjector(IPointerInjector primary, IPointerInjector fallback)
        {
            _primary = primary;
            _fallback = fallback;
        }

        private IPointerInjector Active => _primary.Available ? _primary : _fallback;

        public bool Available => _primary.Available || _fallback.Available;

        public string Unavailable => $"{_primary.Unavailable}; fallback: {_fallback.Unavailable}";

        public bool Press(Vector2 p) => Active.Press(p);
        public bool Move(Vector2 p) => Active.Move(p);
        public bool Release(Vector2 p) => Active.Release(p);
    }
}
