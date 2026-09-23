using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Spec §8.10 — how many error/exception lines the game logged since Play Mode began, and the
    /// first one. The Phase 0 Doctor reported "all green" over Rogue Legend while it sat on its
    /// loading screen behind a 404 from its own login server (2026-09-23): the kit measured frames,
    /// never the console, so the one line that said why was invisible to the website.
    ///
    /// Counted from the moment Play is pressed: reset at ExitingEditMode, which fires BEFORE the
    /// first scene's Awake/OnEnable (EnteredPlayMode fires after them, so resetting there wiped
    /// exactly the boot errors this exists for — audit of 5a823c21). With domain reload on, the
    /// reload wipes the statics too and [InitializeOnLoad] subscribes again before the scene loads.
    /// Thread-safe (the threaded log callback). The first line is cut and scrubbed before anything
    /// reads it: machine paths, URL query strings and token-like runs are removed, because a game's
    /// own error line can carry a login URL or a session id.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayConsoleWatch
    {
        public const int FirstLineMax = 300;
        private static int _errors;
        private static string? _first;
        private static readonly object Gate = new object();

        static PlayConsoleWatch()
        {
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.ExitingEditMode) Reset();
            };
            Application.logMessageReceivedThreaded += OnLog;
        }

        public static int Errors { get { lock (Gate) return _errors; } }
        public static string? First { get { lock (Gate) return _first; } }

        internal static void Reset()
        {
            lock (Gate) { _errors = 0; _first = null; }
        }

        internal static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            // the kit's own lines are not the game's problem
            if (condition != null && condition.StartsWith("[RecorderKit]", StringComparison.Ordinal)) return;
            // nor are the EDITOR's (kit 0.9.1): an editor script's error — `Assets/Editor/…`, or code run
            // from the editor's own update/delayCall — is the project's tooling, not the game failing.
            // 0.9.0 reported RL's `[CjkFontBootstrap] source font missing` (an Assets/Editor/ script)
            // as "the first error" of a game that was loading fine.
            if (IsEditorCode(stackTrace)) return;
            // nor a PACKAGE's own (kit 0.9.2): an error whose every frame is in a package
            // (`Library/PackageCache/`, `Packages/`) was raised by that package, not by the game — RL's
            // 0.9.1 check led with the Unity MCP plugin's "Authorization failed". A package error the
            // GAME triggered has a game frame (`Assets/…`) in its trace and still counts.
            if (IsPackageOnly(stackTrace)) return;
            lock (Gate)
            {
                _errors++;
                if (_first == null) _first = Scrub(condition ?? "");
            }
        }

        internal static bool IsEditorCode(string? stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return false;
            return stackTrace!.IndexOf("/Editor/", StringComparison.Ordinal) >= 0
                   || stackTrace.IndexOf("UnityEditor.EditorApplication:Internal_CallDelayFunctions", StringComparison.Ordinal) >= 0
                   || stackTrace.IndexOf("UnityEditor.EditorApplication:Internal_CallUpdateFunctions", StringComparison.Ordinal) >= 0;
        }

        internal static bool IsPackageOnly(string? stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return false;
            var sawPackage = false;
            foreach (var raw in stackTrace!.Split('\n'))
            {
                var at = raw.IndexOf("(at ", StringComparison.Ordinal);
                if (at < 0) continue;
                var where = raw.Substring(at + 4);
                if (where.IndexOf("Assets/", StringComparison.Ordinal) >= 0) return false;
                if (where.IndexOf("Library/PackageCache/", StringComparison.Ordinal) >= 0 || where.IndexOf("Packages/", StringComparison.Ordinal) >= 0)
                    sawPackage = true;
            }
            return sawPackage;
        }

        private static readonly System.Text.RegularExpressions.Regex RichTextTag =
            new System.Text.RegularExpressions.Regex(@"</?(color|b|i|size|material|quad)(=[^>]*)?>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        internal static string Scrub(string line)
        {
            // Unity rich-text tags (<color=…>, <b>) are console styling, not the message (kit 0.9.2)
            var s = RichTextTag.Replace(line, "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            try
            {
                var project = Path.GetDirectoryName(Application.dataPath);
                if (!string.IsNullOrEmpty(project)) s = s.Replace(project, "<project>");
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home)) s = s.Replace(home, "~");
            }
            catch (Exception) { }
            // a URL keeps its host and path (the useful part: WHICH server said 404), never its query
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(https?://[^\s?#]+)[?#][^\s]*", "$1?…");
            // long opaque runs — tokens, keys, ids — become a marker
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[A-Za-z0-9_\-]{32,}", "<redacted>");
            return s.Length <= FirstLineMax ? s : s.Substring(0, FirstLineMax);
        }
    }

    /// <summary>Spec §8.10 — the branch and commit the project is on, read from `.git` as TEXT. Git is
    /// never run. Null when it cannot tell (not a repo, a detached/unknown layout).</summary>
    public static class GitHead
    {
        public static (string? branch, string? commit) Read(string projectRoot)
        {
            try
            {
                var gitDir = Path.Combine(projectRoot, ".git");
                if (File.Exists(gitDir))
                {
                    // a worktree or submodule: `gitdir: <path>`
                    var line = File.ReadAllText(gitDir).Trim();
                    if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) return (null, null);
                    var p = line.Substring("gitdir:".Length).Trim();
                    gitDir = Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(projectRoot, p));
                }
                if (!Directory.Exists(gitDir)) return (null, null);
                var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
                if (!head.StartsWith("ref:", StringComparison.Ordinal))
                    return (null, IsSha(head) ? head : null);   // detached
                var reference = head.Substring(4).Trim();
                var branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference.Substring(11) : reference;
                return (branch, ResolveRef(gitDir, reference));
            }
            catch (Exception) { return (null, null); }
        }

        private static string? ResolveRef(string gitDir, string reference)
        {
            foreach (var dir in new[] { gitDir, CommonDir(gitDir) })
            {
                if (dir == null) continue;
                var loose = Path.Combine(dir, reference.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(loose))
                {
                    var sha = File.ReadAllText(loose).Trim();
                    if (IsSha(sha)) return sha;
                }
                var packed = Path.Combine(dir, "packed-refs");
                if (File.Exists(packed))
                    foreach (var l in File.ReadAllLines(packed))
                    {
                        var parts = l.Split(' ');
                        if (parts.Length == 2 && parts[1] == reference && IsSha(parts[0])) return parts[0];
                    }
            }
            return null;
        }

        private static string? CommonDir(string gitDir)
        {
            var f = Path.Combine(gitDir, "commondir");
            if (!File.Exists(f)) return null;
            var p = File.ReadAllText(f).Trim();
            return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(gitDir, p));
        }

        private static bool IsSha(string s)
        {
            if (s.Length != 40 && s.Length != 64) return false;
            foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }
    }
}
