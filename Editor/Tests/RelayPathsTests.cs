using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    public class RelayPathsTests
    {
        private string _root = "";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "novakit-relaypaths-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            AtomicFile.ReplaceForTests = null;
            AtomicFile.MoveForTests = null;
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void ResolveRoot_DefaultsToLibrarySoTheStudioRepoStaysClean()
        {
            var resolved = RelayPaths.ResolveRoot(_root);
            Assert.AreEqual(Path.Combine(_root, "Library", "AdRelay"), resolved);
        }

        [Test]
        public void ResolveRoot_KeepsALegacyAdRelaySession()
        {
            var legacy = Path.Combine(_root, RelayPaths.RootDirName);
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "status.json"), "{}");
            Assert.AreEqual(legacy, RelayPaths.ResolveRoot(_root));
        }

        [Test]
        public void ResolveRoot_PrefersLibraryOnceThatSessionExists()
        {
            var preferred = Path.Combine(_root, "Library", "AdRelay");
            var legacy = Path.Combine(_root, RelayPaths.RootDirName);
            Directory.CreateDirectory(preferred);
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(preferred, "status.json"), "{}");
            File.WriteAllText(Path.Combine(legacy, "status.json"), "{}");
            Assert.AreEqual(preferred, RelayPaths.ResolveRoot(_root));
        }

        [Test]
        public void EnsureDirs_CreatesTheResolvedTree()
        {
            RelayPaths.EnsureDirs(_root);
            Assert.IsTrue(Directory.Exists(RelayPaths.Commands(_root)));
            Assert.IsTrue(RelayPaths.Commands(_root).Replace('\\', '/').Contains("Library/AdRelay"));
        }

        /// <summary>
        /// K1 (second audit), re-pinned by the third: AtomicFile's whole reason to exist is
        /// "replace, never delete-then-move". The second audit's seam replaced the swap WHOLE, so
        /// the body that decides between the two OS calls never ran under test — turning it into
        /// delete-then-move left the suite green (surviving mutant S1). Now only the two OS calls
        /// are injectable, the swap's own branch runs, and these three say what it must do: REPLACE
        /// a file that is there (and never delete it first), MOVE to a path that is not, and when a
        /// replace fails leave the old file there and whole with nothing moved over it.
        /// (That <c>File.Replace</c> itself keeps the old bytes on failure is the OS's promise —
        /// exercised by the real call elsewhere in this suite, on macOS only.)
        /// </summary>
        private (List<string> replaced, List<string> moved) RecordTheOsCalls(System.Action<string>? atReplace = null)
        {
            var replaced = new List<string>();
            var moved = new List<string>();
            AtomicFile.ReplaceForTests = (tmp, dest) =>
            {
                replaced.Add(dest);
                atReplace?.Invoke(dest);
                File.Replace(tmp, dest, null);
            };
            AtomicFile.MoveForTests = (tmp, dest) =>
            {
                moved.Add(dest);
                File.Move(tmp, dest);
            };
            return (replaced, moved);
        }

        [Test]
        public void AtomicWrite_OverAFileThatIsThere_ReplacesItAndNeverDeletesItFirst()
        {
            var path = Path.Combine(_root, "status.json");
            const string old = "{\"old\":true}";
            File.WriteAllText(path, old);
            var oldWasThereAtTheReplace = false;
            var (replaced, moved) = RecordTheOsCalls(dest =>
                oldWasThereAtTheReplace = File.Exists(dest) && File.ReadAllText(dest) == old);

            AtomicFile.Write(path, "{\"new\":true}");

            CollectionAssert.AreEqual(new[] { path }, replaced, "the swap did not REPLACE the file that was there");
            CollectionAssert.IsEmpty(moved, "the swap MOVED onto a path that already held a file");
            Assert.IsTrue(oldWasThereAtTheReplace,
                "the old file was already gone when the new one was put in its place — delete-then-move");
            Assert.AreEqual("{\"new\":true}", File.ReadAllText(path));
            Assert.IsFalse(File.Exists(path + ".tmp"), "the temp file outlived the write");
        }

        [Test]
        public void AtomicWrite_ToAPathThatIsNotThere_Moves()
        {
            var path = Path.Combine(_root, "fresh.json");
            var (replaced, moved) = RecordTheOsCalls();

            AtomicFile.Write(path, "{\"new\":true}");

            CollectionAssert.AreEqual(new[] { path }, moved, "a new file is MOVED into place");
            CollectionAssert.IsEmpty(replaced, "File.Replace has nothing to replace here");
            Assert.AreEqual("{\"new\":true}", File.ReadAllText(path));
        }

        [Test]
        public void AtomicWrite_WhenTheReplaceFails_TheOldFileIsStillThereAndWhole()
        {
            var path = Path.Combine(_root, "status.json");
            const string old = "{\"old\":true}";
            File.WriteAllText(path, old);
            var oldFileWasStillThere = false;
            var (_, moved) = RecordTheOsCalls();
            AtomicFile.ReplaceForTests = (tmp, dest) =>
            {
                oldFileWasStillThere = File.Exists(dest) && File.ReadAllText(dest) == old;
                throw new IOException("the replace was refused");
            };

            Assert.Throws<IOException>(() => AtomicFile.Write(path, "{\"new\":true}"),
                "a write that could not be completed must not report success");

            Assert.IsTrue(oldFileWasStillThere,
                "the old file was deleted before the new one was in place — a crash there leaves neither");
            Assert.AreEqual(old, File.ReadAllText(path), "the old file did not survive a failed write");
            CollectionAssert.IsEmpty(moved, "a failed replace fell back to a move over the old file");
            Assert.IsFalse(File.Exists(path + ".tmp"), "the temp file outlived the attempt");
        }
    }
}
