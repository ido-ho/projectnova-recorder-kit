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
            var projectRoot = Directory.GetCurrentDirectory();
            var loaded = AdapterJson.Load(projectRoot);
            var ui = new UguiDriver();
            AdapterRegistry.Current = new GameAdapter
            {
                GameId = loaded.GameId ?? "unregistered",
                OverlayTypeNames = loaded.OverlayTypeNames,
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
