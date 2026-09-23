using System.IO;
using UnityEditor;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Registers a generic adapter when the studio has not authored one. Runs on delayCall so a
    /// game-specific [InitializeOnLoad] adapter always wins if it set AdapterRegistry.Current.
    /// Writes nothing to Assets/; shots (if any) are re-read from Library/Nova/shots.json.
    /// </summary>
    [InitializeOnLoad]
    internal static class DefaultAdapterBoot
    {
        static DefaultAdapterBoot()
        {
            if (Application.isBatchMode)
                return;
            // Sync first: entering Play Mode reloads the domain and a run-shot can arrive
            // before delayCall. A game-specific [InitializeOnLoad] that already set Current
            // wins (IsRegistered). One that runs after us overwrites Current — also fine.
            RegisterIfNeeded();
            EditorApplication.delayCall += RegisterIfNeeded;
        }

        internal static void RegisterIfNeeded()
        {
            if (AdapterRegistry.IsRegistered)
                return;
            AdapterRegistry.Current = BuildForThisProject();
        }

        /// <summary>The generic adapter for the project this Editor has open — one resolver for the
        /// whole kit (audit M4), never the process's working directory, which anything can move
        /// under us.</summary>
        internal static GameAdapter BuildForThisProject() => BuildDefault(KitProject.Root());

        /// <summary>
        /// The generic adapter over one project folder. Separated from the registration so the
        /// EditMode tests can look at what it carries without touching the live
        /// <see cref="AdapterRegistry"/>.
        ///
        /// IT CACHES NO OVERLAY NAMES (second audit, S1). It used to copy adapter.json's
        /// <c>overlayTypeNames</c> into <see cref="GameAdapter.OverlayTypeNames"/> once, here, at
        /// registration — and the director falls back to that field whenever the adapter.json on
        /// disk lists none, while the lever gate only gated names that adapter.json lists TODAY.
        /// A type the cloud delivered yesterday was therefore disabled in the running game with
        /// nothing ticked, after any domain reload and a second send. The director re-reads
        /// adapter.json on every run, so the cached copy was never anything but the stale answer.
        /// </summary>
        internal static GameAdapter BuildDefault(string projectRoot)
        {
            var loaded = AdapterJson.Load(projectRoot);
            var ui = new UguiDriver();
            return new GameAdapter
            {
                GameId = loaded.GameId ?? "unregistered",
                IsDefaultAdapter = true,
                CheatBridge = new GenericCheatBridge(projectRoot, ui),
                Ui = ui,
                ReadyGate = new HygieneReadyGate(
                    () => HygieneSpec.Load(projectRoot),
                    () => Time.timeScale,
                    v => Time.timeScale = v,
                    () => ReadLastProbe(projectRoot)),
                Shots = () => JsonShotLoader.LoadFromPath(RelayPaths.NovaShotsFile(projectRoot)).Shots,
            };
        }

        internal static string? ReadGameId(string projectRoot) =>
            AdapterJson.Load(projectRoot).GameId;

        internal static string? ReadLastProbe(string projectRoot)
        {
            var path = Path.Combine(RelayPaths.Root(projectRoot), "probe", "last.txt");
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
