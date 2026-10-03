using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// F0 (fix-everything plan: "$0 tests need the real RL export on the laptop") — the kit option "keep a copy of the last
    /// export". Off by default; when on, a fully uploaded build is moved aside instead of deleted, replacing the copy kept
    /// before, somewhere the orphan sweep never reaches.
    /// </summary>
    public class ExportKeepCopyTests
    {
        private string _root = "";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "novakit-keepcopy-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private static void Touch(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        [Test]
        public void AnUploadedBuild_IsMovedAside_ReplacingTheEarlierCopy()
        {
            var build = CapturePaths.ExportDir(_root, "run-2");
            var keep = CapturePaths.LastExportDir(_root);
            Touch(Path.Combine(build, "part-000.zip"), "new");
            Touch(Path.Combine(build, "header.json"), "{}");
            Touch(Path.Combine(keep, "part-000.zip"), "old");
            Touch(Path.Combine(keep, "stale-only-in-old.zip"), "old");

            Assert.IsNull(ExportOnDisk.KeepCopy(build, keep));
            Assert.IsFalse(Directory.Exists(build), "moved, not copied — nothing is left for the sweep");
            Assert.AreEqual("new", File.ReadAllText(Path.Combine(keep, "part-000.zip")));
            Assert.IsTrue(File.Exists(Path.Combine(keep, "header.json")));
            Assert.IsFalse(File.Exists(Path.Combine(keep, "stale-only-in-old.zip")), "the earlier copy is replaced whole");
        }

        [Test]
        public void NothingToMove_LeavesTheEarlierCopyAlone()
        {
            var keep = CapturePaths.LastExportDir(_root);
            Touch(Path.Combine(keep, "part-000.zip"), "old");
            Assert.IsNotNull(ExportOnDisk.KeepCopy(CapturePaths.ExportDir(_root, "gone"), keep),
                "a resumed `done` whose build was already moved says why and touches nothing");
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(keep, "part-000.zip")));
        }

        [Test]
        public void TheKeptCopy_IsOutsideTheExportRoot_SoTheOrphanSweepNeverDeletesIt()
        {
            var keep = CapturePaths.LastExportDir(_root);
            Touch(Path.Combine(keep, "part-000.zip"), "kept");
            Touch(Path.Combine(CapturePaths.ExportDir(_root, "orphan"), "part-000.zip"), "orphan");
            var exportRoot = CapturePaths.ExportRoot(_root);
            StringAssert.DoesNotStartWith(Path.GetFullPath(exportRoot), Path.GetFullPath(keep));
            Assert.IsTrue(ExportOnDisk.SweepOrphans(exportRoot, progressFileExists: false), "positive control: the sweep ran");
            Assert.IsFalse(Directory.Exists(exportRoot));
            Assert.IsTrue(File.Exists(Path.Combine(keep, "part-000.zip")));
        }

        /// <summary>Audit L4 — a leftover from an earlier keep whose clean-up failed (here: a FILE where the aside folder would
        /// go, which a directory delete cannot remove) must not stop the next keep.</summary>
        [Test]
        public void ALeftoverFromAnEarlierKeep_DoesNotStopTheNextOne()
        {
            var build = CapturePaths.ExportDir(_root, "run-3");
            var keep = CapturePaths.LastExportDir(_root);
            Touch(Path.Combine(build, "part-000.zip"), "new");
            Touch(Path.Combine(keep, "part-000.zip"), "old");
            File.WriteAllText(keep + ".old", "a leftover nothing could delete");
            Assert.IsNull(ExportOnDisk.KeepCopy(build, keep));
            Assert.AreEqual("new", File.ReadAllText(Path.Combine(keep, "part-000.zip")));
        }

        /// <summary>The failure path: the move cannot happen (a FILE sits where the kept folder goes). A reason comes back,
        /// nothing throws, the build is untouched (`done` still removes it) and what was there stays.</summary>
        [Test]
        public void AMoveThatCannotHappen_SaysWhy_AndTouchesNothing()
        {
            var build = CapturePaths.ExportDir(_root, "run-4");
            var keep = CapturePaths.LastExportDir(_root);
            Touch(Path.Combine(build, "part-000.zip"), "new");
            Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
            File.WriteAllText(keep, "not a folder");
            Assert.IsNotNull(ExportOnDisk.KeepCopy(build, keep));
            Assert.IsTrue(File.Exists(Path.Combine(build, "part-000.zip")));
            Assert.AreEqual("not a folder", File.ReadAllText(keep));
        }

        [Test]
        public void TheOptionIsOffByDefault()
        {
            var pref = NovaCaptureAgent.PrefKeyForTests("KeepLastExport");
            var had = EditorPrefs.HasKey(pref);
            var old = EditorPrefs.GetBool(pref, false);
            try
            {
                EditorPrefs.DeleteKey(pref);
                Assert.IsFalse(NovaCaptureAgent.KeepLastExport);
                NovaCaptureAgent.KeepLastExport = true;
                Assert.IsTrue(NovaCaptureAgent.KeepLastExport, "positive control: the setting reads back");
            }
            finally
            {
                if (had) EditorPrefs.SetBool(pref, old); else EditorPrefs.DeleteKey(pref);
            }
        }

        /// <summary>Structural guard (FinishExport has no seam a pure test can drive): only a FULLY uploaded export is kept,
        /// and it is moved BEFORE `done` — ReportDone deletes the export root once the server accepts it.</summary>
        [Test]
        public void FinishExport_KeepsOnlyAFullyUploadedBuild_BeforeDoneRemovesIt()
        {
            var src = File.ReadAllText(Path.Combine(KitHygieneTests.PackageRoot(), "Editor", "Cloud", "NovaCaptureAgent.cs"));
            var start = src.IndexOf("private static IEnumerable FinishExport(", StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0);
            var guard = src.IndexOf("if (KeepLastExport && progress.ResultPosted", start, StringComparison.Ordinal);
            var keep = src.IndexOf("ExportOnDisk.KeepCopy(", start, StringComparison.Ordinal);
            var done = src.IndexOf("ReportDone(progress, progressFile)", start, StringComparison.Ordinal);
            Assert.Greater(guard, start);
            Assert.Greater(keep, guard);
            Assert.Greater(done, keep, "moved before `done` deletes it");
        }
    }
}
