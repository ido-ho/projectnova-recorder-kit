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

    /// <summary>All relay writes go through here: temp-file-then-rename, never a partial read.</summary>
    public static class AtomicFile
    {
        public static void Write(string path, string contents)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, contents);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
