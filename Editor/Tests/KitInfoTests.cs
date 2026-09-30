using System.Text.RegularExpressions;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    public class KitInfoTests
    {
        [Test]
        public void Version_IsSet()
        {
            // Shape only, on purpose. The EXACT match KitInfo.Version == package.json version
            // (and README #v...) is enforced against SOURCE by the renderer's
            // apps/renderer/src/game-ads/kit-version.spec.ts, which reads package.json directly.
            // A Unity editor test cannot reach the sibling package.json stably from a PackageCache
            // install, so a hard-coded literal here only drifts — it was frozen at "0.4.2" through
            // the 0.4.3 and 0.4.4 bumps and went red. Invariant 99-104: don't re-implement (and
            // let drift) a rule a source-reading gate already computes exactly.
            Assert.IsNotEmpty(KitInfo.Version);
            Assert.IsTrue(
                Regex.IsMatch(KitInfo.Version, @"^\d+\.\d+\.\d+$"),
                $"KitInfo.Version '{KitInfo.Version}' is not semver");
            Assert.AreEqual("com.projectnova.recorder-kit", KitInfo.PackageName);
        }
    }
}
