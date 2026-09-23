using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// A virtual pointer the kit can drive, for content the EventSystem cannot reach — the `backend`
    /// tier of <see cref="InputTierKind"/>.
    ///
    /// Every method QUEUES its event(s) and returns immediately. It does not return "the gesture
    /// happened", because a gesture cannot happen inside one call: gem-match3's own handler stores
    /// the press position on one frame and compares a later position against it on a LATER frame, so
    /// a press+moves+release collapsed into a single update leaves the board byte-identical. Task 0
    /// measured exactly that. The queue is drained one event per editor tick; a caller that needs the
    /// gesture finished waits on its EFFECT (a settle condition), never on these returning true.
    ///
    /// The bool is "was this queued", and false always means <see cref="Available"/> is false.
    /// </summary>
    public interface IPointerInjector
    {
        /// <summary>False when this game has no injectable backend. Never throws to say so.</summary>
        bool Available { get; }

        /// <summary>Operator-facing reason <see cref="Available"/> is false; empty when it is true.</summary>
        string Unavailable { get; }

        /// <summary>Queue a button-down at <paramref name="p"/> (screen pixels, origin bottom-left).</summary>
        bool Press(Vector2 p);

        /// <summary>Queue a move to <paramref name="p"/>, holding whatever button state is current.</summary>
        bool Move(Vector2 p);

        /// <summary>Queue a button-up at <paramref name="p"/>.</summary>
        bool Release(Vector2 p);
    }
}
