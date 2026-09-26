using System.IO;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// The relay's on-disk layout under the game project. MUST match the TypeScript mirror
    /// in apps/renderer/src/game-ads/relay/relay-protocol.ts byte-for-byte.
    ///
    /// Preferred root is Library/AdRelay — Unity already gitignores Library/, so a studio that
    /// adds the kit does not have to ignore a new folder or keep a recorder branch just to hide
    /// command traffic. A pre-0.3 project that already has AdRelay/status.json keeps that path
    /// so an in-flight session is not split across two trees.
    /// </summary>
    public static class RelayPaths
    {
        /// <summary>Legacy project-root folder. Kept so ResolveRoot can find a pre-0.3 session.</summary>
        public const string RootDirName = "AdRelay";
        public const string PreferredRootRelative = "Library/AdRelay";
        public const string NovaDataRelative = "Library/Nova";

        public static string ResolveRoot(string projectRoot)
        {
            var preferred = Path.Combine(projectRoot, "Library", "AdRelay");
            var legacy = Path.Combine(projectRoot, RootDirName);
            if (File.Exists(Path.Combine(preferred, "status.json")))
                return preferred;
            if (File.Exists(Path.Combine(legacy, "status.json")))
                return legacy;
            return preferred;
        }

        public static string Root(string projectRoot) => ResolveRoot(projectRoot);

        public static string NovaDir(string projectRoot) => Path.Combine(projectRoot, "Library", "Nova");
        public static string NovaShotsFile(string projectRoot) => Path.Combine(NovaDir(projectRoot), "shots.json");
        public static string Commands(string projectRoot) => Path.Combine(Root(projectRoot), "commands");
        public static string Results(string projectRoot) => Path.Combine(Root(projectRoot), "results");
        public static string Processed(string projectRoot) => Path.Combine(Root(projectRoot), "processed");
        public static string Screenshots(string projectRoot) => Path.Combine(Root(projectRoot), "screenshots");
        public static string VisionRequests(string projectRoot) => Path.Combine(Root(projectRoot), "vision", "requests");
        public static string VisionResponses(string projectRoot) => Path.Combine(Root(projectRoot), "vision", "responses");
        public static string StatusFile(string projectRoot) => Path.Combine(Root(projectRoot), "status.json");

        public static string Command(string projectRoot, string id) => Path.Combine(Commands(projectRoot), id + ".json");
        public static string Result(string projectRoot, string id) => Path.Combine(Results(projectRoot), id + ".json");
        public static string ProcessedMarker(string projectRoot, string id) => Path.Combine(Processed(projectRoot), id + ".json");
        public static string Screenshot(string projectRoot, string id) => Path.Combine(Screenshots(projectRoot), id + ".png");

        public static void EnsureDirs(string projectRoot)
        {
            Directory.CreateDirectory(Commands(projectRoot));
            Directory.CreateDirectory(Results(projectRoot));
            Directory.CreateDirectory(Processed(projectRoot));
            Directory.CreateDirectory(Screenshots(projectRoot));
            Directory.CreateDirectory(VisionRequests(projectRoot));
            Directory.CreateDirectory(VisionResponses(projectRoot));
        }
    }

    /// <summary>
    /// All relay writes go through here: temp-file-then-rename, never a partial read.
    ///
    /// REPLACE, NEVER DELETE-THEN-MOVE (audit S2, 2026-09-21). Deleting the target first leaves a
    /// window in which the file does not exist at all, and a move that then fails leaves the
    /// studio with neither the old file nor the new one. For <c>synced.json</c> — the record that
    /// makes the lever gate live — that window was a fail-OPEN: cloud shots on disk with nothing
    /// saying they came from the cloud. <c>File.Replace</c> keeps the old bytes until the new ones
    /// are in place.
    /// </summary>
    public static class AtomicFile
    {
        public static void Write(string path, string contents)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, contents);
            try
            {
                Swap(tmp, path);
            }
            catch (System.Exception)
            {
                // The temp file must not outlive the attempt: the next write would find it and a
                // stray half-document beside a real one is the thing this class exists to prevent.
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (System.Exception) { }
                throw;
            }
        }

        /// <summary>
        /// PUT THE TEMP FILE IN THE TARGET'S PLACE, IN ONE STEP. The one implementation of that
        /// sentence in the kit — <see cref="Write"/> and <c>SyncNova</c>'s byte writer both come
        /// here, so "replace, never delete-then-move" cannot be true of one and not the other.
        ///
        /// <c>File.Replace</c> over an existing target keeps the old bytes until the new ones are
        /// in place. Deleting first leaves a window where the file does not exist at all, and a
        /// move that then fails leaves the studio with neither the old file nor the new one.
        /// </summary>
        internal static void Swap(string tmp, string path)
        {
            if (File.Exists(path))
                (ReplaceForTests ?? ReplaceOnDisk)(tmp, path);
            else
                (MoveForTests ?? MoveOnDisk)(tmp, path);
        }

        private static void ReplaceOnDisk(string tmp, string path) => File.Replace(tmp, path, null);
        private static void MoveOnDisk(string tmp, string path) => File.Move(tmp, path);

        /// <summary>
        /// THE TWO OS CALLS, AND NOTHING ELSE, FOR THE TESTS (third audit, surviving mutant S1).
        /// Never set outside them — the kit never assigns either.
        ///
        /// The second audit's seam replaced <see cref="Swap"/> WHOLE, so its body never ran under
        /// test: turning that body into delete-then-move left every test green. These stand in for
        /// exactly one call each — <c>File.Replace</c> and <c>File.Move</c> — so <see cref="Swap"/>'s
        /// own branch runs, and a test can say which primitive it chose (replace over a file that is
        /// there, move to a path that is not) and that a replace which fails leaves the old file
        /// whole, with no move behind it. What they cannot pin is the OS's own promise that
        /// <c>File.Replace</c> keeps the old bytes until the new ones are in place: that is
        /// exercised by the real call, and the kit suite has run on macOS only.
        /// </summary>
        internal static System.Action<string, string>? ReplaceForTests;

        /// <summary>See <see cref="ReplaceForTests"/>.</summary>
        internal static System.Action<string, string>? MoveForTests;
    }
}
