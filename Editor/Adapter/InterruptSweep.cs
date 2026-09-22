using System;
using System.Collections;
using System.Collections.Generic;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// The recovery shape every game's IRecoveryPolicy converges on: poll against a deadline, and on
    /// each tick run AT MOST ONE branch — the first whose condition holds — until an independent
    /// verify predicate says the game is back somewhere capturable.
    ///
    /// Extracted after reading all three real policies (SnlRecoveryPolicy, RlRecoveryPolicy,
    /// GemRecoveryPolicy), and the signature is shaped by what they actually need rather than by the
    /// simplest of them:
    ///
    ///  - `clear` returns IEnumerable, not void: a real clear action routinely WAITS. Rogue Legend's
    ///    MOVING branch waits out a transient animating state; its auto-resolve branch waits up to
    ///    25s for idle; gemmatch3's scene reload waits 2s. A non-yielding Action cannot express any
    ///    of them, and a fire-and-forget version would re-fire mid-transition.
    ///
    ///  - `verify` is its OWN predicate, never derived from the branch list. Rogue Legend's final
    ///    check deliberately differs from its branches: it checks RESUME_ABANDON while a branch
    ///    clicks RESUME_CONFIRM, and omits several branch conditions entirely. "None of the branches
    ///    still match" would be the wrong question there.
    ///
    ///  - conditions are plain predicates, not WaitConditions: gemmatch3 branches on a scene-name
    ///    PREFIX (CurrentStateName?.StartsWith(...)), which WaitCondition.State's exact match cannot
    ///    express. A WaitCondition-based branch is just `c => cond.IsMet(c.Ui, c.State)`.
    ///
    ///  - one-shot guards stay with the CALLER, as an ordinary captured bool — exactly how
    ///    RlRecoveryPolicy's `triedResolve` and GemRecoveryPolicy's `reloaded` already work. Baking
    ///    once-only into the primitive would add a concept none of the three policies need from it.
    ///
    /// Sets ctx.LastOpSucceeded = verify(ctx) on every exit path, satisfying IRecoveryPolicy's
    /// contract (AdDirector reads it to decide whether recovery worked).
    /// </summary>
    public static class InterruptSweep
    {
        public static IEnumerable Run(
            DirectorContext ctx,
            IReadOnlyList<(Func<DirectorContext, bool> when, Func<DirectorContext, IEnumerable> clear)> branches,
            Func<DirectorContext, bool> verify,
            double deadline,
            double pollInterval = 0.6)
        {
            while (ctx.Now() < deadline && !verify(ctx))
            {
                foreach (var (when, clear) in branches)
                {
                    if (!when(ctx)) continue;
                    foreach (var _ in clear(ctx)) yield return null;
                    break; // one branch per tick — every real policy is an if/else-if chain
                }
                foreach (var _ in ctx.WaitSeconds(pollInterval)) yield return null;
            }
            ctx.LastOpSucceeded = verify(ctx);
        }
    }
}
