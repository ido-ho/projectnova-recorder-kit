using System.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>The Nova Capture window's "Find" box (kit 0.13.3): a Rogue Legend cheat search proposed ~100 lever rows and
    /// the window had no scroll view, so the three cheats the owner needed could not be reached to tick.</summary>
    public class LeverFilterTests
    {
        private static readonly (string, bool)[] Rows =
        {
            ("cheat-registry", false),
            ("call PocketRoll.DevConsole.DevConsoleActions.Invoke \"Skip 1 Level\"", false),
            ("call PocketRoll.DevConsole.DevConsoleActions.Invoke \"Give me 10k topaz pls\"", false),
            ("call PocketRoll.DevConsole.DevConsoleActions.Invoke \"Reset user\"", true),
        };

        [Test]
        public void ABlankFilter_ListsEveryRow()
        {
            Assert.AreEqual(4, LeverFilter.Visible(Rows, "").Count);
            Assert.AreEqual(4, LeverFilter.Visible(Rows, "   ").Count);
            Assert.AreEqual(4, LeverFilter.Visible(Rows, null).Count);
        }

        [Test]
        public void AFilter_MatchesAnyPartOfTheCommand_IgnoringCase()
        {
            var shown = LeverFilter.Visible(Rows, "topaz");
            Assert.IsTrue(shown.Contains(Rows[2].Item1));
            Assert.IsFalse(shown.Contains(Rows[1].Item1));
            Assert.IsTrue(LeverFilter.Visible(Rows, "SKIP 1").Contains(Rows[1].Item1));
        }

        [Test]
        public void ATickedRow_IsNeverHiddenByTheFilter()
        {
            var shown = LeverFilter.Visible(Rows, "topaz");
            Assert.IsTrue(shown.Contains(Rows[3].Item1), "a ticked row must stay visible so it can be unticked");
            Assert.AreEqual(2, shown.Count);
        }
    }
}
