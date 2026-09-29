using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Guards the one capture failure that produces a clip which passes every check and is still
    /// unusable: a Game View whose aspect differs from the capture target.
    ///
    /// WHY THIS IS ITS OWN GUARD. The Unity Recorder path captures the Game View but RESCALES it to a
    /// fixed output size (1080x1920). Games lay out world-space content from the screen aspect they see
    /// at load, so when the two aspects disagree the world can fall partly outside the captured frame
    /// while canvas UI, which has a CanvasScaler, keeps fitting. Nothing automated notices: the state
    /// settle passes, the shot's anchors pass, captured:true, and even Phase 0's screenshot proof passes
    /// because screenshots use the other capture path. Only opening the clip reveals it.
    ///
    /// HONEST SCOPE — this guard was written while chasing a missing match-3 board, and that turned out
    /// NOT to be an aspect problem: matching the aspects exactly changed nothing, and the real cause was
    /// the capture PATH (Unity Recorder dropping world-space rendering on a URP project — see
    /// RecorderDrivers.For). The guard is kept because the failure it describes is real and the check is
    /// nearly free, but do not treat a clean aspect as proof that framing is the whole story. The only
    /// proof a shot captured its subject is vision-reading the CLIP.
    ///
    /// This deliberately DETECTS and reports rather than fixing automatically. Setting the Game View
    /// size requires reflection into UnityEditor internals (GameViewSizes / GameView.selectedSizeIndex)
    /// whose shape differs across editor versions, and a silent best-effort mutation that quietly stops
    /// working on some future version would restore exactly the silent failure this guard exists to
    /// remove. A loud, accurate warning that names the fix is worth more than a fragile automation.
    /// </summary>
    public static class CaptureAspect
    {
        /// <summary>Aspect ratios within this fraction of each other are treated as matching.</summary>
        private const float TOLERANCE = 0.02f;

        /// <summary>
        /// The Game View's RENDER size — which is not Screen.width/height.
        ///
        /// This distinction cost a false alarm from this very guard: with a fixed Game View resolution
        /// selected, Screen.width/height report the editor WINDOW (937x991 in the case that caught it)
        /// while the view actually renders at the chosen resolution (1080x1920, confirmed by measuring a
        /// captured PNG). Comparing against Screen therefore warned about a correctly-configured project.
        /// Handles.GetMainGameViewSize() is the render target, which is what capture uses.
        ///
        /// The general lesson, which the guard itself violated: measure the ARTIFACT, not an instrument
        /// you assume reflects it.
        /// </summary>
        public static (int W, int H) GameViewSize()
        {
            try
            {
                var size = UnityEditor.Handles.GetMainGameViewSize();
                if (size.x >= 1f && size.y >= 1f)
                    return ((int)size.x, (int)size.y);
            }
            catch (System.Exception)
            {
                // No main game view (headless/batch); fall through.
            }
            return (Screen.width, Screen.height);
        }

        /// <summary>
        /// Null when the live Game View aspect matches <paramref name="targetW"/>x<paramref name="targetH"/>;
        /// otherwise an operator-facing explanation of what will be wrong with the footage and how to fix it.
        /// Call from Play Mode — outside it, Screen has no meaningful size and this returns null.
        /// </summary>
        public static string? Mismatch(int targetW, int targetH)
        {
            var (liveW, liveH) = GameViewSize();
            if (liveW <= 0 || liveH <= 0 || targetW <= 0 || targetH <= 0)
                return null;

            var live = (float)liveW / liveH;
            var target = (float)targetW / targetH;
            if (Mathf.Abs(live - target) <= TOLERANCE * target)
                return null;

            return $"CAPTURE ASPECT MISMATCH: the Game View renders {liveW}x{liveH} (aspect {live:0.000}) but " +
                   $"capture outputs {targetW}x{targetH} (aspect {target:0.000}). The recorder rescales " +
                   "the Game View, and this game laid its WORLD-SPACE content out for the Game View's " +
                   "aspect at load — so expect world content to sit partly or wholly OUTSIDE the frame " +
                   "while canvas UI still looks correct. Fix it in the Game View's resolution dropdown " +
                   $"(add/select {targetW}x{targetH}) BEFORE entering Play Mode, so the game lays out for " +
                   "the aspect that will be captured, then re-run the shot.";
        }
    }
}
