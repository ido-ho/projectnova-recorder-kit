using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Production wiring: one RelayServer + RelayStatus per domain, pumping from
    /// EditorApplication.update (macOS FileSystemWatcher is unreliable in the Editor; we poll),
    /// throttled to 4 scans/second. Disabled in batch mode so headless test runs don't spin a relay.
    /// </summary>
    [InitializeOnLoad]
    internal static class RelayBoot
    {
        internal static RelayServer? Server;
        private static double _nextScan;

        static RelayBoot()
        {
            if (Application.isBatchMode)
                return;
            var projectRoot = Directory.GetCurrentDirectory();
            var env = new RelayEnv
            {
                StartShot = (shot, bindings) => AdDirector.Run(AdapterRegistry.Current, new[] { shot },
                    new AdDirector.Options { Bindings = bindings }),
                // Zero shots = run the ready gate (and its one recovery retry) and stop.
                StartReady = () => AdDirector.Run(AdapterRegistry.Current, System.Array.Empty<AdShot>()),
            };
            Server = new RelayServer(projectRoot, env);
            new RelayStatus(projectRoot).Attach();
            KeepPlayingWhileUnfocused();
            EditorApplication.update += () =>
            {
                if (EditorApplication.timeSinceStartup < _nextScan || Server == null)
                    return;
                _nextScan = EditorApplication.timeSinceStartup + 0.25;
                Server.PumpOnce();
            };
        }

        /// <summary>
        /// Force Application.runInBackground on every Play Mode entry.
        ///
        /// LOAD-BEARING, and it took a third game to notice: with runInBackground false — the Unity
        /// DEFAULT for many templates — the player stops updating and rendering the moment the editor
        /// loses focus. An agent driving this relay leaves the editor unfocused BY DEFINITION, so the
        /// whole pipeline silently half-works: clicks are delivered but never processed, and
        /// ScreenCapture never completes, surfacing only as "screenshot never appeared (timeout)". Both
        /// look exactly like adapter bugs, which is the expensive part.
        ///
        /// The runtime property is set rather than PlayerSettings because PlayerSettings.runInBackground
        /// is the BUILD setting, read at play start — setting it mid-session changes nothing, which is
        /// its own confusing dead end. The project setting is reported in status.json instead, so
        /// onboarding can decide whether to commit it for future sessions; this keeps the fix
        /// session-scoped and writes nothing to the studio's repo.
        ///
        /// The first two games onboarded both happened to have it enabled, which is exactly why no
        /// amount of work on them could have surfaced this.
        /// </summary>
        private static void KeepPlayingWhileUnfocused()
        {
            if (!Application.runInBackground)
                Application.runInBackground = true;

            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredPlayMode)
                    return;
                if (!Application.runInBackground)
                    Application.runInBackground = true;
                // Leftover camera-hold.json from an aborted run: follow never writes
                // rotation, so the next session would restore the stale snapshot.
                // Armed holds are left alone (stills stay posed).
                try
                {
                    var sweep = CameraPose.SweepOrphaned(
                        CameraPose.Host.ForProject(Directory.GetCurrentDirectory()));
                    if (!sweep.Ok)
                        Debug.LogWarning($"[RecorderKit] leftover camera-hold: {sweep.Error}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[RecorderKit] leftover camera-hold sweep threw: {e.Message}");
                }
            };
        }
    }
}
