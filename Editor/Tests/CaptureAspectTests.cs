using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// Guards the guard. Its entire value is refusing to stay quiet when footage will be wrong, so the
    /// cases that matter are "warns when it should" and "does not cry wolf".
    ///
    /// The live size is whatever the test runner's Game View happens to be, so these tests drive the
    /// pure comparison by choosing TARGETS relative to it rather than asserting on it.
    ///
    /// `Live` MUST read CaptureAspect.GameViewSize() — the same source the code under test reads — and
    /// not Screen.width/height. This originally used Screen, and the first time the suite was ever
    /// executed (2026-07-29) Matching_AspectIsSilent failed: Screen reported the editor WINDOW at
    /// 1600x1296 while the Game View rendered 1080x1920, so the "matching" case handed the guard
    /// genuinely mismatched numbers and the guard correctly warned. The production code was right and
    /// the test was wrong — which is the exact error CaptureAspect's own summary warns about ("measure
    /// the ARTIFACT, not an instrument you assume reflects it"), committed in the test written to
    /// protect it. A test comparing against a different source than its subject is not a test.
    /// </summary>
    public class CaptureAspectTests
    {
        private static (int w, int h) Live => CaptureAspect.GameViewSize();

        [Test]
        public void Matching_AspectIsSilent()
        {
            var (w, h) = Live;
            if (w <= 0 || h <= 0)
                Assert.Pass("no meaningful Game View size in this context");

            // Same aspect, different absolute size — a pure rescale is fine and must not warn.
            Assert.IsNull(CaptureAspect.Mismatch(w, h));
            Assert.IsNull(CaptureAspect.Mismatch(w * 2, h * 2));
        }

        [Test]
        public void Mismatched_AspectWarnsAndNamesTheFix()
        {
            var (w, h) = Live;
            if (w <= 0 || h <= 0)
                Assert.Pass("no meaningful Game View size in this context");

            // Deliberately wrong: swap the axes, which is the worst realistic case (portrait vs
            // landscape) unless the view happens to be square.
            if (w == h)
                Assert.Pass("square Game View cannot express an axis-swap mismatch");

            var warning = CaptureAspect.Mismatch(h, w);
            Assert.IsNotNull(warning);
            StringAssert.Contains("CAPTURE ASPECT MISMATCH", warning);
            // The operator needs to know WHAT will be wrong and WHERE to fix it, not just that
            // something is off — the failure it describes looks like a capture bug otherwise.
            StringAssert.Contains("WORLD-SPACE", warning);
            StringAssert.Contains("BEFORE entering Play Mode", warning);
        }

        [Test]
        public void UnknownSizes_AreNotReportedAsMismatches()
        {
            // 0x0 is how a headless or fake driver says "no fixed output"; that is not a mismatch, and
            // treating it as one would make every unit test log a spurious warning.
            Assert.IsNull(CaptureAspect.Mismatch(0, 0));
            Assert.IsNull(CaptureAspect.Mismatch(-1, 100));
        }
    }
}
