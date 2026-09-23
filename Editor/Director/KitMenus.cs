using UnityEditor;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    public static class KitMenus
    {
        [MenuItem("Tools/Recorder Kit/Run All Shots")]
        public static void RunAllShots()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("[RecorderKit] Enter Play Mode before running.");
                return;
            }
            if (!AdapterRegistry.IsRegistered)
            {
                Debug.LogError("[RecorderKit] No GameAdapter registered — is the game's adapter on this branch?");
                return;
            }
            // Menu runs are fully autonomous — no agent in the loop to answer vision requests.
            AdDirector.Run(AdapterRegistry.Current, AdapterRegistry.Current.Shots(),
                new AdDirector.Options { SkipVisionShots = true });
        }

        [MenuItem("Tools/Recorder Kit/Stop")]
        public static void Stop() => AdDirector.Active?.Finish("stopped by user");
    }
}
