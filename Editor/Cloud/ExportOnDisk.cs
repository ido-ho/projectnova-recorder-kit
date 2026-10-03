using System;
using System.IO;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Slice D part 1 — a built export as it sits on the studio's disk between "the build
    /// finished" and "the server accepted done". Pure System.IO: no Unity API, so the kit's
    /// EditMode tests cover the resume rule that runs in production.
    ///
    /// THE RULE: a build is only RESUMED if it is provably whole. A domain reload can land at any
    /// line of the build, and a studio can clear Library/ whenever it likes — so after a reload
    /// the agent does not trust its own "built" flag. It re-reads header.json (which the exporter
    /// writes LAST) and checks that every part the header names is on disk at the size the header
    /// says. Anything less is debris, and the export is rebuilt rather than uploaded short — an
    /// upload the server would then refuse as "received N of M", minutes and megabytes later.
    /// </summary>
    public static class ExportOnDisk
    {
        public const string HeaderFileName = "header.json";

        /// <summary>The header of a whole, finished build under <paramref name="outDir"/>, or null
        /// with the reason. Never throws.</summary>
        public static ExportHeader? LoadVerified(string outDir, out string? why)
        {
            why = null;
            try
            {
                var headerPath = Path.Combine(outDir, HeaderFileName);
                if (!File.Exists(headerPath)) { why = "no header.json — the build never finished"; return null; }
                var header = ExportHeader.FromJson(File.ReadAllText(headerPath));
                if (header == null) { why = "header.json is unreadable"; return null; }
                if (header.Parts.Count == 0) { why = "header.json names no parts"; return null; }
                foreach (var part in header.Parts)
                {
                    // The name becomes a path: only the exporter's own shape is followed.
                    if (part.Name != ExportFormat.PartName(part.Index))
                    { why = $"header.json names part {part.Index} as '{part.Name}'"; return null; }
                    var path = Path.Combine(outDir, part.Name);
                    if (!File.Exists(path)) { why = part.Name + " is missing"; return null; }
                    var bytes = new FileInfo(path).Length;
                    if (bytes != part.Bytes)
                    { why = $"{part.Name} is {bytes} bytes on disk; the header says {part.Bytes}"; return null; }
                }
                return header;
            }
            catch (Exception e)
            {
                why = "could not read the build: " + e.Message;
                return null;
            }
        }

        /// <summary>
        /// THE ORPHAN SWEEP. An export build is up to a gigabyte under `Library/AdRelay/export/`,
        /// and it used to be removed ONLY when the server accepted `done`. Every other way a run
        /// can end left it there for good: the owner cancels (the kit learns on a 409), the `done`
        /// answer is lost, the studio unticks "run capture jobs" and never re-ticks (fresh-context
        /// audit K2, 2026-09-21).
        ///
        /// So the poll sweeps before it asks for work. ONE JOB RUNS AT A TIME and the progress file
        /// is what says one is in flight — with no progress file, nothing under `export/` belongs to
        /// anybody. Returns whether it deleted something, so the agent can say so.
        ///
        /// Pure enough to unit-test, which the routine that calls it is not.
        /// </summary>
        public static bool SweepOrphans(string exportRoot, bool progressFileExists)
        {
            if (progressFileExists) return false;          // a job IS in flight; that build is its
            if (string.IsNullOrEmpty(exportRoot)) return false;
            try { if (!Directory.Exists(exportRoot)) return false; }
            catch (Exception) { return false; }
            TryDeleteDir(exportRoot);
            return true;
        }

        /// <summary>
        /// F0 (plan "$0 tests need the real export on the laptop") — KEEP A COPY OF THE LAST EXPORT. With the studio's
        /// option on, a fully uploaded build is MOVED (a rename on the same disk, never a gigabyte copy in the editor's
        /// tick) to <paramref name="keepDir"/>, replacing the copy kept before. <paramref name="keepDir"/> must be OUTSIDE
        /// the export root, or the orphan sweep deletes it on the next poll. Nothing to move (a resumed `done` whose build
        /// was already moved) leaves the earlier copy alone. Never throws: returns null when it moved, else why not.
        /// </summary>
        public static string? KeepCopy(string buildDir, string keepDir)
        {
            // the earlier copy is set aside, not deleted, until the new one is in place — a move that fails puts it back.
            // Audit L4: under a name of its own each time, so a leftover aside nothing could delete never blocks a keep.
            var aside = keepDir + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                if (!Directory.Exists(buildDir)) return "there was no build to keep";
                var parent = Path.GetDirectoryName(keepDir);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                    // best-effort: asides an earlier keep could not remove
                    foreach (var stale in Directory.GetDirectories(parent, Path.GetFileName(keepDir) + ".old*"))
                        TryDeleteDir(stale);
                }
                if (Directory.Exists(keepDir)) Directory.Move(keepDir, aside);
                try { Directory.Move(buildDir, keepDir); }
                catch (Exception)
                {
                    try { if (Directory.Exists(aside) && !Directory.Exists(keepDir)) Directory.Move(aside, keepDir); }
                    catch (Exception) { /* the earlier copy stays under .old */ }
                    throw;
                }
                TryDeleteDir(aside);
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        /// <summary>Best-effort and silent: a build dir that cannot be removed is disk space, not a
        /// failed job, and this runs on paths that must not throw (inside the agent's pump).</summary>
        public static void TryDeleteDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception)
            {
                // Locked by an antivirus scan or an open Finder window; the next export sweeps it.
            }
        }
    }
}
